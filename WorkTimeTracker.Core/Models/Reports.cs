namespace WorkTimeTracker.Core.Models;

public sealed record WindowMetrics(
    WindowInfo Window,
    TimeSpan DisplayedTime,
    TimeSpan ActiveTime);

public sealed record DayReport(
    WorkDay WorkDay,
    DateTimeOffset EffectiveEndUtc,
    TimeSpan TotalSpan,
    TimeSpan NotTrackedTime,
    TimeSpan AwayTime,
    TimeSpan TotalDisplayedTime,
    TimeSpan TotalActiveTime,
    IReadOnlyList<WindowMetrics> Windows);
