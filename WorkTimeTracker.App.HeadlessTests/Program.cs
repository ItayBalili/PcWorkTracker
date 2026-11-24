using WorkTimeTracker.App;
using WorkTimeTracker.Data;

namespace WorkTimeTracker.App.HeadlessTests;

internal static class Program
{
    // dotnet run --project WorkTimeTracker.App.HeadlessTests -- --record 8
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--fixture") return RunFixture();
        // dotnet run --project WorkTimeTracker.App.HeadlessTests -- --report <tracking.sqlite3> <title filter>
        if (args.Length == 3 && args[0] == "--report") return Report(args[1], args[2]);
        // dotnet run --project WorkTimeTracker.App.HeadlessTests -- --export <tracking.sqlite3> <output folder>
        if (args.Length == 3 && args[0] == "--export") return Export(args[1], args[2]);
        if (args.Length == 2 && args[0] is "--crash-record" or "--crash-record-paused")
        {
            var crashPath = Path.GetFullPath(args[1]);
            if (!crashPath.StartsWith(Environment.CurrentDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return 2;
            var tracker = new TrackingService(new() { DatabasePath = crashPath });
            await tracker.StartAsync();
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (args[0] == "--crash-record-paused") await tracker.PauseAsync();
            await tracker.FlushAsync();
            // Deliberately skip Dispose/Stop to verify recovery from a process termination.
            Environment.Exit(73);
        }
        if (args.Length != 2 || args[0] != "--record" || !int.TryParse(args[1], out var seconds) || seconds < 3)
        {
            Console.Error.WriteLine("Use --record <seconds >= 3> on an interactive Windows desktop.");
            return 2;
        }
        var directory = Path.Combine(Environment.CurrentDirectory, "WorkTimeTracker.App.HeadlessTests", "artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "recording.sqlite3");
        try
        {
            await using (var tracker = new TrackingService(new() { DatabasePath = path }))
            {
                tracker.Error += e => Console.Error.WriteLine(e);
                await tracker.StartAsync();
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(seconds)), tracker.Completion);
                await tracker.StopAsync();
                Console.WriteLine($"Rejected input seconds={tracker.RejectedInteractions}");
            }
            using var repository = new SqliteWorkTimeRepository(path);
            var day = repository.ListWorkDays().Single();
            var raw = repository.GetRawData(day.Id);
            Console.WriteLine($"UTC recording: {raw.TrackingStartedUtc:O}; flush: {repository.GetRecoveryState().LastFlushedUtc:O}");
            Console.WriteLine($"Windows={raw.Windows.Count}; segments={raw.VisibilitySegments.Count}; input seconds={raw.Interactions.Count}; session events={raw.SessionEvents.Count}");
            if (raw.VisibilitySegments.Count == 0 || raw.Windows.Count == 0 || raw.SessionEvents.Count < 2)
                throw new InvalidOperationException("No real eligible windows/session lifecycle were captured. Use an interactive desktop.");
            Console.WriteLine($"Observed segment range: {raw.VisibilitySegments.Min(s => s.StartUtc):O} .. {raw.VisibilitySegments.Max(s => s.EndUtc):O}");
            foreach (var group in raw.Interactions.GroupBy(i => raw.Windows.First(w => w.Id == i.WindowId).ProcessName))
                Console.WriteLine($"  input {group.Key}: {group.Count()} s");
            var report = WorkTimeTracker.Core.Calculation.CalculationEngine.Calculate(raw, new WorkTimeTracker.Core.Models.TrackerSettings(), DateTimeOffset.UtcNow);
            foreach (var w in report.Windows.Where(w => w.ActiveTime > TimeSpan.Zero || w.DisplayedTime > TimeSpan.Zero))
                Console.WriteLine($"  {w.Window.ProcessName} '{w.Window.Title}': active={w.ActiveTime:mm\\:ss} displayed={w.DisplayedTime:mm\\:ss}");
            return 0;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int Report(string source, string titleFilter)
    {
        // Work on a snapshot copy so the running app's database is never modified.
        var directory = Path.Combine(Path.GetTempPath(), "wtt-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "tracking.sqlite3");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(source + suffix)) File.Copy(source + suffix, path + suffix);
            using var repository = new SqliteWorkTimeRepository(path);
            var day = repository.ListWorkDays().OrderBy(d => d.WorkDay.StartUtc).Last();
            var raw = repository.GetRawData(day.Id);
            var report = WorkTimeTracker.Core.Calculation.CalculationEngine.Calculate(raw, new WorkTimeTracker.Core.Models.TrackerSettings(), DateTimeOffset.UtcNow);
            Console.WriteLine($"Day {day.WorkDay.Date} start {day.WorkDay.StartUtc.ToLocalTime():HH:mm:ss} active total {report.TotalActiveTime:h\\:mm\\:ss}");
            foreach (var w in report.Windows.Where(w => w.Window.Title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase)))
            {
                var seconds = raw.Interactions.Where(i => i.WindowId == w.Window.Id).Select(i => i.SecondUtc).OrderBy(s => s).ToList();
                Console.WriteLine($"{w.Window.ProcessName} '{w.Window.Title}': active={w.ActiveTime:h\\:mm\\:ss} displayed={w.DisplayedTime:h\\:mm\\:ss} " +
                    $"input-seconds={seconds.Count} first={seconds.FirstOrDefault().ToLocalTime():HH:mm:ss} last={seconds.LastOrDefault().ToLocalTime():HH:mm:ss}");
            }
            return 0;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int Export(string source, string output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wtt-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(output);
        try
        {
            var path = Path.Combine(directory, "tracking.sqlite3");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(source + suffix)) File.Copy(source + suffix, path + suffix);
            var assembly = typeof(TrackingService).Assembly;
            var reportType = assembly.GetType("WorkTimeTracker.App.DashboardReport", true)!;
            var groupingType = assembly.GetType("WorkTimeTracker.App.DashboardGrouping", true)!;
            var write = assembly.GetType("WorkTimeTracker.App.ReportExport", true)!.GetMethod("Write", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var grouping in Enum.GetValues(groupingType))
            {
                var report = reportType.GetMethod("Load")!.Invoke(null, [path, today, today, new AppSettings(), grouping])!;
                foreach (var extension in new[] { ".xlsx", ".csv" })
                {
                    var file = Path.Combine(output, $"export-{grouping}{extension}");
                    write.Invoke(null, [file, report]);
                    Console.WriteLine($"{file} {new FileInfo(file).Length} bytes");
                    if (extension != ".xlsx") continue;
                    using var book = new ClosedXML.Excel.XLWorkbook(file);
                    var sheet = book.Worksheets.First();
                    Console.WriteLine($"  sheet '{sheet.Name}' RTL={sheet.RightToLeft} rows={sheet.RowsUsed().Count()}");
                    foreach (var row in sheet.RowsUsed().Where(r => r.RowNumber() == 1 || r.Cells(5, 7).Any(c => c.GetString().Contains("notepad", StringComparison.OrdinalIgnoreCase))).Take(4))
                        Console.WriteLine("  " + string.Join(" | ", Enumerable.Range(1, 10).Select(c => row.Cell(c).GetString())));
                }
            }
            return 0;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int RunFixture()
    {
        var thread = new Thread(() =>
        {
            using var form = new NonActivatingForm
            {
                Text = "WorkTimeTracker disposable fixture", TopMost = true,
                StartPosition = FormStartPosition.Manual, Location = new(25, 25), Size = new(260, 140),
                ShowInTaskbar = true
            };
            using var popup = new NonActivatingForm
            {
                Text = "WorkTimeTracker unsampled owned fixture", TopMost = true,
                StartPosition = FormStartPosition.Manual, Location = new(300, 25), Size = new(240, 140),
                ShowInTaskbar = false
            };
            form.Shown += (_, _) =>
            {
                popup.Show(form);
                Console.WriteLine($"READY {form.Handle} {popup.Handle}");
                Console.Out.Flush();
                _ = Task.Run(() =>
                {
                    string? command;
                    while ((command = Console.ReadLine()) != null)
                    {
                        var current = command;
                        if (form.IsDisposed) break;
                        form.BeginInvoke(() =>
                        {
                            if (current == "rename") form.Text = "WorkTimeTracker renamed fixture";
                            if (current == "exit") form.Close();
                        });
                    }
                });
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return 0;
    }

    private sealed class NonActivatingForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
}
