using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Tests;

public sealed class TrackingAccountingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    [Fact]
    public void FractionalPauseMergesWithTheUntrackedPrefixWithoutDoubleCounting()
    {
        var raw = Raw(trackingStarted: At(2)) with
        {
            TrackingEvents = [new(At(1.25), TrackingEventKind.Pause), new(At(4.75), TrackingEventKind.Resume)]
        };
        var report = Calculate(raw);
        Assert.Equal(TimeSpan.FromSeconds(4.75), report.NotTrackedTime);
        Assert.Equal(TimeSpan.FromSeconds(5.25), report.AwayTime);
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 4)]
    public void ManualPauseAndStoppedAppAreIndependent(bool resumeWhileStopped, int notTrackedSeconds)
    {
        var raw = Raw(
            sessions: [new(At(2), SessionEventKind.AppStop), new(At(5), SessionEventKind.AppStart)]) with
        {
            TrackingEvents =
            [
                new(At(1), TrackingEventKind.Pause),
                new(At(resumeWhileStopped ? 3 : 8), TrackingEventKind.Resume)
            ]
        };
        var report = Calculate(raw);
        Assert.Equal(TimeSpan.FromSeconds(notTrackedSeconds), report.NotTrackedTime);
        Assert.Equal(TimeSpan.FromSeconds(10 - notTrackedSeconds), report.AwayTime);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 10)]
    public void SameInstantStartStopPreservesTheActualSourceOrder(bool stoppedLast, int untrackedSeconds)
    {
        SessionEvent begin = new(Start, SessionEventKind.AppStart);
        SessionEvent stop = new(Start, SessionEventKind.AppStop);
        var raw = Raw(sessions: stoppedLast ? [begin, stop] : [stop, begin]);
        Assert.Equal(TimeSpan.FromSeconds(untrackedSeconds), Calculate(raw).NotTrackedTime);
    }

    [Fact]
    public void BoundedRecoveryAtThePriorStartWatermarkDoesNotInventTrackedOfflineTime()
    {
        var raw = Raw(sessions: [new(At(2), SessionEventKind.AppStart), new(At(7), SessionEventKind.AppStart)]) with
        {
            TrackingEvents = [new(At(2), TrackingEventKind.Recovery) { EndUtc = At(7) }]
        };
        var report = Calculate(raw);
        Assert.Equal(TimeSpan.FromSeconds(5), report.NotTrackedTime);
        Assert.Equal(TimeSpan.FromSeconds(5), report.AwayTime);
    }

    [Fact]
    public void AnUnfinishedRecoveryRemainsUntrackedWithoutAnOsLock()
    {
        var report = Calculate(Raw() with { TrackingEvents = [new(At(2), TrackingEventKind.Recovery)] });
        Assert.Equal(TimeSpan.FromSeconds(8), report.NotTrackedTime);
        Assert.Equal(TimeSpan.FromSeconds(2), report.AwayTime);
    }

    [Fact]
    public void PauseDoesNotRetainOldInputOrAcceptInputFromInsideThePausedInterval()
    {
        WindowInfo window = new("editor", "editor", "Editor", null, "Notes");
        var raw = Raw() with
        {
            Windows = [window],
            VisibilitySegments = [new(window.Id, Start, At(10), 100m, true)],
            Interactions =
            [
                new(window.Id, Start, InteractionKind.Keyboard),
                new(window.Id, At(3), InteractionKind.Keyboard),
                new(window.Id, At(6), InteractionKind.Click)
            ],
            TrackingEvents = [new(At(1.25), TrackingEventKind.Pause), new(At(4.75), TrackingEventKind.Resume)]
        };
        var report = Calculate(raw);
        Assert.Equal(TimeSpan.FromSeconds(3.5), report.NotTrackedTime);
        Assert.Equal(TimeSpan.FromSeconds(1.25), report.AwayTime);
        Assert.Equal(TimeSpan.FromSeconds(5.25), report.TotalActiveTime);
    }

    [Fact]
    public void AnOsLockSurvivesManualPauseAndResumeButPausedTimeIsNotAway()
    {
        WindowInfo window = new("editor", "editor", "Editor", null, "Notes");
        var raw = Raw(sessions: [new(At(2), SessionEventKind.Lock), new(At(9), SessionEventKind.Unlock)]) with
        {
            Windows = [window],
            VisibilitySegments = [new(window.Id, Start, At(10), 100m, true)],
            Interactions = [new(window.Id, Start, InteractionKind.Click), new(window.Id, At(8), InteractionKind.Click)],
            TrackingEvents = [new(At(3), TrackingEventKind.Pause), new(At(7), TrackingEventKind.Resume)]
        };
        var report = Calculate(raw);
        Assert.Equal(TimeSpan.FromSeconds(4), report.NotTrackedTime);
        Assert.Equal(TimeSpan.FromSeconds(3), report.AwayTime);
        Assert.Equal(TimeSpan.FromSeconds(3), report.TotalActiveTime);
    }

    [Fact]
    public void GlobalIdleCrossCheckProvidesPresenceWithoutAWindowOrInteraction()
    {
        var raw = Raw() with { GlobalInputs = [new(Start, Start)] };
        var report = CalculationEngine.Calculate(raw, new() { AwayTimeout = TimeSpan.FromSeconds(2.5) }, At(10));
        Assert.Equal(TimeSpan.FromSeconds(7.5), report.AwayTime);
        Assert.Equal(TimeSpan.Zero, report.NotTrackedTime);
        Assert.Empty(report.Windows);
        Assert.Equal(TimeSpan.Zero, report.TotalActiveTime);
    }

    [Fact]
    public void GlobalPresenceDoesNotInventBackgroundWindowInteractionRecency()
    {
        WindowInfo background = new("background", "editor", "Editor", null, "Notes");
        var raw = Raw() with
        {
            Windows = [background],
            VisibilitySegments = [new(background.Id, Start, At(10), 100m, false)],
            GlobalInputs = [new(Start, Start)]
        };
        var report = Calculate(raw);
        Assert.Equal(TimeSpan.Zero, report.AwayTime);
        Assert.Equal(TimeSpan.Zero, report.TotalDisplayedTime);
        Assert.Equal(TimeSpan.Zero, report.TotalActiveTime);
        Assert.Empty(raw.Interactions);
    }

    [Fact]
    public void OnlyAFreshGlobalObservationAfterResumeCanRestorePresence()
    {
        var raw = Raw() with
        {
            TrackingEvents = [new(At(1), TrackingEventKind.Pause), new(At(5), TrackingEventKind.Resume)],
            GlobalInputs = [new(At(2), At(2)), new(At(6), At(5.5))]
        };
        var report = Calculate(raw);
        Assert.Equal(TimeSpan.FromSeconds(4), report.NotTrackedTime);
        Assert.Equal(TimeSpan.FromSeconds(2), report.AwayTime);
    }

    private static WorkDayRawData Raw(DateTimeOffset? trackingStarted = null, IReadOnlyList<SessionEvent>? sessions = null) =>
        new(new(DateOnly.FromDateTime(Start.DateTime), Start, At(10), WorkDayEndReason.Manual),
            trackingStarted ?? Start, [], [], [], sessions ?? []);

    private static DayReport Calculate(WorkDayRawData raw) => CalculationEngine.Calculate(raw, new(), At(10));
}
