using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Core.Calculation;

public static class WorkDayBoundaryCalculator
{
    public static WorkDay? CloseAutomaticallyIfDue(
        WorkDay day,
        IEnumerable<SessionEvent> sessionEvents,
        TimeSpan autoEndAfter,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(sessionEvents);
        if (autoEndAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(autoEndAfter));
        }

        if (day.EndUtc is not null)
        {
            return day;
        }

        var now = nowUtc.ToUniversalTime();
        var endOfEvents = sessionEvents
            .Where(sessionEvent => sessionEvent.TimeUtc >= day.StartUtc && sessionEvent.TimeUtc <= now)
            .OrderBy(sessionEvent => sessionEvent.TimeUtc)
            .ToArray();
        var locked = false;
        var suspended = false;
        var loggedOff = false;
        DateTimeOffset? unavailableSinceUtc = null;

        foreach (var sessionEvent in endOfEvents)
        {
            var wasUnavailable = locked || suspended || loggedOff;
            Apply(sessionEvent.Kind, ref locked, ref suspended, ref loggedOff);
            var isUnavailable = locked || suspended || loggedOff;

            if (!wasUnavailable && isUnavailable)
            {
                unavailableSinceUtc = sessionEvent.TimeUtc.ToUniversalTime();
            }
            else if (wasUnavailable && !isUnavailable)
            {
                unavailableSinceUtc = null;
            }
        }

        if (unavailableSinceUtc is not { } unavailableSince ||
            now - unavailableSince < autoEndAfter)
        {
            return null;
        }

        return day with
        {
            EndUtc = unavailableSince,
            EndReason = WorkDayEndReason.Auto4Hours
        };
    }

    private static void Apply(
        SessionEventKind kind,
        ref bool locked,
        ref bool suspended,
        ref bool loggedOff)
    {
        switch (kind)
        {
            case SessionEventKind.Lock:
                locked = true;
                break;
            case SessionEventKind.Unlock:
                locked = false;
                break;
            case SessionEventKind.Logoff:
            case SessionEventKind.Shutdown:
                loggedOff = true;
                break;
            case SessionEventKind.Logon:
                loggedOff = false;
                break;
            case SessionEventKind.Suspend:
                suspended = true;
                break;
            case SessionEventKind.Resume:
                suspended = false;
                break;
        }
    }
}
