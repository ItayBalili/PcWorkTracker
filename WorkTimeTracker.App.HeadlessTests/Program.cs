using WorkTimeTracker.App;
using WorkTimeTracker.Data;

namespace WorkTimeTracker.App.HeadlessTests;

internal static class Program
{
    // dotnet run --project WorkTimeTracker.App.HeadlessTests -- --record 8
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--fixture") return RunFixture();
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
            }
            using var repository = new SqliteWorkTimeRepository(path);
            var day = repository.ListWorkDays().Single();
            var raw = repository.GetRawData(day.Id);
            Console.WriteLine($"UTC recording: {raw.TrackingStartedUtc:O}; flush: {repository.GetRecoveryState().LastFlushedUtc:O}");
            Console.WriteLine($"Windows={raw.Windows.Count}; segments={raw.VisibilitySegments.Count}; input seconds={raw.Interactions.Count}; session events={raw.SessionEvents.Count}");
            if (raw.VisibilitySegments.Count == 0 || raw.Windows.Count == 0 || raw.SessionEvents.Count < 2)
                throw new InvalidOperationException("No real eligible windows/session lifecycle were captured. Use an interactive desktop.");
            Console.WriteLine($"Observed segment range: {raw.VisibilitySegments.Min(s => s.StartUtc):O} .. {raw.VisibilitySegments.Max(s => s.EndUtc):O}");
            return 0;
        }
        finally
        {
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
