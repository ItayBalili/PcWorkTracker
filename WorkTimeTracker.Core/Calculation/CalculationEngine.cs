using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Core.Calculation;

public static class CalculationEngine
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    public static DayReport Calculate(WorkDayRawData data, TrackerSettings settings, DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        var day = data.WorkDay ?? throw new ArgumentException("A work day is required.", nameof(data));
        var endUtc = (day.EndUtc ?? asOfUtc).ToUniversalTime();
        var startUtc = day.StartUtc.ToUniversalTime();
        if (endUtc < startUtc)
        {
            throw new ArgumentException("The work day ends before it starts.", nameof(data));
        }

        var windows = data.Windows.ToDictionary(window => window.Id, StringComparer.Ordinal);
        var segments = data.VisibilitySegments
            .GroupBy(segment => segment.WindowId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(segment => segment.StartUtc).ToArray(), StringComparer.Ordinal);
        ValidateSegments(segments, windows);
        foreach (var interaction in data.Interactions)
        {
            if (!windows.ContainsKey(interaction.WindowId))
            {
                throw new ArgumentException($"Unknown interaction window id '{interaction.WindowId}'.", nameof(data));
            }
        }

        var interactions = data.Interactions
            .Where(interaction => interaction.Kind != InteractionKind.None)
            .Select(interaction => interaction with { SecondUtc = interaction.SecondUtc.ToUniversalTime() })
            .OrderBy(interaction => interaction.SecondUtc)
            .ToArray();
        var sessionEvents = data.SessionEvents.OrderBy(sessionEvent => sessionEvent.TimeUtc).ToArray();
        var windowStates = windows.Keys.ToDictionary(
            id => id,
            _ => new WindowState(),
            StringComparer.Ordinal);
        var segmentIndexes = windows.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var displayedTime = windows.Keys.ToDictionary(id => id, _ => TimeSpan.Zero, StringComparer.Ordinal);
        var activeTime = windows.Keys.ToDictionary(id => id, _ => TimeSpan.Zero, StringComparer.Ordinal);

        var trackingStartUtc = data.TrackingStartedUtc?.ToUniversalTime();
        var trackedStartUtc = trackingStartUtc is { } trackedStart
            ? Max(startUtc, trackedStart)
            : endUtc;
        var notTrackedTime = Max(TimeSpan.Zero, Min(endUtc, trackedStartUtc) - startUtc);
        var cursor = trackedStartUtc;
        var interactionIndex = 0;
        var sessionIndex = 0;
        var locked = false;
        var suspended = false;
        var loggedOff = false;
        DateTimeOffset? lastGlobalInputUtc = null;
        var awayTime = TimeSpan.Zero;

        while (cursor < endUtc)
        {
            while (sessionIndex < sessionEvents.Length && sessionEvents[sessionIndex].TimeUtc.ToUniversalTime() <= cursor)
            {
                ApplySessionEvent(sessionEvents[sessionIndex++].Kind, ref locked, ref suspended, ref loggedOff);
            }

            while (interactionIndex < interactions.Length && interactions[interactionIndex].SecondUtc.ToUniversalTime() <= cursor)
            {
                var interaction = interactions[interactionIndex++];
                var state = windowStates[interaction.WindowId];
                state.LastInteractionUtc = interaction.SecondUtc;
                lastGlobalInputUtc = interaction.SecondUtc;
            }

            var duration = Min(Tick, endUtc - cursor);
            var isUnavailable = locked || suspended || loggedOff;
            var userPresent = !isUnavailable &&
                lastGlobalInputUtc is { } globalInput &&
                cursor - globalInput < settings.AwayTimeout;

            if (isUnavailable || !userPresent)
            {
                awayTime += duration;
            }

            foreach (var windowId in windows.Keys)
            {
                var state = windowStates[windowId];
                var segment = GetSegmentAt(windowId, cursor, segments, segmentIndexes);
                var isForeground = segment?.IsForeground == true;
                if (isForeground)
                {
                    state.LastInteractionUtc = cursor;
                }

                if (!userPresent)
                {
                    continue;
                }

                var visible = segment is not null && segment.VisiblePercent >= settings.MinimumVisiblePercent;
                var interactionIsRecent = state.LastInteractionUtc is { } lastInteraction &&
                    cursor - lastInteraction < settings.InteractionWindow;
                if (visible && (isForeground || interactionIsRecent))
                {
                    displayedTime[windowId] += duration;
                }

                var inputIsRecent = lastGlobalInputUtc is { } lastInput &&
                    cursor - lastInput < settings.ActiveTimeout;
                if (isForeground && inputIsRecent)
                {
                    activeTime[windowId] += duration;
                }
            }

            cursor += duration;
        }

        var windowReports = windows.Values
            .Select(window => new WindowMetrics(
                window,
                displayedTime[window.Id],
                activeTime[window.Id]))
            .OrderByDescending(metrics => metrics.ActiveTime)
            .ThenBy(metrics => metrics.Window.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(metrics => metrics.Window.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DayReport(
            day,
            endUtc,
            endUtc - startUtc,
            notTrackedTime,
            awayTime,
            displayedTime.Values.Aggregate(TimeSpan.Zero, (total, duration) => total + duration),
            activeTime.Values.Aggregate(TimeSpan.Zero, (total, duration) => total + duration),
            windowReports);
    }

    private static void ValidateSegments(
        IReadOnlyDictionary<string, VisibilitySegment[]> segments,
        IReadOnlyDictionary<string, WindowInfo> windows)
    {
        foreach (var (windowId, windowSegments) in segments)
        {
            if (!windows.ContainsKey(windowId))
            {
                throw new ArgumentException($"Unknown visibility window id '{windowId}'.", nameof(segments));
            }

            for (var index = 1; index < windowSegments.Length; index++)
            {
                if (windowSegments[index - 1].EndUtc > windowSegments[index].StartUtc)
                {
                    throw new ArgumentException($"Visibility segments overlap for window '{windowId}'.", nameof(segments));
                }
            }

            var foregroundSegments = segments.Values
                .SelectMany(windowSegments => windowSegments)
                .Where(segment => segment.IsForeground)
                .OrderBy(segment => segment.StartUtc)
                .ToArray();
            for (var index = 1; index < foregroundSegments.Length; index++)
            {
                if (foregroundSegments[index - 1].EndUtc > foregroundSegments[index].StartUtc)
                {
                    throw new ArgumentException("Foreground visibility segments cannot overlap.", nameof(segments));
                }
            }
        }
    }

    private static VisibilitySegment? GetSegmentAt(
        string windowId,
        DateTimeOffset timeUtc,
        IReadOnlyDictionary<string, VisibilitySegment[]> segments,
        IDictionary<string, int> indexes)
    {
        if (!segments.TryGetValue(windowId, out var windowSegments))
        {
            return null;
        }

        var index = indexes[windowId];
        while (index < windowSegments.Length && windowSegments[index].EndUtc <= timeUtc)
        {
            index++;
        }

        indexes[windowId] = index;
        return index < windowSegments.Length &&
            windowSegments[index].StartUtc <= timeUtc &&
            windowSegments[index].EndUtc > timeUtc
            ? windowSegments[index]
            : null;
    }

    private static void ApplySessionEvent(
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

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private sealed class WindowState
    {
        public DateTimeOffset? LastInteractionUtc { get; set; }
    }
}
