namespace WorkTimeTracker.Core.Models;

[Flags]
public enum InteractionKind
{
    None = 0,
    Keyboard = 1,
    Click = 2,
    Scroll = 4,
    Hover = 8
}

public enum SessionEventKind
{
    Lock,
    Unlock,
    Logon,
    Logoff,
    Suspend,
    Resume,
    Shutdown,
    AppStart,
    AppStop
}

public sealed record WindowInfo(
    string Id,
    string ProcessName,
    string FriendlyName,
    string? ExecutablePath,
    string Title);

public sealed record VisibilitySegment
{
    public VisibilitySegment(
        string windowId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        decimal visiblePercent,
        bool isForeground)
    {
        if (string.IsNullOrWhiteSpace(windowId))
        {
            throw new ArgumentException("A window id is required.", nameof(windowId));
        }

        if (endUtc <= startUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(endUtc));
        }

        if (visiblePercent is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(visiblePercent));
        }

        WindowId = windowId;
        StartUtc = startUtc.ToUniversalTime();
        EndUtc = endUtc.ToUniversalTime();
        VisiblePercent = decimal.Round(visiblePercent, 1, MidpointRounding.AwayFromZero);
        IsForeground = isForeground;
    }

    public string WindowId { get; }
    public DateTimeOffset StartUtc { get; }
    public DateTimeOffset EndUtc { get; }
    public decimal VisiblePercent { get; }
    public bool IsForeground { get; }
}

public sealed record InteractionSecond(
    string WindowId,
    DateTimeOffset SecondUtc,
    InteractionKind Kind);

public sealed record SessionEvent(DateTimeOffset TimeUtc, SessionEventKind Kind);

public sealed record WorkDayRawData(
    WorkDay WorkDay,
    DateTimeOffset? TrackingStartedUtc,
    IReadOnlyList<WindowInfo> Windows,
    IReadOnlyList<VisibilitySegment> VisibilitySegments,
    IReadOnlyList<InteractionSecond> Interactions,
    IReadOnlyList<SessionEvent> SessionEvents);

public static class VisibilityRunLengthEncoder
{
    public static void Append(IList<VisibilitySegment> segments, VisibilitySegment next)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(next);

        for (var index = segments.Count - 1; index >= 0; index--)
        {
            var previous = segments[index];
            if (previous.WindowId != next.WindowId)
            {
                continue;
            }

            if (previous.EndUtc > next.StartUtc)
            {
                throw new ArgumentException("Visibility segments cannot overlap.", nameof(next));
            }

            if (previous.WindowId == next.WindowId &&
                previous.EndUtc == next.StartUtc &&
                previous.VisiblePercent == next.VisiblePercent &&
                previous.IsForeground == next.IsForeground)
            {
                segments[index] = new VisibilitySegment(
                    previous.WindowId,
                    previous.StartUtc,
                    next.EndUtc,
                    previous.VisiblePercent,
                    previous.IsForeground);
                return;
            }

            break;
        }

        segments.Add(next);
    }
}
