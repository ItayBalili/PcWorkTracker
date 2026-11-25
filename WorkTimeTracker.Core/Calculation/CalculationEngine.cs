using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Core.Calculation;

public static class CalculationEngine
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    public static DayReport Calculate(WorkDayRawData data, TrackerSettings settings, DateTimeOffset asOfUtc) =>
        Run(data, settings, asOfUtc, null, null).Report;

    internal static CalculationRun Run(WorkDayRawData data, TrackerSettings settings, DateTimeOffset asOfUtc,
        CalculationCheckpoint? checkpoint, DateTimeOffset? checkpointTarget)
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
        var visibleWindowIds = segments.Keys.ToArray();
        var currentSegments = new VisibilitySegment?[visibleWindowIds.Length];
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
        var globalInputs = data.GlobalInputs.OrderBy(input => input.ObservedUtc).ToArray();
        if (globalInputs.Any(input => input.LastInputUtc > input.ObservedUtc))
            throw new ArgumentException("Global input cannot occur after its observation.", nameof(data));
        var notTrackedIntervals = TrackingAvailability.GetNotTrackedIntervals(data, endUtc);
        var poweredOffIntervals = TrackingAvailability.GetPoweredOffIntervals(data, endUtc);
        var activity = InputActivity.Create(data, endUtc);
        var trackingStartUtc = data.TrackingStartedUtc?.ToUniversalTime();
        var trackedStartUtc = trackingStartUtc is { } trackedStart
            ? Max(startUtc, trackedStart)
            : endUtc;
        var seed = checkpoint?.Clone() ?? new CalculationCheckpoint
        {
            Cursor = startUtc,
            RecordingSince = trackedStartUtc
        };
        foreach (var id in windows.Keys)
        {
            seed.WindowStates.TryAdd(id, new WindowState());
            seed.SegmentIndexes.TryAdd(id, 0);
            seed.DisplayedTime.TryAdd(id, TimeSpan.Zero);
            seed.ActiveTime.TryAdd(id, TimeSpan.Zero);
        }
        var windowStates = seed.WindowStates;
        var segmentIndexes = seed.SegmentIndexes;
        var displayedTime = seed.DisplayedTime;
        var activeTime = seed.ActiveTime;
        var notTrackedTime = seed.NotTrackedTime;
        var poweredOffTime = seed.PoweredOffTime;
        var cursor = seed.Cursor;
        var interactionIndex = seed.InteractionIndex;
        var sessionIndex = seed.SessionIndex;
        var trackingIndex = seed.TrackingIndex;
        var poweredOffIndex = seed.PoweredOffIndex;
        var globalInputIndex = seed.GlobalInputIndex;
        var activityIndex = seed.ActivityIndex;
        var recordingSince = seed.RecordingSince;
        var locked = seed.Locked;
        var suspended = seed.Suspended;
        var loggedOff = seed.LoggedOff;
        var lastGlobalInputUtc = seed.LastGlobalInputUtc;
        var awayTime = seed.AwayTime;
        CalculationCheckpoint? nextCheckpoint = null;

        while (cursor < endUtc)
        {
            if (checkpointTarget is { } target && cursor <= target && cursor > (checkpoint?.Cursor ?? startUtc) &&
                cursor >= target - Tick)
            {
                nextCheckpoint = new CalculationCheckpoint
                {
                    Cursor = cursor,
                    RecordingSince = recordingSince,
                    NotTrackedTime = notTrackedTime,
                    PoweredOffTime = poweredOffTime,
                    AwayTime = awayTime,
                    InteractionIndex = interactionIndex,
                    SessionIndex = sessionIndex,
                    TrackingIndex = trackingIndex,
                    PoweredOffIndex = poweredOffIndex,
                    GlobalInputIndex = globalInputIndex,
                    ActivityIndex = activityIndex,
                    Locked = locked,
                    Suspended = suspended,
                    LoggedOff = loggedOff,
                    LastGlobalInputUtc = lastGlobalInputUtc,
                    WindowStates = windowStates.ToDictionary(x => x.Key, x => new WindowState { LastInteractionUtc = x.Value.LastInteractionUtc }, StringComparer.Ordinal),
                    SegmentIndexes = new(segmentIndexes, StringComparer.Ordinal),
                    DisplayedTime = new(displayedTime, StringComparer.Ordinal),
                    ActiveTime = new(activeTime, StringComparer.Ordinal)
                };
            }
            while (poweredOffIndex < poweredOffIntervals.Count && poweredOffIntervals[poweredOffIndex].EndUtc <= cursor)
                poweredOffIndex++;
            if (poweredOffIndex < poweredOffIntervals.Count && poweredOffIntervals[poweredOffIndex].StartUtc <= cursor)
            {
                var intervalEnd = poweredOffIntervals[poweredOffIndex].EndUtc;
                poweredOffTime += intervalEnd - cursor;
                cursor = intervalEnd;
                recordingSince = intervalEnd;
                lastGlobalInputUtc = null;
                foreach (var state in windowStates.Values) state.LastInteractionUtc = null;
                continue;
            }
            while (trackingIndex < notTrackedIntervals.Count && notTrackedIntervals[trackingIndex].EndUtc <= cursor)
                trackingIndex++;
            if (trackingIndex < notTrackedIntervals.Count && notTrackedIntervals[trackingIndex].StartUtc <= cursor)
            {
                var intervalEnd = notTrackedIntervals[trackingIndex].EndUtc;
                notTrackedTime += intervalEnd - cursor;
                cursor = intervalEnd;
                recordingSince = intervalEnd;
                lastGlobalInputUtc = null;
                foreach (var state in windowStates.Values) state.LastInteractionUtc = null;
                continue;
            }

            while (sessionIndex < sessionEvents.Length && sessionEvents[sessionIndex].TimeUtc.ToUniversalTime() <= cursor)
            {
                ApplySessionEvent(sessionEvents[sessionIndex++].Kind, ref locked, ref suspended, ref loggedOff);
            }

            while (interactionIndex < interactions.Length && interactions[interactionIndex].SecondUtc.ToUniversalTime() <= cursor)
            {
                var interaction = interactions[interactionIndex++];
                if (interaction.SecondUtc < recordingSince) continue;
                var state = windowStates[interaction.WindowId];
                state.LastInteractionUtc = interaction.SecondUtc;
                lastGlobalInputUtc = interaction.SecondUtc;
            }

            while (globalInputIndex < globalInputs.Length && globalInputs[globalInputIndex].ObservedUtc <= cursor)
            {
                var observation = globalInputs[globalInputIndex++];
                if (observation.ObservedUtc < recordingSince) continue;
                if (lastGlobalInputUtc == null || observation.LastInputUtc > lastGlobalInputUtc)
                    lastGlobalInputUtc = observation.LastInputUtc;
            }
            while (activityIndex < activity.Inputs.Count && activity.Inputs[activityIndex] <= cursor)
            {
                var inputTime = activity.Inputs[activityIndex++];
                if (inputTime >= recordingSince && (lastGlobalInputUtc is null || inputTime > lastGlobalInputUtc))
                    lastGlobalInputUtc = inputTime;
            }

            var duration = Min(Tick, endUtc - cursor);
            if (trackingIndex < notTrackedIntervals.Count)
                duration = Min(duration, notTrackedIntervals[trackingIndex].StartUtc - cursor);
            if (poweredOffIndex < poweredOffIntervals.Count)
                duration = Min(duration, poweredOffIntervals[poweredOffIndex].StartUtc - cursor);
            if (sessionIndex < sessionEvents.Length)
                duration = Min(duration, sessionEvents[sessionIndex].TimeUtc - cursor);
            if (interactionIndex < interactions.Length)
                duration = Min(duration, interactions[interactionIndex].SecondUtc - cursor);
            if (globalInputIndex < globalInputs.Length)
                duration = Min(duration, globalInputs[globalInputIndex].ObservedUtc - cursor);
            if (activityIndex < activity.Inputs.Count)
                duration = Min(duration, activity.Inputs[activityIndex] - cursor);
            for (var i = 0; i < visibleWindowIds.Length; i++)
            {
                var windowId = visibleWindowIds[i];
                var segment = GetSegmentAt(windowId, cursor, segments, segmentIndexes);
                currentSegments[i] = segment;
                if (segment is not null)
                    duration = Min(duration, segment.EndUtc - cursor);
                else if (segments.TryGetValue(windowId, out var futureSegments) && segmentIndexes[windowId] < futureSegments.Length)
                    duration = Min(duration, futureSegments[segmentIndexes[windowId]].StartUtc - cursor);
            }
            var presenceEnd = lastGlobalInputUtc is { } knownInput ? activity.End(knownInput, settings.AwayTimeout) : cursor;
            var activeEnd = lastGlobalInputUtc is { } activeInput ? activity.End(activeInput, settings.ActiveTimeout) : cursor;
            if (presenceEnd > cursor) duration = Min(duration, presenceEnd - cursor);
            if (activeEnd > cursor) duration = Min(duration, activeEnd - cursor);
            var isUnavailable = locked || suspended || loggedOff;
            var userPresent = !isUnavailable &&
                lastGlobalInputUtc is not null && cursor < presenceEnd;

            if (isUnavailable || !userPresent)
            {
                awayTime += duration;
            }

            for (var i = 0; i < visibleWindowIds.Length; i++)
            {
                var windowId = visibleWindowIds[i];
                var state = windowStates[windowId];
                var segment = currentSegments[i];
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

                var inputIsRecent = lastGlobalInputUtc is not null && cursor < activeEnd;
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

        var report = new DayReport(
            day,
            endUtc,
            endUtc - startUtc,
            notTrackedTime,
            awayTime,
            displayedTime.Values.Aggregate(TimeSpan.Zero, (total, duration) => total + duration),
            activeTime.Values.Aggregate(TimeSpan.Zero, (total, duration) => total + duration),
            windowReports) { PoweredOffTime = poweredOffTime };
        return new CalculationRun(report, nextCheckpoint);
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
            case SessionEventKind.Boot:
                locked = suspended = loggedOff = false;
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

    internal sealed class WindowState
    {
        public DateTimeOffset? LastInteractionUtc { get; set; }
    }

    internal sealed class CalculationCheckpoint
    {
        internal DateTimeOffset Cursor, RecordingSince;
        internal TimeSpan NotTrackedTime, AwayTime, PoweredOffTime;
        internal int InteractionIndex, SessionIndex, TrackingIndex, PoweredOffIndex, GlobalInputIndex, ActivityIndex;
        internal bool Locked, Suspended, LoggedOff;
        internal DateTimeOffset? LastGlobalInputUtc;
        internal Dictionary<string, WindowState> WindowStates = new(StringComparer.Ordinal);
        internal Dictionary<string, int> SegmentIndexes = new(StringComparer.Ordinal);
        internal Dictionary<string, TimeSpan> DisplayedTime = new(StringComparer.Ordinal);
        internal Dictionary<string, TimeSpan> ActiveTime = new(StringComparer.Ordinal);

        internal CalculationCheckpoint Clone() => new()
        {
            Cursor = Cursor, RecordingSince = RecordingSince, NotTrackedTime = NotTrackedTime, AwayTime = AwayTime, PoweredOffTime = PoweredOffTime,
            InteractionIndex = InteractionIndex, SessionIndex = SessionIndex, TrackingIndex = TrackingIndex,
            PoweredOffIndex = PoweredOffIndex, GlobalInputIndex = GlobalInputIndex, ActivityIndex = ActivityIndex,
            Locked = Locked, Suspended = Suspended, LoggedOff = LoggedOff, LastGlobalInputUtc = LastGlobalInputUtc,
            WindowStates = WindowStates.ToDictionary(x => x.Key, x => new WindowState { LastInteractionUtc = x.Value.LastInteractionUtc }, StringComparer.Ordinal),
            SegmentIndexes = new(SegmentIndexes, StringComparer.Ordinal),
            DisplayedTime = new(DisplayedTime, StringComparer.Ordinal), ActiveTime = new(ActiveTime, StringComparer.Ordinal)
        };
    }

    internal sealed record CalculationRun(DayReport Report, CalculationCheckpoint? Checkpoint);

}
