using System.Collections;
using System.Reflection;
using ClosedXML.Excel;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Data;

namespace WorkTimeTracker.App.HeadlessTests;

public sealed class ApplicationUsageTests
{
    [Fact]
    public void PackageUpdatesKeepTheSameApplicationIdentity()
    {
        var type = typeof(TrackingService).Assembly.GetType("WorkTimeTracker.App.DashboardReport", true)!;
        var key = type.GetMethod("AppKey", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Identity(string path) => (string)key.Invoke(null, [new WindowInfo("one", "app", "App", path, "Title")])!;
        var first = Identity(@"C:\Program Files\WindowsApps\Example_1.2.3.4_x64__publisher\App\app.exe");
        Assert.Equal(first, Identity(@"C:\Program Files\WindowsApps\Example_2.0.0.0_x64__publisher\App\app.exe"));
        Assert.NotEqual(first, Identity(@"C:\Program Files\WindowsApps\Example_2.0.0.0_x64__other\App\app.exe"));
    }

    [Fact]
    public void DocumentsAcrossMonthsUseCalculatedTimesAndActivePercentages()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "usage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "work.sqlite3");
            var first = new DateTimeOffset(2026, 1, 31, 10, 0, 0, TimeSpan.Zero);
            using (var repository = new SqliteWorkTimeRepository(path))
            {
                for (var i = 0; i < 2; i++)
                {
                    var start = first.AddDays(i);
                    var day = repository.CreateWorkDay(new WorkDay(DateOnly.FromDateTime(start.Date), start,
                        start.AddMinutes(2), WorkDayEndReason.Manual), start);
                    var title = i == 0 ? "Report.docx - Word" : "Budget.docx - Word";
                    var window = new WindowInfo("word", "winword", "Word", null, title);
                    repository.WriteBatch(day.Id, new RawSignalBatch
                    {
                        Windows = [window],
                        VisibilitySegments = [new VisibilitySegment(window.Id, start, start.AddMinutes(2), 100, true)],
                        Interactions = [new InteractionSecond(window.Id, start, InteractionKind.Keyboard),
                            new InteractionSecond(window.Id, start.AddSeconds(30), InteractionKind.Keyboard)],
                        GlobalInputs = [new GlobalInputObservation(start, start), new GlobalInputObservation(start.AddSeconds(30), start.AddSeconds(30))]
                    });
                }
            }
            var assembly = typeof(TrackingService).Assembly;
            var reportType = assembly.GetType("WorkTimeTracker.App.DashboardReport", true)!;
            var groupingType = assembly.GetType("WorkTimeTracker.App.DashboardGrouping", true)!;
            var report = reportType.GetMethod("Load")!.Invoke(null, [path, new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 1), new AppSettings(), Enum.ToObject(groupingType, 0)])!;
            var usageType = assembly.GetType("WorkTimeTracker.App.ApplicationUsageReport", true)!;
            var usage = usageType.GetMethod("Create")!.Invoke(null, [report, new AppSettings(), "|winword"])!;
            Assert.Equal(2, usageType.GetProperty("DaysUsed")!.GetValue(usage));
            var documents = ((IEnumerable)usageType.GetProperty("Documents")!.GetValue(usage)!).Cast<object>().ToArray();
            Assert.Equal(2, documents.Length);
            Assert.Equal(new[] { "Budget.docx", "Report.docx" }, documents.Select(x => (string)x.GetType().GetProperty("Label")!.GetValue(x)!).Order().ToArray());
            foreach (var document in documents)
            {
                Assert.Equal(50d, (double)document.GetType().GetProperty("Percent")!.GetValue(document)!, 6);
                Assert.Equal(TimeSpan.FromSeconds(31), document.GetType().GetProperty("Active")!.GetValue(document));
                Assert.Equal(1, document.GetType().GetProperty("DaysUsed")!.GetValue(document));
            }
            Assert.NotNull(usageType.GetProperty("FirstUsed")!.GetValue(usage));
            Assert.NotNull(usageType.GetProperty("LastUsed")!.GetValue(usage));
            var catalogType = assembly.GetType("WorkTimeTracker.App.ApplicationCatalog", true)!;
            var choices = ((IEnumerable)catalogType.GetMethod("Load")!.Invoke(null, [path, new AppSettings()])!).Cast<object>().ToArray();
            var choice = Assert.Single(choices);
            var matches = choice.GetType().GetMethod("Matches")!;
            Assert.True((bool)matches.Invoke(choice, ["Word"])!);
            Assert.True((bool)matches.Invoke(choice, ["Budget.docx"])!);
            Assert.True((bool)matches.Invoke(choice, ["Report.docx - Word"])!);
            Assert.False((bool)matches.Invoke(choice, ["not present"])!);
            var export = assembly.GetType("WorkTimeTracker.App.ApplicationUsageExport", true)!.GetMethod("Write")!;
            var typedDocuments = usageType.GetProperty("Documents")!.GetValue(usage)!;
            var typedDays = usageType.GetProperty("Days")!.GetValue(usage)!;
            var csv = Path.Combine(directory, "usage.csv");
            var xlsx = Path.Combine(directory, "usage.xlsx");
            export.Invoke(null, [csv, "Word", report, usage, typedDocuments, typedDays, null]);
            export.Invoke(null, [xlsx, "Word", report, usage, typedDocuments, typedDays, null]);
            Assert.Contains("Report.docx", File.ReadAllText(csv));
            using var workbook = new XLWorkbook(xlsx);
            Assert.Contains(workbook.Worksheet(1).RowsUsed(), row => row.CellsUsed().Any(cell => cell.GetString().Contains("Budget.docx")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
