using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Core.Calculation;

/// <summary>Looks ahead in recorded input so expired idle tails can be removed retroactively.</summary>
public sealed class InputActivity
{
    private static readonly TimeSpan InputSecond = TimeSpan.FromSeconds(1);
    private readonly DateTimeOffset[] inputs;
    private readonly DateTimeOffset[] boundaries;
    private readonly DateTimeOffset end;
    public IReadOnlyList<DateTimeOffset> Inputs => inputs;

    private InputActivity(IEnumerable<DateTimeOffset> inputs, IEnumerable<DateTimeOffset> boundaries, DateTimeOffset end)
    {
        this.inputs = inputs.Select(x => x.ToUniversalTime()).Distinct().Order().ToArray();
        this.boundaries = boundaries.Select(x => x.ToUniversalTime()).Distinct().Order().ToArray();
        this.end = end;
    }

    public static InputActivity Create(WorkDayRawData raw, DateTimeOffset end)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var tracking = TrackingAvailability.GetNotTrackedIntervals(raw, end);
        var sessions = raw.SessionEvents.OrderBy(e => e.TimeUtc).ToArray();
        var boundaries = sessions.Where(e => e.Kind is SessionEventKind.Lock or SessionEventKind.Unlock or SessionEventKind.Suspend
                or SessionEventKind.Resume or SessionEventKind.Logoff or SessionEventKind.Logon or SessionEventKind.Shutdown)
            .Select(e => e.TimeUtc)
            .Concat(tracking.SelectMany(i => new[] { i.StartUtc, i.EndUtc })).Order().ToArray();
        var trackingStarts = tracking.Select(interval => interval.StartUtc).ToArray();
        var sessionTimes = new DateTimeOffset[sessions.Length];
        var sessionAvailable = new bool[sessions.Length];
        var locked = false;
        var suspended = false;
        var loggedOff = false;
        for (var index = 0; index < sessions.Length; index++)
        {
            var item = sessions[index];
            switch (item.Kind)
            {
                case SessionEventKind.Lock: locked = true; break;
                case SessionEventKind.Unlock: locked = false; break;
                case SessionEventKind.Suspend: suspended = true; break;
                case SessionEventKind.Resume: suspended = false; break;
                case SessionEventKind.Logoff:
                case SessionEventKind.Shutdown: loggedOff = true; break;
                case SessionEventKind.Logon: loggedOff = false; break;
            }
            sessionTimes[index] = item.TimeUtc;
            sessionAvailable[index] = !locked && !suspended && !loggedOff;
        }
        bool IsAvailable(DateTimeOffset time)
        {
            var interval = UpperBound(trackingStarts, time) - 1;
            if (interval >= 0 && time < tracking[interval].EndUtc) return false;
            var session = UpperBound(sessionTimes, time) - 1;
            return session < 0 || sessionAvailable[session];
        }
        var inputs = raw.Interactions.Where(input => input.Kind != InteractionKind.None && input.SecondUtc <= end &&
                input.SecondUtc >= (raw.TrackingStartedUtc ?? end) && IsAvailable(input.SecondUtc))
            .Select(input => input.SecondUtc)
            .Concat(raw.GlobalInputs.Where(input => input.ObservedUtc <= end &&
                    input.LastInputUtc >= (raw.TrackingStartedUtc ?? end) &&
                    IsAvailable(input.ObservedUtc) && IsAvailable(input.LastInputUtc) &&
                    (After(boundaries, input.LastInputUtc) is not { } boundary || boundary > input.ObservedUtc))
                .Select(input => input.LastInputUtc));
        return new InputActivity(inputs, boundaries, end);
    }

    public DateTimeOffset End(DateTimeOffset lastInput, TimeSpan timeout)
    {
        var next = After(inputs, lastInput);
        var boundary = After(boundaries, lastInput);
        if (next is { } input && input - lastInput < timeout &&
            (boundary is null || input < boundary))
            return input;
        if (end - lastInput < timeout && (boundary is null || boundary >= end))
            return end;
        return lastInput + (timeout < InputSecond ? timeout : InputSecond);
    }

    private static DateTimeOffset? After(DateTimeOffset[] values, DateTimeOffset value)
    {
        var index = UpperBound(values, value);
        return index < values.Length ? values[index] : null;
    }

    private static int UpperBound(DateTimeOffset[] values, DateTimeOffset value)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle] <= value) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
