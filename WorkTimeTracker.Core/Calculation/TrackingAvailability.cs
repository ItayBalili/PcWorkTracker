using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Core.Calculation;

public sealed record TrackingInterval(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>Pure, merged collector-unavailable intervals; OS absence is deliberately separate.</summary>
public static class TrackingAvailability
{
    public static IReadOnlyList<TrackingInterval> GetNotTrackedIntervals(WorkDayRawData data, DateTimeOffset endUtc)
    {
        ArgumentNullException.ThrowIfNull(data);
        var start = data.WorkDay.StartUtc.ToUniversalTime();
        var end = (data.WorkDay.EndUtc ?? endUtc).ToUniversalTime();
        if (end < start) throw new ArgumentException("The work day ends before it starts.", nameof(data));
        var intervals = new List<TrackingInterval>();
        void Add(DateTimeOffset from, DateTimeOffset to)
        {
            from = from < start ? start : from;
            to = to > end ? end : to;
            if (from < to) intervals.Add(new(from, to));
        }
        Add(start, data.TrackingStartedUtc ?? end);
        var events = data.SessionEvents.Where(e => e.Kind is SessionEventKind.AppStart or SessionEventKind.AppStop)
            .Select(e => (e.TimeUtc, Kind: e.Kind == SessionEventKind.AppStart ? 0 : 1))
            .Concat(data.TrackingEvents.Where(e => e.Kind != TrackingEventKind.Recovery).Select(e =>
                (e.TimeUtc, Kind: e.Kind switch
                {
                    TrackingEventKind.Pause => 2,
                    TrackingEventKind.Resume => 3,
                    _ => throw new ArgumentException("Unknown tracking event.", nameof(data))
                })))
            .OrderBy(e => e.TimeUtc).ToArray();
        var running = true;
        var paused = false;
        var cursor = start;
        foreach (var item in events)
        {
            if (item.TimeUtc > end) break;
            var time = item.TimeUtc < start ? start : item.TimeUtc;
            if (!running || paused) Add(cursor, time);
            cursor = time;
            switch (item.Kind)
            {
                case 0: running = true; break;
                case 1: running = false; break;
                case 2: paused = true; break;
                case 3: paused = false; break;
            }
        }
        if (!running || paused) Add(cursor, end);
        foreach (var recovery in data.TrackingEvents.Where(e => e.Kind == TrackingEventKind.Recovery))
        {
            if (recovery.EndUtc < recovery.TimeUtc) throw new ArgumentException("Recovery ends before its watermark.", nameof(data));
            Add(recovery.TimeUtc, recovery.EndUtc ?? end);
        }
        var merged = new List<TrackingInterval>();
        foreach (var interval in intervals.OrderBy(i => i.StartUtc))
        {
            if (merged.Count == 0 || merged[^1].EndUtc < interval.StartUtc) merged.Add(interval);
            else if (interval.EndUtc > merged[^1].EndUtc) merged[^1] = merged[^1] with { EndUtc = interval.EndUtc };
        }
        return merged;
    }
}
