using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Tests;

public sealed class CalculationEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WordAndExcelTrackDisplayedAndActiveTimeSeparately()
    {
        var word = Window("word", "Microsoft Word", "Report.docx - Word");
        var excel = Window("excel", "Microsoft Excel", "Hours.xlsx - Excel");
        var interactions = new List<InteractionSecond>();

        for (var minute = 0; minute < 30; minute++)
        {
            interactions.Add(At("word", TimeSpan.FromMinutes(minute), InteractionKind.Keyboard));
        }

        for (var minute = 30; minute < 120; minute++)
        {
            interactions.Add(At("excel", TimeSpan.FromMinutes(minute), InteractionKind.Keyboard));
        }

        for (var minute = 38; minute < 120; minute += 9)
        {
            interactions.Add(At("word", TimeSpan.FromMinutes(minute), InteractionKind.Hover));
        }

        var report = Calculate(
            [word, excel],
            [
                Visible(word, TimeSpan.Zero, TimeSpan.FromMinutes(30), 100m, true),
                Visible(word, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(120), 100m, false),
                Visible(excel, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(120), 100m, true)
            ],
            interactions,
            TimeSpan.FromMinutes(120));

        Assert.Equal(TimeSpan.FromHours(2), Metric(report, "word").DisplayedTime);
        Assert.Equal(TimeSpan.FromMinutes(30), Metric(report, "word").ActiveTime);
        Assert.Equal(TimeSpan.FromMinutes(90), Metric(report, "excel").ActiveTime);
        Assert.Equal(TimeSpan.FromHours(2), report.TotalActiveTime);
    }

    [Fact]
    public void BackgroundPdfWithRegularScrollIsDisplayedButNotActive()
    {
        var pdf = Window("pdf", "Reader", "Reference.pdf");
        var excel = Window("excel", "Microsoft Excel", "Hours.xlsx");
        var interactions = new List<InteractionSecond>();
        for (var minute = 0; minute < 20; minute++)
        {
            interactions.Add(At("excel", TimeSpan.FromMinutes(minute), InteractionKind.Keyboard));
        }

        for (var minute = 0; minute < 20; minute += 5)
        {
            interactions.Add(At("pdf", TimeSpan.FromMinutes(minute), InteractionKind.Scroll));
        }

        var report = Calculate(
            [pdf, excel],
            [
                Visible(pdf, TimeSpan.Zero, TimeSpan.FromMinutes(20), 50m, false),
                Visible(excel, TimeSpan.Zero, TimeSpan.FromMinutes(20), 100m, true)
            ],
            interactions,
            TimeSpan.FromMinutes(20));

        Assert.Equal(TimeSpan.FromMinutes(20), Metric(report, "pdf").DisplayedTime);
        Assert.Equal(TimeSpan.Zero, Metric(report, "pdf").ActiveTime);
    }

    [Fact]
    public void BackgroundWindowStopsDisplayingAfterInteractionWindow()
    {
        var pdf = Window("pdf", "Reader", "Reference.pdf");
        var excel = Window("excel", "Microsoft Excel", "Hours.xlsx");
        var interactions = Enumerable.Range(0, 20)
            .Select(minute => At("excel", TimeSpan.FromMinutes(minute), InteractionKind.Keyboard))
            .Append(At("pdf", TimeSpan.Zero, InteractionKind.Scroll))
            .ToArray();

        var report = Calculate(
            [pdf, excel],
            [
                Visible(pdf, TimeSpan.Zero, TimeSpan.FromMinutes(20), 50m, false),
                Visible(excel, TimeSpan.Zero, TimeSpan.FromMinutes(20), 100m, true)
            ],
            interactions,
            TimeSpan.FromMinutes(20));

        Assert.Equal(TimeSpan.FromMinutes(10), Metric(report, "pdf").DisplayedTime);
    }

    [Fact]
    public void WindowBelowVisibilityThresholdIsNotDisplayed()
    {
        var window = Window("small", "Editor", "Notes.txt");
        var report = Calculate(
            [window],
            [Visible(window, TimeSpan.Zero, TimeSpan.FromMinutes(10), 20m, true)],
            [At("small", TimeSpan.Zero, InteractionKind.Keyboard)],
            TimeSpan.FromMinutes(10));

        Assert.Equal(TimeSpan.Zero, Metric(report, "small").DisplayedTime);
        Assert.Equal(TimeSpan.FromMinutes(1), Metric(report, "small").ActiveTime);
    }

    [Fact]
    public void OverlappingForegroundSegmentsAreRejectedToPreserveSingleActiveWindow()
    {
        var first = Window("first", "Editor", "Notes.txt");
        var second = Window("second", "Browser", "Web page");
        var data = RawData(
            [first, second],
            [
                Visible(first, TimeSpan.Zero, TimeSpan.FromMinutes(1), 100m, true),
                Visible(second, TimeSpan.Zero, TimeSpan.FromMinutes(1), 100m, true)
            ],
            [],
            TimeSpan.FromMinutes(1));

        Assert.Throws<ArgumentException>(() =>
            CalculationEngine.Calculate(data, new TrackerSettings(), Start.AddMinutes(1)));
    }

    [Fact]
    public void LockPeriodIsAwayAndDoesNotCountForAnyWindow()
    {
        var window = Window("editor", "Editor", "Notes.txt");
        var interactions = new List<InteractionSecond>();
        for (var minute = 0; minute < 30; minute++)
        {
            interactions.Add(At("editor", TimeSpan.FromMinutes(minute), InteractionKind.Keyboard));
        }

        for (var minute = 90; minute < 120; minute++)
        {
            interactions.Add(At("editor", TimeSpan.FromMinutes(minute), InteractionKind.Keyboard));
        }

        var report = Calculate(
            [window],
            [Visible(window, TimeSpan.Zero, TimeSpan.FromMinutes(120), 100m, true)],
            interactions,
            TimeSpan.FromMinutes(120),
            [
                new SessionEvent(Start.AddMinutes(30), SessionEventKind.Lock),
                new SessionEvent(Start.AddMinutes(90), SessionEventKind.Unlock)
            ]);

        Assert.Equal(TimeSpan.FromHours(1), report.AwayTime);
        Assert.Equal(TimeSpan.FromHours(1), report.TotalDisplayedTime);
        Assert.Equal(TimeSpan.FromHours(1), report.TotalActiveTime);
    }

    [Fact]
    public void NoInputMeansNoDisplayedOrActiveTime()
    {
        var window = Window("editor", "Editor", "Notes.txt");
        var report = Calculate(
            [window],
            [Visible(window, TimeSpan.Zero, TimeSpan.FromMinutes(10), 100m, true)],
            [],
            TimeSpan.FromMinutes(10));

        Assert.Equal(TimeSpan.Zero, report.TotalDisplayedTime);
        Assert.Equal(TimeSpan.Zero, report.TotalActiveTime);
        Assert.Equal(TimeSpan.FromMinutes(10), report.AwayTime);
    }

    [Fact]
    public void ChangingVisibilityThresholdRecalculatesPastData()
    {
        var window = Window("editor", "Editor", "Notes.txt");
        var interactions = Enumerable.Range(0, 10)
            .Select(minute => At("editor", TimeSpan.FromMinutes(minute), InteractionKind.Keyboard))
            .ToArray();
        var data = RawData(
            [window],
            [Visible(window, TimeSpan.Zero, TimeSpan.FromMinutes(10), 40.4m, true)],
            interactions,
            TimeSpan.FromMinutes(10));

        var lowThresholdReport = CalculationEngine.Calculate(
            data,
            new TrackerSettings { MinimumVisiblePercent = 30m },
            Start.AddMinutes(10));
        var highThresholdReport = CalculationEngine.Calculate(
            data,
            new TrackerSettings { MinimumVisiblePercent = 60m },
            Start.AddMinutes(10));

        Assert.Equal(TimeSpan.FromMinutes(10), lowThresholdReport.TotalDisplayedTime);
        Assert.Equal(TimeSpan.Zero, highThresholdReport.TotalDisplayedTime);
    }

    [Fact]
    public void UntrackedStartupGapIsNotCountedAsAwayOrWindowTime()
    {
        var window = Window("editor", "Editor", "Notes.txt");
        var data = RawData(
            [window],
            [Visible(window, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60), 100m, true)],
            Enumerable.Range(30, 30)
                .Select(minute => At("editor", TimeSpan.FromMinutes(minute), InteractionKind.Keyboard))
                .ToArray(),
            TimeSpan.FromMinutes(60),
            trackingStarted: Start.AddMinutes(30));

        var report = CalculationEngine.Calculate(data, new TrackerSettings(), Start.AddMinutes(60));

        Assert.Equal(TimeSpan.FromMinutes(30), report.NotTrackedTime);
        Assert.Equal(TimeSpan.Zero, report.AwayTime);
        Assert.Equal(TimeSpan.FromMinutes(30), report.TotalActiveTime);
    }

    [Fact]
    public void VisibilitySegmentsRoundToTenthsAndMergeOnlyWhenStateMatches()
    {
        var segments = new List<VisibilitySegment>();
        var first = Visible(Window("editor", "Editor", "Notes.txt"), TimeSpan.Zero, TimeSpan.FromSeconds(1), 25.31m, false);
        var equivalent = Visible(Window("editor", "Editor", "Notes.txt"), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 25.34m, false);
        var changed = Visible(Window("editor", "Editor", "Notes.txt"), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), 25.4m, false);
        var otherWindow = Visible(Window("browser", "Browser", "Web page"), TimeSpan.Zero, TimeSpan.FromSeconds(1), 100m, true);

        VisibilityRunLengthEncoder.Append(segments, first);
        VisibilityRunLengthEncoder.Append(segments, otherWindow);
        VisibilityRunLengthEncoder.Append(segments, equivalent);
        VisibilityRunLengthEncoder.Append(segments, changed);

        Assert.Equal(3, segments.Count);
        Assert.Equal(25.3m, segments[0].VisiblePercent);
        Assert.Equal(TimeSpan.FromSeconds(2), segments[0].EndUtc - segments[0].StartUtc);
    }

    private static DayReport Calculate(
        IReadOnlyList<WindowInfo> windows,
        IReadOnlyList<VisibilitySegment> segments,
        IReadOnlyList<InteractionSecond> interactions,
        TimeSpan duration,
        IReadOnlyList<SessionEvent>? sessionEvents = null) =>
        CalculationEngine.Calculate(
            RawData(windows, segments, interactions, duration, sessionEvents: sessionEvents),
            new TrackerSettings(),
            Start.Add(duration));

    private static WorkDayRawData RawData(
        IReadOnlyList<WindowInfo> windows,
        IReadOnlyList<VisibilitySegment> segments,
        IReadOnlyList<InteractionSecond> interactions,
        TimeSpan duration,
        IReadOnlyList<SessionEvent>? sessionEvents = null,
        DateTimeOffset? trackingStarted = null) =>
        new(
            new WorkDay(DateOnly.FromDateTime(Start.DateTime), Start, Start.Add(duration), WorkDayEndReason.Manual),
            trackingStarted ?? Start,
            windows,
            segments,
            interactions,
            sessionEvents ?? []);

    private static WindowInfo Window(string id, string app, string title) =>
        new(id, app.Replace(" ", string.Empty, StringComparison.Ordinal), app, null, title);

    private static VisibilitySegment Visible(
        WindowInfo window,
        TimeSpan from,
        TimeSpan to,
        decimal percent,
        bool foreground) =>
        new(window.Id, Start.Add(from), Start.Add(to), percent, foreground);

    private static InteractionSecond At(string windowId, TimeSpan offset, InteractionKind kind) =>
        new(windowId, Start.Add(offset), kind);

    private static WindowMetrics Metric(DayReport report, string windowId) =>
        Assert.Single(report.Windows, metrics => metrics.Window.Id == windowId);
}
