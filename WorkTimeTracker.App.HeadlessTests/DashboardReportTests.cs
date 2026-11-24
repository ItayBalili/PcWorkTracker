using System.Reflection;
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
            var report = reportType.GetMethod("Load")!.Invoke(null, [db, day.Date, day.Date, new TrackerSettings()])!;
            var days = (System.Collections.IEnumerable)reportType.GetProperty("Reports")!.GetValue(report)!;
            var calculated = Assert.Single(days.Cast<DayReport>());
            Assert.Equal(TimeSpan.FromSeconds(30), calculated.TotalActiveTime);
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

    private static void AssertTimelineMatchesEngine(WorkDayRawData raw, DateTimeOffset end)
    {
        var settings = new TrackerSettings();
        var calculated = CalculationEngine.Calculate(raw, settings, end);
        var method = typeof(TrackingService).Assembly.GetType("WorkTimeTracker.App.DashboardReport", true)!
            .GetMethod("BuildTimeline", BindingFlags.NonPublic | BindingFlags.Static)!;
        var entries = (System.Collections.IEnumerable)method.Invoke(null, [raw, settings, end])!;
        var active = TimeSpan.Zero;
        var away = TimeSpan.Zero;
        foreach (var item in entries)
        {
            var type = item!.GetType();
            var duration = (DateTimeOffset)type.GetProperty("End")!.GetValue(item)! - (DateTimeOffset)type.GetProperty("Start")!.GetValue(item)!;
            if ((string)type.GetProperty("Label")!.GetValue(item)! == "Editor") active += duration;
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
                var windowType = assembly.GetType("WorkTimeTracker.App.DashboardWindow", true)!;
                var window = Activator.CreateInstance(windowType,
                    (Func<TrackingService>)(() => throw new InvalidOperationException()),
                    (Func<TrackerSettings>)(() => new TrackerSettings()),
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
                var navigation = (Wpf.Ui.Controls.NavigationView)dashboard.FindName("MainNavigation");
                var overview = (Wpf.Ui.Controls.NavigationViewItem)dashboard.FindName("OverviewNavigation");
                var settingsItem = (Wpf.Ui.Controls.NavigationViewItem)dashboard.FindName("SettingsNavigation");
                Assert.True(navigation.ActualHeight > 0 && overview.ActualWidth > 0 && settingsItem.ActualWidth > 0);
                foreach (var item in new[] { overview, settingsItem })
                {
                    var icon = Assert.Single(FindVisuals<Wpf.Ui.Controls.SymbolIcon>(item));
                    Assert.True(icon.ActualWidth > 0 && icon.ActualHeight > 0 && icon.Visibility == System.Windows.Visibility.Visible);
                }
                VerifySettingsLayout();
                var settingsRequested = 0;
                dashboard.SettingsRequested += (_, _) => settingsRequested++;
                settingsItem.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(1, settingsRequested);
                windowType.GetMethod("ShowExportSuccess", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [Path.Combine(Environment.CurrentDirectory, "report.xlsx")]);
                var presenter = ((System.Windows.Window)window).FindName("SnackPresenter")!;
                var snackbar = Assert.IsType<Wpf.Ui.Controls.Snackbar>(((Wpf.Ui.Controls.SnackbarPresenter)presenter).Content);
                var notice = Assert.IsType<System.Windows.Controls.TextBlock>(snackbar.Content);
                var link = Assert.Single(notice.Inlines.OfType<System.Windows.Documents.Hyperlink>());
                Assert.Equal("פתיחת תיקיית הדוח", ((System.Windows.Documents.Run)link.Inlines.FirstInline).Text);
                windowType.GetMethod("Close")!.Invoke(window, null);
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
        var day = Activator.CreateInstance(dayType, DateOnly.FromDateTime(start.Date), start, (DateTimeOffset?)start.AddHours(8),
            TimeSpan.FromHours(5), TimeSpan.FromHours(9), TimeSpan.FromHours(1), TimeSpan.FromHours(8))!;
        var daysList = (System.Collections.IList)vmType.GetProperty("Days")!.GetValue(vm)!;
        daysList.Add(day);
        daysList.Add(Activator.CreateInstance(dayType, DateOnly.FromDateTime(start.AddDays(-1).Date), start.AddDays(-1), (DateTimeOffset?)start.AddDays(-1).AddHours(8),
            TimeSpan.FromHours(5), TimeSpan.FromHours(9), TimeSpan.FromHours(1), TimeSpan.FromHours(8))!);
        var appType = assembly.GetType("WorkTimeTracker.App.DashboardApplication", true)!;
        var titleType = assembly.GetType("WorkTimeTracker.App.DashboardTitle", true)!;
        var title = Activator.CreateInstance(titleType, "כותרת ארוכה של חלון לדוגמה עם פרטים נוספים שלא צריכים להיחתך", TimeSpan.FromHours(5), TimeSpan.FromHours(9))!;
        var titles = Array.CreateInstance(titleType, 1);
        titles.SetValue(title, 0);
        var application = Activator.CreateInstance(appType, "editor", "עורך יישומים ארוך עם שם נוסף שצריך להישאר גלוי במלואו", null,
            TimeSpan.FromHours(5), TimeSpan.FromHours(9), titles, null, System.Windows.Media.Brushes.CornflowerBlue, 130d)!;
        ((System.Collections.IList)vmType.GetProperty("Chart")!.GetValue(vm)!).Add(application);
        var appList = (System.Collections.IList)vmType.GetProperty("Applications")!.GetValue(vm)!;
        appList.Add(application);
        appList.Add(Activator.CreateInstance(appType, "other", "יישום נוסף", null,
            TimeSpan.FromHours(1), TimeSpan.FromHours(2), titles, null, System.Windows.Media.Brushes.CornflowerBlue, 26d)!);
        var periodField = vmType.GetField("period", BindingFlags.NonPublic | BindingFlags.Instance)!;
        periodField.SetValue(vm, Enum.Parse(periodField.FieldType, "Week"));
        var notify = vmType.GetMethod("OnChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        notify.Invoke(vm, ["IsPeriod"]);
        notify.Invoke(vm, ["IsDaily"]);
        var root = (System.Windows.FrameworkElement)window.Content;
        var days = (System.Windows.Controls.DataGrid)window.FindName("DaysGrid");
        var chart = (System.Windows.Controls.ItemsControl)window.FindName("ChartItems");
        var apps = (System.Windows.Controls.ItemsControl)window.FindName("ApplicationItems");
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
            var expander = FindVisuals<System.Windows.Controls.Expander>(appContainer).Single();
            expander.IsExpanded = true;
            root.UpdateLayout();
            var appName = (System.Windows.Controls.TextBlock)apps.ItemTemplate.FindName("AppName", appContainer);
            var appActive = (System.Windows.FrameworkElement)apps.ItemTemplate.FindName("AppActive", appContainer);
            var appDisplayed = (System.Windows.FrameworkElement)apps.ItemTemplate.FindName("AppDisplayed", appContainer);
            var appNameRect = appName.TransformToAncestor(appContainer).TransformBounds(new System.Windows.Rect(appName.RenderSize));
            var appActiveRect = appActive.TransformToAncestor(appContainer).TransformBounds(new System.Windows.Rect(appActive.RenderSize));
            var appDisplayedRect = appDisplayed.TransformToAncestor(appContainer).TransformBounds(new System.Windows.Rect(appDisplayed.RenderSize));
            Assert.True(appName.ActualWidth > 80 && appNameRect.Right <= appActiveRect.Left + 0.1 && appActiveRect.Right <= appDisplayedRect.Left + 0.1,
                $"Application group labels overlap at {size.Width}: name={appNameRect}, active={appActiveRect}, displayed={appDisplayedRect}.");
            var headers = FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(days)
                .Where(h => h.Column is not null).ToArray();
            Assert.Equal(7, headers.Length);
            var rects = headers.Select(h => h.TransformToAncestor(days).TransformBounds(new System.Windows.Rect(h.RenderSize)))
                .OrderBy(r => r.Left).ToArray();
            for (var i = 1; i < rects.Length; i++) Assert.True(rects[i - 1].Right <= rects[i].Left + 0.1);
            Assert.True(rects[0].Left >= -0.5 && rects[^1].Right <= days.ActualWidth + 0.5,
                $"Period headers exceed the viewport at {size.Width}: left={rects[0].Left}, right={rects[^1].Right}, grid={days.ActualWidth}, columns={string.Join(",", days.Columns.Select(c => c.ActualWidth))}.");
            foreach (var header in headers)
            {
                var headerText = FindVisuals<System.Windows.Controls.TextBlock>(header).First(t => t.Text == header.Content.ToString());
                var headerSize = new System.Windows.Media.FormattedText(headerText.Text, System.Globalization.CultureInfo.GetCultureInfo("he-IL"),
                    headerText.FlowDirection, new System.Windows.Media.Typeface(headerText.FontFamily, headerText.FontStyle, headerText.FontWeight, headerText.FontStretch),
                    headerText.FontSize, System.Windows.Media.Brushes.Black, 1) { MaxTextWidth = headerText.ActualWidth };
                Assert.True(headerSize.Height <= headerText.ActualHeight + 0.5, $"Header '{headerText.Text}' is clipped at {size.Width}: formatted {headerSize.Height}, actual {headerText.ActualHeight}, width {headerText.ActualWidth}, header {header.ActualWidth} padding {header.Padding}.");
            }
            foreach (var column in days.Columns)
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
            var titleGrid = (System.Windows.Controls.DataGrid)apps.ItemTemplate.FindName("TitlesGrid", appContainer);
            titleGrid.Measure(new System.Windows.Size(size.Width - 100, 200));
            titleGrid.Arrange(new System.Windows.Rect(0, 0, size.Width - 100, 200));
            titleGrid.UpdateLayout();
            var titleText = Assert.IsType<System.Windows.Controls.TextBlock>(titleGrid.Columns[0].GetCellContent(title));
            var titleWidth = new System.Windows.Media.FormattedText(titleText.Text, System.Globalization.CultureInfo.GetCultureInfo("he-IL"),
                titleText.FlowDirection, new System.Windows.Media.Typeface(titleText.FontFamily, titleText.FontStyle, titleText.FontWeight, titleText.FontStretch),
                titleText.FontSize, System.Windows.Media.Brushes.Black, 1).WidthIncludingTrailingWhitespace;
            Assert.True(titleWidth <= titleText.ActualWidth + 0.5, $"Application title is clipped at {size.Width}.");
        }
        var sortHeader = FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(days)
            .Single(h => h.Column?.SortMemberPath == "ActiveTime");
        typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sortHeader, null);
        Assert.Equal(System.ComponentModel.ListSortDirection.Ascending, sortHeader.Column.SortDirection);
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
            var report = load.Invoke(null, [db, day.Date, day.Date, new TrackerSettings()])!;
            var timeline = ((System.Collections.IEnumerable)reportType.GetProperty("Timeline")!.GetValue(report)!).Cast<object>().ToArray();
            Assert.Equal(2, timeline.Length);
            Assert.Equal(0d, (double)timeline[0].GetType().GetProperty("Left")!.GetValue(timeline[0])!, 6);
            Assert.Equal(480d, (double)timeline[0].GetType().GetProperty("Width")!.GetValue(timeline[0])!, 6);
            Assert.Equal(720d, (double)timeline[1].GetType().GetProperty("Left")!.GetValue(timeline[1])!, 6);
            Assert.Equal(240d, (double)timeline[1].GetType().GetProperty("Width")!.GetValue(timeline[1])!, 6);
            var app = Assert.Single(((System.Collections.IEnumerable)reportType.GetProperty("Applications")!.GetValue(report)!).Cast<object>());
            var filtered = app.GetType().GetMethod("Filter")!.Invoke(app, ["danger"])!;
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
            Assert.Contains("\"0:01\"", text);
            using var workbook = new XLWorkbook(xlsx);
            var sheet = workbook.Worksheets.First();
            Assert.True(sheet.RightToLeft);
            Assert.Contains(sheet.RowsUsed(), row => row.Cell(6).GetString() == "=danger,\"title\"" && row.Cell(8).GetString() == "0:01" && Math.Abs(row.Cell(9).GetDouble() - 1.5 / 60) < 0.000001);
            Assert.Contains(sheet.RowsUsed(), row => row.Cell(2).GetString() == "2025-01-01" && row.Cell(3).GetString() == start.ToLocalTime().ToString("dd/MM/yyyy HH:mm") && row.Cell(4).GetString() == start.AddMinutes(2).ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
