using System.Reflection;
using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Data;

namespace WorkTimeTracker.App.HeadlessTests;

public sealed class DashboardReportTests
{
    [Fact]
    public void SameDateSessionsAndPowerGapAppearInReportAndExport()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "dashboard-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "work.sqlite3");
            var start = new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.Zero);
            var date = new DateOnly(2025, 1, 1);
            long firstId;
            using (var repository = new SqliteWorkTimeRepository(path))
            {
                firstId = repository.CreateWorkDay(new WorkDay(date, start, start.AddHours(1), WorkDayEndReason.Auto4Hours), start).Id;
                repository.CreateWorkDay(new WorkDay(date, start.AddHours(6), start.AddHours(7), WorkDayEndReason.Manual), start.AddHours(6));
            }
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE TrackingLifecycle (Id INTEGER PRIMARY KEY, WorkDayId INTEGER, TimeUtc INTEGER NOT NULL, Kind TEXT NOT NULL, EndUtc INTEGER); INSERT INTO TrackingLifecycle (WorkDayId, TimeUtc, Kind, EndUtc) VALUES ($id, $start, 'PoweredOff', $end);";
                command.Parameters.AddWithValue("$id", firstId);
                command.Parameters.AddWithValue("$start", start.AddHours(1).UtcTicks);
                command.Parameters.AddWithValue("$end", start.AddHours(6).UtcTicks);
                command.ExecuteNonQuery();
            }
            var assembly = typeof(TrackingService).Assembly;
            var reportType = assembly.GetType("WorkTimeTracker.App.DashboardReport", true)!;
            var groupingType = assembly.GetType("WorkTimeTracker.App.DashboardGrouping", true)!;
            var report = reportType.GetMethod("Load")!.Invoke(null, [path, date, date, new AppSettings(), Enum.ToObject(groupingType, 0)])!;
            var reports = ((System.Collections.IEnumerable)reportType.GetProperty("Reports")!.GetValue(report)!).Cast<DayReport>().ToArray();
            Assert.Equal(2, reports.Length);
            Assert.Equal(TimeSpan.FromHours(2), reports.Aggregate(TimeSpan.Zero, (sum, x) => sum + x.TotalSpan));
            var day = ((System.Collections.IEnumerable)reportType.GetProperty("Days")!.GetValue(report)!).Cast<object>().First();
            Assert.Equal(2, ((System.Collections.IEnumerable)day.GetType().GetProperty("Sessions")!.GetValue(day)!).Cast<object>().Count());
            var timeline = ((System.Collections.IEnumerable)reportType.GetProperty("Timeline")!.GetValue(report)!).Cast<object>().ToArray();
            var off = Assert.Single(timeline, x => (bool)x.GetType().GetProperty("IsPoweredOff")!.GetValue(x)!);
            Assert.Equal(start.AddHours(1), off.GetType().GetProperty("Start")!.GetValue(off));
            Assert.Equal(start.AddHours(6), off.GetType().GetProperty("End")!.GetValue(off));
            var csv = Path.Combine(directory, "sessions.csv");
            assembly.GetType("WorkTimeTracker.App.ReportExport", true)!.GetMethod("Write")!.Invoke(null, [csv, report]);
            var text = File.ReadAllText(csv);
            var resources = new System.Resources.ResourceManager("WorkTimeTracker.App.DashboardStrings", assembly);
            Assert.Contains(resources.GetString("PoweredOff", CultureInfo.CurrentCulture)!, text);
            Assert.Contains(resources.GetString("Sessions", CultureInfo.CurrentCulture)!, text);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void NormalizedTitlesGroupRetroactivelyAndExportTogether()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "dashboard-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "work.sqlite3");
            var start = new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.Zero);
            var day = new WorkDay(new DateOnly(2025, 1, 1), start, start.AddMinutes(6), WorkDayEndReason.Manual);
            var titles = new[] { "(4) Facebook - Google Chrome", "(5) Facebook - Google Chrome", "Inbox (12) - Gmail", "Inbox (3) - Gmail", "*file.txt", "file.txt" };
            using (var repository = new SqliteWorkTimeRepository(path))
            {
                var id = repository.CreateWorkDay(day, start).Id;
                var windows = titles.Select((title, i) => new WindowInfo(i.ToString(), i < 4 ? "chrome" : "notepad",
                    i < 4 ? "Chrome" : "Notepad", null, title)).ToArray();
                repository.WriteBatch(id, new RawSignalBatch
                {
                    Windows = windows,
                    VisibilitySegments = windows.Select((window, i) => new VisibilitySegment(window.Id, start.AddMinutes(i), start.AddMinutes(i + 1), 100, true)).ToArray(),
                    Interactions = windows.Select((window, i) => new InteractionSecond(window.Id, start.AddMinutes(i), InteractionKind.Keyboard)).ToArray()
                });
            }
            var assembly = typeof(TrackingService).Assembly;
            var reportType = assembly.GetType("WorkTimeTracker.App.DashboardReport", true)!;
            var groupingType = assembly.GetType("WorkTimeTracker.App.DashboardGrouping", true)!;
            var grouping = Enum.ToObject(groupingType, 0);
            object Load(AppSettings settings) => reportType.GetMethod("Load")!.Invoke(null, [path, day.Date, day.Date, settings, grouping])!;
            var report = Load(new AppSettings());
            var apps = ((System.Collections.IEnumerable)reportType.GetProperty("Applications")!.GetValue(report)!).Cast<object>().ToArray();
            var groupedTitles = apps.SelectMany(app => ((System.Collections.IEnumerable)app.GetType().GetProperty("Titles")!.GetValue(app)!).Cast<object>())
                .Select(title => (string)title.GetType().GetProperty("Name")!.GetValue(title)!).ToArray();
            Assert.Equal(3, groupedTitles.Length);
            Assert.Contains("Facebook - Google Chrome", groupedTitles);
            Assert.Contains("Inbox - Gmail", groupedTitles);
            Assert.Contains("file.txt", groupedTitles);
            var csv = Path.Combine(directory, "report.csv");
            assembly.GetType("WorkTimeTracker.App.ReportExport", true)!.GetMethod("Write")!.Invoke(null, [csv, report]);
            var text = File.ReadAllText(csv);
            Assert.Contains("Facebook - Google Chrome", text);
            Assert.DoesNotContain("(4) Facebook", text);
            var disabled = new AppSettings { TitleNormalizationRules = WorkTimeTracker.Core.Labels.TitleNormalizer.DefaultRules.Select(r => r with { Enabled = false }).ToArray() };
            var unnormalized = Load(disabled);
            var rawApps = ((System.Collections.IEnumerable)reportType.GetProperty("Applications")!.GetValue(unnormalized)!).Cast<object>().ToArray();
            Assert.Equal(6, rawApps.Sum(app => ((System.Collections.IEnumerable)app.GetType().GetProperty("Titles")!.GetValue(app)!).Cast<object>().Count()));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LiveReportUsesBufferedDataAndHighlightsForeground()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "dashboard-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "work.sqlite3");
            var now = DateTimeOffset.UtcNow;
            var start = now.AddSeconds(-20);
            var date = DateOnly.FromDateTime(start.LocalDateTime);
            var workDay = new WorkDay(date, start, null, WorkDayEndReason.Open);
            long id;
            using (var repository = new SqliteWorkTimeRepository(path))
                id = repository.CreateWorkDay(workDay, start).Id;
            var live = new WorkDayRawData(workDay, start,
                [new WindowInfo("one", "editor", "Editor", null, "Title")],
                [new VisibilitySegment("one", start, now, 100, true)],
                [new InteractionSecond("one", start, InteractionKind.Keyboard)], [])
            {
                GlobalInputs = [new GlobalInputObservation(now, now.AddMilliseconds(-100))]
            };
            var assembly = typeof(TrackingService).Assembly;
            var reportType = assembly.GetType("WorkTimeTracker.App.DashboardReport", true)!;
            var groupingType = assembly.GetType("WorkTimeTracker.App.DashboardGrouping", true)!;
            var report = reportType.GetMethod("LoadLive", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [path, date, date, new AppSettings(), Enum.ToObject(groupingType, 0), id, live, new Dictionary<long, IncrementalCalculationEngine>()])!;
            var calculated = Assert.Single(((System.Collections.IEnumerable)reportType.GetProperty("Reports")!.GetValue(report)!).Cast<DayReport>());
            Assert.True(calculated.TotalActiveTime > TimeSpan.FromSeconds(15));
            var app = Assert.Single(((System.Collections.IEnumerable)reportType.GetProperty("Applications")!.GetValue(report)!).Cast<object>());
            Assert.True((bool)app.GetType().GetProperty("IsForeground")!.GetValue(app)!);
            using var check = new SqliteWorkTimeRepository(path);
            Assert.Empty(check.GetRawData(id).Windows);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ReportsAndExportsUseEditedBoundariesWithoutChangingRawDay()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "dashboard-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "work.sqlite3");
            var start = new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.Zero);
            var original = new WorkDay(new DateOnly(2025, 1, 1), start, start.AddMinutes(10), WorkDayEndReason.Manual);
            using (var repository = new SqliteWorkTimeRepository(path))
            {
                var day = repository.CreateWorkDay(original, start);
                repository.SaveWorkDayEdit(new(day.Id, start.AddMinutes(2), start.AddMinutes(7)));
                Assert.Equal(original, repository.GetRawData(day.Id).WorkDay);
            }
            var assembly = typeof(TrackingService).Assembly;
            var reportType = assembly.GetType("WorkTimeTracker.App.DashboardReport", true)!;
            var groupingType = assembly.GetType("WorkTimeTracker.App.DashboardGrouping", true)!;
            var report = reportType.GetMethod("Load")!.Invoke(null, [path, original.Date, original.Date, new AppSettings(), Enum.ToObject(groupingType, 0)])!;
            var calculated = Assert.Single(((System.Collections.IEnumerable)reportType.GetProperty("Reports")!.GetValue(report)!).Cast<DayReport>());
            Assert.Equal(start.AddMinutes(2), calculated.WorkDay.StartUtc);
            Assert.Equal(start.AddMinutes(7), calculated.WorkDay.EndUtc);
            Assert.Equal(TimeSpan.FromMinutes(5), calculated.TotalSpan);
            var write = assembly.GetType("WorkTimeTracker.App.ReportExport", true)!.GetMethod("Write")!;
            var csv = Path.Combine(directory, "report.csv");
            var xlsx = Path.Combine(directory, "report.xlsx");
            write.Invoke(null, [csv, report]);
            write.Invoke(null, [xlsx, report]);
            var expectedStart = start.AddMinutes(2).ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            var expectedEnd = start.AddMinutes(7).ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            Assert.Contains(expectedStart, File.ReadAllText(csv));
            Assert.Contains(expectedEnd, File.ReadAllText(csv));
            using var workbook = new XLWorkbook(xlsx);
            Assert.Contains(workbook.Worksheets.First().RowsUsed(), row => row.Cell(3).GetString() == expectedStart && row.Cell(4).GetString() == expectedEnd);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ZeroFilterAlsoRemovesZeroDurationChildren()
    {
        var assembly = typeof(TrackingService).Assembly;
        var titleType = assembly.GetType("WorkTimeTracker.App.DashboardTitle", true)!;
        var labelType = assembly.GetType("WorkTimeTracker.App.DashboardLabel", true)!;
        var appType = assembly.GetType("WorkTimeTracker.App.DashboardApplication", true)!;
        var titles = Array.CreateInstance(titleType, 2);
        titles.SetValue(Activator.CreateInstance(titleType, "zero", TimeSpan.FromSeconds(30), TimeSpan.Zero, "project"), 0);
        titles.SetValue(Activator.CreateInstance(titleType, "nonzero", TimeSpan.FromMinutes(2), TimeSpan.Zero, "project"), 1);
        var labels = Array.CreateInstance(labelType, 1);
        labels.SetValue(Activator.CreateInstance(labelType, "project", TimeSpan.FromMinutes(2), TimeSpan.Zero, titles), 0);
        var app = Activator.CreateInstance(appType, "editor", "Editor", null, TimeSpan.FromMinutes(2), TimeSpan.Zero,
            titles, null, System.Windows.Media.Brushes.CornflowerBlue, 1d)!;
        appType.GetProperty("Labels")!.SetValue(app, labels);
        var filter = appType.GetMethod("Filter")!;
        foreach (var showZero in new[] { false, true })
        {
            var filtered = filter.Invoke(app, ["", showZero])!;
            var filteredLabels = ((System.Collections.IEnumerable)appType.GetProperty("Labels")!.GetValue(filtered)!).Cast<object>().ToArray();
            var filteredTitles = ((System.Collections.IEnumerable)labelType.GetProperty("Titles")!.GetValue(Assert.Single(filteredLabels))!).Cast<object>().ToArray();
            Assert.Equal(showZero ? 2 : 1, filteredTitles.Length);
        }
    }

    [Fact]
    public void UnchangedNumberBoxValuePreservesOriginalTickPrecision()
    {
        var method = typeof(SettingsWindow).GetMethod("Duration", BindingFlags.Static | BindingFlags.NonPublic, null,
            [typeof(double?), typeof(TimeSpan), typeof(long), typeof(bool)], null)!;
        var original = TimeSpan.FromTicks(1234567890123);
        Assert.Equal(original, method.Invoke(null, [(double?)original.TotalMinutes, original, TimeSpan.TicksPerMinute, false]));
    }

    [Fact]
    public void PackageAppearanceUsesMatchingApplicationAndScaledIcon()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "package-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Assets"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "AppxManifest.xml"),
                """<Package xmlns:uap="urn:visual"><Applications><Application Executable="Other.exe"><uap:VisualElements DisplayName="Other"/></Application><Application Executable="App\Editor.exe"><uap:VisualElements DisplayName="עורך" Square44x44Logo="Assets\Icon.png"/></Application></Applications></Package>""");
            File.WriteAllBytes(Path.Combine(directory, "Assets", "Icon.scale-100.png"),
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lE8AAAAASUVORK5CYII="));
            var type = typeof(TrackingService).Assembly.GetType("WorkTimeTracker.App.PackageAppearance", true)!;
            var read = type.GetMethod("ReadPackage", BindingFlags.Static | BindingFlags.NonPublic)!;
            var result = read.Invoke(null, [directory, "Package", @"App\Editor.exe"])!;
            Assert.Equal("עורך", type.GetProperty("Name")!.GetValue(result));
            Assert.NotNull(type.GetProperty("Icon")!.GetValue(result));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void PeriodReportLoadsPersistedPauseEvents()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "dashboard-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var db = Path.Combine(directory, "work.sqlite3");
            var start = new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.Zero);
            var day = new WorkDay(new DateOnly(2025, 1, 1), start, start.AddMinutes(2), WorkDayEndReason.Manual);
            long id;
            using (var repo = new SqliteWorkTimeRepository(db))
            {
                id = repo.CreateWorkDay(day, start).Id;
                repo.WriteBatch(id, new RawSignalBatch
                {
                    Windows = [new WindowInfo("one", "editor", "Editor", null, "Title")],
                    VisibilitySegments = [new VisibilitySegment("one", start, start.AddMinutes(2), 100, true)],
                    Interactions = [new InteractionSecond("one", start, InteractionKind.Keyboard)]
                });
            }
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE TrackingLifecycle (Id INTEGER PRIMARY KEY, WorkDayId INTEGER, TimeUtc INTEGER NOT NULL, Kind TEXT NOT NULL); INSERT INTO TrackingLifecycle (WorkDayId, TimeUtc, Kind) VALUES ($id, $time, 'Pause');";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$time", start.AddSeconds(30).UtcTicks);
                command.ExecuteNonQuery();
            }
            var reportType = typeof(TrackingService).Assembly.GetType("WorkTimeTracker.App.DashboardReport", true)!;
            var groupingType = typeof(TrackingService).Assembly.GetType("WorkTimeTracker.App.DashboardGrouping", true)!;
            var report = reportType.GetMethod("Load")!.Invoke(null, [db, day.Date, day.Date.AddDays(6), new AppSettings(), Enum.ToObject(groupingType, 0)])!;
            var dayRows = ((System.Collections.IEnumerable)reportType.GetProperty("Days")!.GetValue(report)!).Cast<object>().ToArray();
            Assert.Equal(8, dayRows.Length);
            Assert.Equal(6, dayRows.Count(row => !(bool)row.GetType().GetProperty("HasData")!.GetValue(row)!));
            Assert.True((bool)dayRows[^1].GetType().GetProperty("IsTotal")!.GetValue(dayRows[^1])!);
            var days = (System.Collections.IEnumerable)reportType.GetProperty("Reports")!.GetValue(report)!;
            var calculated = Assert.Single(days.Cast<DayReport>());
            Assert.Equal(TimeSpan.FromSeconds(1), calculated.TotalActiveTime);
            Assert.Equal(TimeSpan.FromSeconds(90), calculated.NotTrackedTime);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimelineMatchesEngineAcrossFractionalUnavailablePeriods(bool recovery)
    {
        var start = new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var raw = new WorkDayRawData(
            new WorkDay(new DateOnly(2025, 1, 1), start, start.AddSeconds(10), WorkDayEndReason.Manual), start,
            [new WindowInfo("one", "editor", "Editor", null, "Title")],
            [new VisibilitySegment("one", start, start.AddSeconds(10), 100, true)],
            [new InteractionSecond("one", start, InteractionKind.Keyboard), new InteractionSecond("one", start.AddSeconds(6), InteractionKind.Keyboard)], [])
        {
            TrackingEvents = recovery
                ? [new TrackingEvent(start.AddSeconds(2.5), TrackingEventKind.Recovery) { EndUtc = start.AddSeconds(5.5) }]
                : [new TrackingEvent(start.AddSeconds(2.5), TrackingEventKind.Pause), new TrackingEvent(start.AddSeconds(5.5), TrackingEventKind.Resume)]
        };
        AssertTimelineMatchesEngine(raw, start.AddSeconds(10));
    }

    [Fact]
    public void TimelineMatchesFractionalGlobalInputAndSessionEvents()
    {
        var start = new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var raw = new WorkDayRawData(
            new WorkDay(new DateOnly(2025, 1, 1), start, start.AddSeconds(10), WorkDayEndReason.Manual), start,
            [new WindowInfo("one", "editor", "Editor", null, "Title")],
            [new VisibilitySegment("one", start, start.AddSeconds(10), 100, true)], [],
            [new SessionEvent(start.AddSeconds(4.25), SessionEventKind.Lock), new SessionEvent(start.AddSeconds(5.75), SessionEventKind.Unlock)])
        {
            GlobalInputs = [new GlobalInputObservation(start.AddSeconds(1.25), start.AddSeconds(1.25))]
        };
        AssertTimelineMatchesEngine(raw, start.AddSeconds(10));
    }

    [Fact]
    public void TimelineMatchesFractionalForegroundTransitions()
    {
        var start = new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var raw = new WorkDayRawData(
            new WorkDay(new DateOnly(2025, 1, 1), start, start.AddSeconds(10), WorkDayEndReason.Manual), start,
            [new WindowInfo("one", "editor", "Editor", null, "Title")],
            [new VisibilitySegment("one", start.AddMilliseconds(250), start.AddSeconds(6.75), 100, true)],
            [new InteractionSecond("one", start, InteractionKind.Keyboard), new InteractionSecond("one", start.AddSeconds(8), InteractionKind.Keyboard)], []);
        AssertTimelineMatchesEngine(raw, start.AddSeconds(10));
    }

    private static void AssertTimelineMatchesEngine(WorkDayRawData raw, DateTimeOffset end)
    {
        var settings = new TrackerSettings();
        var calculated = CalculationEngine.Calculate(raw, settings, end);
        var method = typeof(TrackingService).Assembly.GetType("WorkTimeTracker.App.DashboardReport", true)!
            .GetMethod("BuildTimeline", BindingFlags.NonPublic | BindingFlags.Static)!;
        var entries = (System.Collections.IEnumerable)method.Invoke(null, [raw, settings, end, WorkTimeTracker.Core.Labels.LabelExtractor.DefaultRules, null])!;
        var active = TimeSpan.Zero;
        var away = TimeSpan.Zero;
        foreach (var item in entries)
        {
            var type = item!.GetType();
            var duration = (DateTimeOffset)type.GetProperty("End")!.GetValue(item)! - (DateTimeOffset)type.GetProperty("Start")!.GetValue(item)!;
            if ((bool)type.GetProperty("IsUntracked")!.GetValue(item)!) continue;
            if (((string)type.GetProperty("Label")!.GetValue(item)!).StartsWith("\u2068Editor\u2069", StringComparison.Ordinal)) active += duration;
            else away += duration;
        }
        Assert.Equal(calculated.TotalActiveTime, active);
        Assert.Equal(calculated.AwayTime, away);
    }

    [Fact]
    public void DashboardXamlConstructsOnStaThread()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("he-IL");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("he-IL");
                var assembly = typeof(TrackingService).Assembly;
                var app = Activator.CreateInstance(assembly.GetType("WorkTimeTracker.App.App", true)!)!;
                app.GetType().GetMethod("InitializeComponent")!.Invoke(app, null);
                var wpfApp = (System.Windows.Application)app;
                Assert.Null(wpfApp.TryFindResource("CardBackgroundFillColorSecondaryBrush"));
                app.GetType().GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(app, new AppSettings { Theme = ThemePreference.Light });
                var ensureResources = app.GetType().GetMethod("EnsureUiResources", BindingFlags.Instance | BindingFlags.NonPublic)!;
                ensureResources.Invoke(app, null);
                var lightBackground = ((System.Windows.Media.SolidColorBrush)wpfApp.FindResource("ApplicationBackgroundBrush")).Color;
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                var darkBackground = ((System.Windows.Media.SolidColorBrush)wpfApp.FindResource("ApplicationBackgroundBrush")).Color;
                Assert.NotEqual(lightBackground, darkBackground);
                ensureResources.Invoke(app, null);
                Assert.Equal(2, wpfApp.Resources.MergedDictionaries.Count);
                Assert.NotNull(wpfApp.TryFindResource("CardBackgroundFillColorSecondaryBrush"));
                Assert.NotNull(wpfApp.TryFindResource("SubtleFillColorSecondaryBrush"));
                Assert.NotNull(wpfApp.TryFindResource("AccentTextFillColorPrimaryBrush"));
                Assert.NotNull(wpfApp.TryFindResource("AccentFillColorDefaultBrush"));
                Assert.NotNull(wpfApp.TryFindResource("TextOnAccentFillColorPrimaryBrush"));
                Assert.NotNull(wpfApp.TryFindResource("SystemFillColorSuccessBrush"));
                Assert.NotNull(wpfApp.TryFindResource("SystemFillColorCautionBrush"));
                Assert.NotNull(wpfApp.TryFindResource("SystemFillColorCriticalBrush"));
                Assert.NotNull(wpfApp.TryFindResource("ControlStrokeColorDefaultBrush"));
                var windowType = assembly.GetType("WorkTimeTracker.App.DashboardWindow", true)!;
                var window = Activator.CreateInstance(windowType,
                    (Func<TrackingService>)(() => throw new InvalidOperationException()),
                    (Func<AppSettings>)(() => new AppSettings()),
                    Path.Combine(Environment.CurrentDirectory, "unused-dashboard-smoke.sqlite3"))!;
                var button = windowType.GetField("DayAction", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var picker = windowType.GetField("DatePickerControl", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.Equal(DateTime.Today, picker.GetType().GetProperty("SelectedDate")!.GetValue(picker));
                var update = windowType.GetMethod("UpdateDayAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
                update.Invoke(window, [new TrackingStatus(TrackingState.DayEnded, null, null)]);
                Assert.Equal("התחלת יום עבודה", button.GetType().GetProperty("Content")!.GetValue(button));
                Assert.True((bool)button.GetType().GetProperty("IsEnabled")!.GetValue(button)!);
                var start = DateTimeOffset.UtcNow;
                update.Invoke(window, [new TrackingStatus(TrackingState.Tracking, 1, new WorkDay(DateOnly.FromDateTime(start.Date), start, null, WorkDayEndReason.Open))]);
                Assert.Equal("סיום יום עבודה", button.GetType().GetProperty("Content")!.GetValue(button));
                Assert.True((bool)button.GetType().GetProperty("IsEnabled")!.GetValue(button)!);
                VerifyLayout((System.Windows.Window)window, assembly, start);
                var dashboard = (DashboardWindow)window;
                Assert.Null(dashboard.FindName("MainNavigation"));
                var reportDayType = assembly.GetType("WorkTimeTracker.App.ReportDay", true)!;
                var reportDays = Array.CreateInstance(reportDayType, 1);
                var original = new StoredWorkDay(1, new WorkDay(DateOnly.FromDateTime(start.Date), start, start.AddMinutes(2), WorkDayEndReason.Manual), start);
                reportDays.SetValue(Activator.CreateInstance(reportDayType, original, null), 0);
                var editType = assembly.GetType("WorkTimeTracker.App.EditDayWindow", true)!;
                var editWindow = (System.Windows.Window)Activator.CreateInstance(editType, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [Path.Combine(Environment.CurrentDirectory, "unused-dashboard-smoke.sqlite3"), reportDays], null)!;
                Assert.True(((System.Windows.Controls.TextBox)editWindow.FindName("EndInput")).IsEnabled);
                editWindow.Close();
                VerifySettingsLayout();
                var settingsRequested = 0;
                dashboard.SettingsRequested += (_, _) => settingsRequested++;
                var settingsButton = (Wpf.Ui.Controls.Button)dashboard.FindName("SettingsButton")!;
                settingsButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(1, settingsRequested);
                windowType.GetMethod("ShowExportSuccess", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [Path.Combine(Environment.CurrentDirectory, "report.xlsx")]);
                var presenter = ((System.Windows.Window)window).FindName("SnackPresenter")!;
                var snackbar = Assert.IsType<Wpf.Ui.Controls.Snackbar>(((Wpf.Ui.Controls.SnackbarPresenter)presenter).Content);
                var notice = Assert.IsType<System.Windows.Controls.TextBlock>(snackbar.Content);
                var link = Assert.Single(notice.Inlines.OfType<System.Windows.Documents.Hyperlink>());
                Assert.Equal("פתיחת תיקיית הדוח", ((System.Windows.Documents.Run)link.Inlines.FirstInline).Text);
                windowType.GetMethod("Close")!.Invoke(window, null);
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
                var english = (System.Windows.Window)Activator.CreateInstance(windowType,
                    (Func<TrackingService>)(() => throw new InvalidOperationException()),
                    (Func<AppSettings>)(() => new AppSettings()),
                    Path.Combine(Environment.CurrentDirectory, "unused-dashboard-smoke.sqlite3"))!;
                Assert.Equal(System.Windows.FlowDirection.LeftToRight, english.FlowDirection);
                Assert.Equal("Work time tracker", english.Title);
                Assert.Equal(DayOfWeek.Monday, ((System.Windows.Controls.DatePicker)english.FindName("DatePickerControl")).FirstDayOfWeek);
                VerifyLayout(english, assembly, start);
                english.Close();
                var englishSettings = new SettingsWindow(new AppSettings(), Environment.CurrentDirectory);
                Assert.Equal(System.Windows.FlowDirection.LeftToRight, englishSettings.FlowDirection);
                Assert.Equal("Work time settings", englishSettings.Title);
                englishSettings.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }

    private static void VerifySettingsLayout()
    {
        var window = new SettingsWindow(new AppSettings(), Environment.CurrentDirectory);
        var root = (System.Windows.FrameworkElement)window.Content;
        var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("SettingsScroll");
        var input = (System.Windows.FrameworkElement)window.FindName("MinimumVisibleInput");
        foreach (var theme in new[] { Wpf.Ui.Appearance.ApplicationTheme.Light, Wpf.Ui.Appearance.ApplicationTheme.Dark })
        foreach (var size in new[] { new System.Windows.Size(480, 480), new System.Windows.Size(600, 820) })
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
            root.Measure(size);
            root.Arrange(new System.Windows.Rect(size));
            root.UpdateLayout();
            var scrollbar = FindVisuals<System.Windows.Controls.Primitives.ScrollBar>(scroll)
                .First(s => s.Orientation == System.Windows.Controls.Orientation.Vertical && s.Visibility == System.Windows.Visibility.Visible);
            var inputRect = input.TransformToAncestor(root).TransformBounds(new System.Windows.Rect(input.RenderSize));
            var scrollRect = scrollbar.TransformToAncestor(root).TransformBounds(new System.Windows.Rect(scrollbar.RenderSize));
            Assert.True(inputRect.Right <= scrollRect.Left || scrollRect.Right <= inputRect.Left,
                $"Settings input overlaps its scrollbar at {size}: input={inputRect}, scrollbar={scrollRect}.");
        }
        window.Close();
    }

    private static void VerifyLayout(System.Windows.Window window, Assembly assembly, DateTimeOffset start)
    {
        var vm = window.DataContext;
        var vmType = vm.GetType();
        var dayType = assembly.GetType("WorkTimeTracker.App.DashboardDay", true)!;
        var day = Activator.CreateInstance(dayType, DateOnly.FromDateTime(start.Date), (DateTimeOffset?)start, (DateTimeOffset?)start.AddHours(8),
            TimeSpan.FromHours(5), TimeSpan.FromHours(9), TimeSpan.FromHours(1), TimeSpan.FromHours(8), true, false)!;
        var daysList = (System.Collections.IList)vmType.GetProperty("Days")!.GetValue(vm)!;
        daysList.Add(day);
        daysList.Add(Activator.CreateInstance(dayType, DateOnly.FromDateTime(start.AddDays(-1).Date), (DateTimeOffset?)start.AddDays(-1), (DateTimeOffset?)start.AddDays(-1).AddHours(8),
            TimeSpan.FromHours(5), TimeSpan.FromHours(9), TimeSpan.FromHours(1), TimeSpan.FromHours(8), true, false)!);
        var appType = assembly.GetType("WorkTimeTracker.App.DashboardApplication", true)!;
        var titleType = assembly.GetType("WorkTimeTracker.App.DashboardTitle", true)!;
        var title = Activator.CreateInstance(titleType, "Long sample window title", TimeSpan.FromHours(5), TimeSpan.FromHours(9), "Project")!;
        var titles = Array.CreateInstance(titleType, 1);
        titles.SetValue(title, 0);
        var application = Activator.CreateInstance(appType, "editor", "עורך יישומים ארוך עם שם נוסף שצריך להישאר גלוי במלואו", null,
            TimeSpan.FromHours(5), TimeSpan.FromHours(9), titles, null, System.Windows.Media.Brushes.CornflowerBlue, 130d)!;
        var labelType = assembly.GetType("WorkTimeTracker.App.DashboardLabel", true)!;
        var labels = Array.CreateInstance(labelType, 1);
        labels.SetValue(Activator.CreateInstance(labelType, "Project", TimeSpan.FromHours(5), TimeSpan.FromHours(9), titles), 0);
        appType.GetProperty("Labels")!.SetValue(application, labels);
        appType.GetProperty("ShowLabelGroups")!.SetValue(application, true);
        appType.GetProperty("ShowTitleRows")!.SetValue(application, false);
        ((System.Collections.IList)vmType.GetProperty("Chart")!.GetValue(vm)!).Add(application);
        var appList = (System.Collections.IList)vmType.GetProperty("Applications")!.GetValue(vm)!;
        appList.Add(application);
        appList.Add(Activator.CreateInstance(appType, "other", "יישום נוסף", null,
            TimeSpan.FromHours(1), TimeSpan.FromHours(2), titles, null, System.Windows.Media.Brushes.CornflowerBlue, 26d)!);
        var periodField = vmType.GetField("period", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var root = (System.Windows.FrameworkElement)window.Content;
        var editButtons = new[] { (Wpf.Ui.Controls.Button)window.FindName("EditStartButton"), (Wpf.Ui.Controls.Button)window.FindName("EditEndButton") };
        foreach (var button in editButtons)
        {
            button.Visibility = System.Windows.Visibility.Visible;
            Assert.Equal(24, button.Width);
            Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Edit20, Assert.IsType<Wpf.Ui.Controls.SymbolIcon>(button.Content).Symbol);
        }
        periodField.SetValue(vm, Enum.Parse(periodField.FieldType, "Week"));
        var notify = vmType.GetMethod("OnChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        notify.Invoke(vm, ["IsPeriod"]);
        notify.Invoke(vm, ["IsDaily"]);
        var days = (System.Windows.Controls.DataGrid)window.FindName("DaysGrid");
        var chart = (System.Windows.Controls.ItemsControl)window.FindName("ChartItems");
        var apps = (System.Windows.Controls.ItemsControl)window.FindName("ApplicationItems");
        ((System.Windows.FrameworkElement)window.FindName("PeriodSection")).Visibility = System.Windows.Visibility.Visible;
        ((System.Windows.FrameworkElement)window.FindName("TableSection")).Visibility = System.Windows.Visibility.Visible;
        foreach (var theme in new[] { Wpf.Ui.Appearance.ApplicationTheme.Light, Wpf.Ui.Appearance.ApplicationTheme.Dark })
        foreach (var size in new[] { new System.Windows.Size(900, 600), new System.Windows.Size(1200, 820) })
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
            var clientSize = new System.Windows.Size(size.Width - 16, size.Height - 40);
            root.Measure(clientSize);
            root.Arrange(new System.Windows.Rect(clientSize));
            root.UpdateLayout();
            var appContainer = Assert.IsType<System.Windows.Controls.ContentPresenter>(apps.ItemContainerGenerator.ContainerFromIndex(0));
            var otherContainer = Assert.IsType<System.Windows.Controls.ContentPresenter>(apps.ItemContainerGenerator.ContainerFromIndex(1));
            var appRow = (System.Windows.Controls.Border)apps.ItemTemplate.FindName("ApplicationRow", appContainer);
            var otherRow = (System.Windows.Controls.Border)apps.ItemTemplate.FindName("ApplicationRow", otherContainer);
            Assert.NotEqual(appRow.Background.ToString(), otherRow.Background.ToString());
            Assert.Contains(appRow.Style.Triggers.OfType<System.Windows.Trigger>(), trigger => trigger.Property == System.Windows.UIElement.IsMouseOverProperty);
            var expand = (System.Windows.Controls.Primitives.ToggleButton)apps.ItemTemplate.FindName("AppExpanded", appContainer);
            expand.IsChecked = true;
            root.UpdateLayout();
            var labelToggle = FindVisuals<System.Windows.Controls.Primitives.ToggleButton>(appContainer)
                .Single(toggle => toggle.Name == "LabelExpanded");
            labelToggle.IsChecked = true;
            root.UpdateLayout();
            var appName = (System.Windows.Controls.TextBlock)apps.ItemTemplate.FindName("AppName", appContainer);
            var appActive = (System.Windows.FrameworkElement)apps.ItemTemplate.FindName("AppActive", appContainer);
            var appDisplayed = (System.Windows.FrameworkElement)apps.ItemTemplate.FindName("AppDisplayed", appContainer);
            var appNameRect = appName.TransformToAncestor(appContainer).TransformBounds(new System.Windows.Rect(appName.RenderSize));
            var appActiveRect = appActive.TransformToAncestor(appContainer).TransformBounds(new System.Windows.Rect(appActive.RenderSize));
            var appDisplayedRect = appDisplayed.TransformToAncestor(appContainer).TransformBounds(new System.Windows.Rect(appDisplayed.RenderSize));
            Assert.True(appName.ActualWidth > 80 && appNameRect.Right <= appActiveRect.Left + 0.1 && appActiveRect.Right <= appDisplayedRect.Left + 0.1,
                $"Application group labels overlap at {size.Width}: name={appNameRect}, active={appActiveRect}, displayed={appDisplayedRect}.");
            var activeHeader = (System.Windows.FrameworkElement)window.FindName("SortActive");
            var displayedHeader = (System.Windows.FrameworkElement)window.FindName("SortDisplayed");
            var activeInRoot = appActive.TransformToAncestor(root).TransformBounds(new System.Windows.Rect(appActive.RenderSize));
            var displayedInRoot = appDisplayed.TransformToAncestor(root).TransformBounds(new System.Windows.Rect(appDisplayed.RenderSize));
            var activeHeaderInRoot = activeHeader.TransformToAncestor(root).TransformBounds(new System.Windows.Rect(activeHeader.RenderSize));
            var displayedHeaderInRoot = displayedHeader.TransformToAncestor(root).TransformBounds(new System.Windows.Rect(displayedHeader.RenderSize));
            Assert.True(Math.Abs(activeHeaderInRoot.Left - activeInRoot.Left) < 0.1,
                $"Header={activeHeaderInRoot}, row={activeInRoot}, header flow={activeHeader.FlowDirection}, row flow={appActive.FlowDirection}, app={appContainer.RenderSize}, header parent={((System.Windows.FrameworkElement)activeHeader.Parent).RenderSize}");
            Assert.Equal(activeHeaderInRoot.Right, activeInRoot.Right, 1);
            Assert.Equal(displayedHeaderInRoot.Left, displayedInRoot.Left, 1);
            Assert.Equal(displayedHeaderInRoot.Right, displayedInRoot.Right, 1);
            var headers = FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(days)
                .Where(h => h.Column is not null).ToArray();
            Assert.Equal(8, headers.Length);
            var rects = headers.Select(h => h.TransformToAncestor(days).TransformBounds(new System.Windows.Rect(h.RenderSize)))
                .OrderBy(r => r.Left).ToArray();
            for (var i = 1; i < rects.Length; i++) Assert.True(rects[i - 1].Right <= rects[i].Left + 0.1);
            Assert.True(rects[0].Left >= -0.5 && rects[^1].Right <= days.ActualWidth + 0.5,
                $"Period headers exceed the viewport at {size.Width}: left={rects[0].Left}, right={rects[^1].Right}, grid={days.ActualWidth}, columns={string.Join(",", days.Columns.Select(c => c.ActualWidth))}.");
            foreach (var header in headers.Where(h => h.Content is not null))
            {
                var headerText = FindVisuals<System.Windows.Controls.TextBlock>(header).First(t => t.Text == header.Content.ToString());
                var headerSize = new System.Windows.Media.FormattedText(headerText.Text, System.Globalization.CultureInfo.GetCultureInfo("he-IL"),
                    headerText.FlowDirection, new System.Windows.Media.Typeface(headerText.FontFamily, headerText.FontStyle, headerText.FontWeight, headerText.FontStretch),
                    headerText.FontSize, System.Windows.Media.Brushes.Black, 1) { MaxTextWidth = headerText.ActualWidth };
                Assert.True(headerSize.Height <= headerText.ActualHeight + 0.5, $"Header '{headerText.Text}' is clipped at {size.Width}: formatted {headerSize.Height}, actual {headerText.ActualHeight}, width {headerText.ActualWidth}, header {header.ActualWidth} padding {header.Padding}.");
            }
            foreach (var column in days.Columns.OfType<System.Windows.Controls.DataGridTextColumn>())
            {
                var cellText = Assert.IsType<System.Windows.Controls.TextBlock>(column.GetCellContent(day));
                Assert.Equal(System.Windows.TextAlignment.Right, cellText.TextAlignment);
                var width = new System.Windows.Media.FormattedText(cellText.Text, System.Globalization.CultureInfo.GetCultureInfo("he-IL"),
                    cellText.FlowDirection, new System.Windows.Media.Typeface(cellText.FontFamily, cellText.FontStyle, cellText.FontWeight, cellText.FontStretch),
                    cellText.FontSize, System.Windows.Media.Brushes.Black, 1).WidthIncludingTrailingWhitespace;
                Assert.True(width <= cellText.ActualWidth + 0.5, $"Cell '{cellText.Text}' is clipped at {size.Width}: text {width}, actual {cellText.ActualWidth}.");
            }
            var firstRow = Assert.IsType<System.Windows.Controls.DataGridRow>(days.ItemContainerGenerator.ContainerFromIndex(0));
            var secondRow = Assert.IsType<System.Windows.Controls.DataGridRow>(days.ItemContainerGenerator.ContainerFromIndex(1));
            Assert.NotEqual(firstRow.Background.ToString(), secondRow.Background.ToString());
            Assert.Contains(firstRow.Style.Triggers.OfType<System.Windows.Trigger>(), trigger => trigger.Property == System.Windows.UIElement.IsMouseOverProperty);
            var container = Assert.IsType<System.Windows.Controls.ContentPresenter>(chart.ItemContainerGenerator.ContainerFromIndex(0));
            var template = chart.ItemTemplate;
            var name = (System.Windows.FrameworkElement)template.FindName("ChartName", container);
            var bar = (System.Windows.FrameworkElement)template.FindName("ChartBar", container);
            var value = (System.Windows.FrameworkElement)template.FindName("ChartValue", container);
            var nameRect = name.TransformToAncestor(container).TransformBounds(new System.Windows.Rect(name.RenderSize));
            var barRect = bar.TransformToAncestor(container).TransformBounds(new System.Windows.Rect(bar.RenderSize));
            var valueRect = value.TransformToAncestor(container).TransformBounds(new System.Windows.Rect(value.RenderSize));
            Assert.True(nameRect.Right <= barRect.Left + 0.1 && barRect.Right <= valueRect.Left + 0.1,
                $"Chart elements overlap at {size.Width}: name={nameRect}, bar={barRect}, value={valueRect}.");
            Assert.True(barRect.Left - nameRect.Right <= 16 && valueRect.Left - barRect.Right <= 16,
                $"Chart labels are separated from the bar at {size.Width}.");
            var chartName = (System.Windows.Controls.TextBlock)name;
            var nameSize = new System.Windows.Media.FormattedText(chartName.Text, System.Globalization.CultureInfo.GetCultureInfo("he-IL"),
                chartName.FlowDirection, new System.Windows.Media.Typeface(chartName.FontFamily, chartName.FontStyle, chartName.FontWeight, chartName.FontStretch),
                chartName.FontSize, System.Windows.Media.Brushes.Black, 1) { MaxTextWidth = chartName.ActualWidth };
            Assert.True(nameSize.Height <= chartName.ActualHeight + 0.5, $"Chart name is clipped at {size.Width}.");
            var commonName = new System.Windows.Media.FormattedText("Windows Shell Experience Host", System.Globalization.CultureInfo.GetCultureInfo("en-US"),
                System.Windows.FlowDirection.LeftToRight, new System.Windows.Media.Typeface(chartName.FontFamily, chartName.FontStyle, chartName.FontWeight, chartName.FontStretch),
                chartName.FontSize, System.Windows.Media.Brushes.Black, 1);
            Assert.True(commonName.WidthIncludingTrailingWhitespace <= chartName.ActualWidth, $"Typical chart name wraps at {size.Width}.");
        }
        var sortHeader = FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(days)
            .Single(h => h.Column?.SortMemberPath == "ActiveTime");
        typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sortHeader, null);
        Assert.Equal(System.ComponentModel.ListSortDirection.Ascending, sortHeader.Column.SortDirection);
        appList.Clear();
        appList.Add(application);
        root.UpdateLayout();
        var replacement = Assert.IsType<System.Windows.Controls.ContentPresenter>(apps.ItemContainerGenerator.ContainerFromIndex(0));
        var restored = (System.Windows.Controls.Primitives.ToggleButton)apps.ItemTemplate.FindName("AppExpanded", replacement);
        typeof(DashboardWindow).GetMethod("AppExpansion_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [restored, new System.Windows.RoutedEventArgs()]);
        Assert.True(restored.IsChecked);
        root.UpdateLayout();
        var restoredLabel = FindVisuals<System.Windows.Controls.Primitives.ToggleButton>(replacement)
            .Single(toggle => toggle.Name == "LabelExpanded");
        typeof(DashboardWindow).GetMethod("LabelExpansion_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [restoredLabel, new System.Windows.RoutedEventArgs()]);
        Assert.True(restoredLabel.IsChecked);
    }

    private static IEnumerable<T> FindVisuals<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisuals<T>(child)) yield return descendant;
        }
    }

    [Fact]
    public void PeriodReportAndExportsIncludeGroupedMetricsAndProtectCsv()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "dashboard-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var db = Path.Combine(directory, "work.sqlite3");
            var start = new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.Zero);
            var day = new WorkDay(new DateOnly(2025, 1, 1), start, start.AddMinutes(2), WorkDayEndReason.Manual);
            using (var repo = new SqliteWorkTimeRepository(db))
            {
                var stored = repo.CreateWorkDay(day, start);
                repo.WriteBatch(stored.Id, new RawSignalBatch
                {
                    Windows = [new WindowInfo("one", "editor", "Editor", null, "=danger,\"title\""), new WindowInfo("two", "editor", "Editor", null, "other title")],
                    VisibilitySegments = [new VisibilitySegment("one", start, start.AddMinutes(2), 100, true)],
                    Interactions = [new InteractionSecond("one", start, InteractionKind.Keyboard), new InteractionSecond("one", start.AddSeconds(90), InteractionKind.Keyboard)]
                });
            }
            var assembly = typeof(TrackingService).Assembly;
            var reportType = assembly.GetType("WorkTimeTracker.App.DashboardReport", true)!;
            var load = reportType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!;
            var groupingType = assembly.GetType("WorkTimeTracker.App.DashboardGrouping", true)!;
            var ruleArgs = new object?[] { db, day.Date, day.Date, new AppSettings(), Enum.ToObject(groupingType, 1) };
            ruleArgs[3] = new AppSettings { LabelRules = [new WorkTimeTracker.Core.Labels.LabelRule("editor", @"(?<label>Alpha)")] };
            var reportBeforeRuleChange = load.Invoke(null, ruleArgs)!;
            var firstBefore = ((System.Collections.IEnumerable)reportType.GetProperty("Applications")!.GetValue(reportBeforeRuleChange)!).Cast<object>().First();
            ruleArgs[3] = new AppSettings { LabelRules = [new WorkTimeTracker.Core.Labels.LabelRule("editor", @"(?<label>danger)")] };
            var reportAfterRuleChange = load.Invoke(null, ruleArgs)!;
            var firstAfter = ((System.Collections.IEnumerable)reportType.GetProperty("Applications")!.GetValue(reportAfterRuleChange)!).Cast<object>().First();
            Assert.NotEqual(firstBefore.GetType().GetProperty("Name")!.GetValue(firstBefore), firstAfter.GetType().GetProperty("Name")!.GetValue(firstAfter));
            ruleArgs[3] = new AppSettings();
            ruleArgs[4] = Enum.ToObject(groupingType, 0);
            var report = load.Invoke(null, ruleArgs)!;
            var appOnlyReport = load.Invoke(null, [db, day.Date, day.Date, new AppSettings(), Enum.ToObject(groupingType, 2)])!;
            var appOnly = Assert.Single(((System.Collections.IEnumerable)reportType.GetProperty("Applications")!.GetValue(appOnlyReport)!).Cast<object>());
            Assert.Empty((System.Collections.IEnumerable)appOnly.GetType().GetProperty("Titles")!.GetValue(appOnly)!);
            var timeline = ((System.Collections.IEnumerable)reportType.GetProperty("Timeline")!.GetValue(report)!).Cast<object>().ToArray();
            Assert.Equal(2, timeline.Length);
            Assert.Equal(952d, (double)timeline[0].GetType().GetProperty("Left")!.GetValue(timeline[0])!, 6);
            Assert.Equal(8d, (double)timeline[0].GetType().GetProperty("Width")!.GetValue(timeline[0])!, 6);
            Assert.Equal(0d, (double)timeline[1].GetType().GetProperty("Left")!.GetValue(timeline[1])!, 6);
            Assert.Equal(240d, (double)timeline[1].GetType().GetProperty("Width")!.GetValue(timeline[1])!, 6);
            var app = Assert.Single(((System.Collections.IEnumerable)reportType.GetProperty("Applications")!.GetValue(report)!).Cast<object>());
            var filtered = app.GetType().GetMethod("Filter")!.Invoke(app, ["danger", true])!;
            var visible = Array.CreateInstance(app.GetType(), 1);
            visible.SetValue(filtered, 0);
            var exportType = assembly.GetType("WorkTimeTracker.App.ReportExport", true)!;
            report = exportType.GetMethod("VisibleGroups")!.Invoke(null, [report, visible])!;
            var export = exportType.GetMethod("Write", BindingFlags.Public | BindingFlags.Static)!;
            var csv = Path.Combine(directory, "report.csv");
            var xlsx = Path.Combine(directory, "report.xlsx");
            export.Invoke(null, [csv, report]);
            export.Invoke(null, [xlsx, report]);
            var bytes = File.ReadAllBytes(csv);
            Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes.Take(3));
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("\"'=danger,\"\"title\"\"\"", text);
            Assert.DoesNotContain("other title", text);
            Assert.Contains("\"0:00\"", text);
            using var workbook = new XLWorkbook(xlsx);
            var sheet = workbook.Worksheets.First();
            Assert.Equal(CultureInfo.CurrentCulture.TextInfo.IsRightToLeft, sheet.RightToLeft);
            Assert.Contains(sheet.RowsUsed(), row => row.Cell(7).GetString() == "=danger,\"title\"" && row.Cell(9).GetString() == "0:00" && Math.Abs(row.Cell(10).GetDouble() - 31d / 3600) < 0.000001);
            Assert.Contains(sheet.RowsUsed(), row => row.Cell(2).GetString() == day.Date.ToString("ddd yyyy-MM-dd", CultureInfo.CurrentCulture) && row.Cell(3).GetString() == start.ToLocalTime().ToString("dd/MM/yyyy HH:mm") && row.Cell(4).GetString() == start.AddMinutes(2).ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
            var labelXlsx = Path.Combine(directory, "labels.xlsx");
            export.Invoke(null, [labelXlsx, reportAfterRuleChange]);
            using var labelBook = new XLWorkbook(labelXlsx);
            var labelRows = labelBook.Worksheets.First().RowsUsed().Skip(1).ToArray();
            Assert.Contains(labelRows, row => row.Cell(5).GetString() == "" && row.Cell(6).GetString() == "danger" && row.Cell(7).GetString() == "");
            Assert.DoesNotContain(labelRows, row => row.Cell(5).GetString() == "danger");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
