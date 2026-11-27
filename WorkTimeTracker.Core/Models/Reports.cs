namespace WorkTimeTracker.Core.Models;

public sealed record WindowMetrics(
    WindowInfo Window,
    TimeSpan DisplayedTime,
    TimeSpan ActiveTime)
{
    public DateTimeOffset? FirstUsedUtc { get; init; }
    public DateTimeOffset? LastUsedUtc { get; init; }
}

public sealed record DayReport(
    WorkDay WorkDay,
    DateTimeOffset EffectiveEndUtc,
    TimeSpan TotalSpan,
    TimeSpan NotTrackedTime,
    TimeSpan AwayTime,
    TimeSpan TotalDisplayedTime,
    TimeSpan TotalActiveTime,
    IReadOnlyList<WindowMetrics> Windows)
{
    public TimeSpan PoweredOffTime { get; init; }
}
