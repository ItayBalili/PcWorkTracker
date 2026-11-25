using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WorkTimeTracker.Core.Abstractions;
using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Data;
using Microsoft.Data.Sqlite;

namespace WorkTimeTracker.App.HeadlessTests;

public sealed class TrackingServiceTests
{
    [Fact]
    public async Task CompletedSamplesAreExclusiveAndCrashGapsAreNeverExtended()
    {
        await using var test = new Driver();
        await test.Start();
        await test.Sample(0, Window("one"), Window("two"));
        await test.Sample(1, Window("two"), Window("one"));
        await test.Sample(2, Window("one", "renamed"), Window("two"));
        await test.Sample(20, Window("one", "renamed"));
        await test.Tracker.StopAsync();
        var raw = test.Raw();
        Assert.All(raw.VisibilitySegments, s => Assert.True(s.EndUtc <= test.At(2)));
        Assert.Equal(2, raw.Windows.Count(w => w.ProcessName == "one"));
        CalculationEngine.Calculate(raw, new(), test.At(20));
        Assert.Equal(TrackingState.Stopped, test.Tracker.Status.State);
        Assert.True(test.Input.Disposed && test.Session.Disposed && test.Resolver.Disposed);
    }

    [Fact]
    public async Task InputUsesTheSameMetadataVersionEvenWhenNotInLatestVisibilitySample()
    {
        await using var test = new Driver();
        await test.Start();
        var old = Window("one", "before");
        var renamed = Window("one", "after");
        await test.Sample(0, old);
        await test.Sample(1, old);
        var oldIdentity = test.Resolver.Map(old);
        await test.Sample(2, renamed);
        await test.Sample(3, Window("two"));
        test.Resolver.ForInput = oldIdentity;
        test.Input.Send(new(old.Id, test.At(1), InteractionKind.Click));
        await Until(() => test.Raw().Interactions.Count == 1);
        var interaction = Assert.Single(test.Raw().Interactions);
        Assert.Equal(oldIdentity.Id, interaction.WindowId);
        Assert.Contains(test.Raw().Windows, w => w.Id == interaction.WindowId && w.Title == "before");
        // A completely unsampled but identity-resolved window is also ingestible.
        var unsampled = test.Resolver.Map(Window("not-sampled"));
        test.Resolver.ForInput = unsampled;
        test.Input.Send(new("not-sampled", test.At(2), InteractionKind.Hover));
        await Until(() => test.Raw().Interactions.Count == 2);
        Assert.Contains(test.Raw().Windows, w => w.Id == unsampled.Id);
    }

    [Fact]
    public async Task ExcludedUnknownAndPauseBoundarySecondsAreNotRecorded()
    {
        await using var test = new Driver(new TrackerSettings
        {
            ExcludedApplications = new HashSet<string> { "PRIVATE.EXE" }
        });
        await test.Start();
        await test.Sample(0, Window("private"), Window("allowed"));
        await test.Sample(1, Window("private"), Window("allowed"));
        test.Resolver.ForInput = test.Resolver.Map(Window("private"));
        test.Input.Send(new("private", test.At(0), InteractionKind.Keyboard));
        await Until(() => test.Tracker.RejectedInteractions == 1);
        test.Resolver.ForInput = null;
        test.Input.Send(new("unknown", test.At(0), InteractionKind.Click));
        await Until(() => test.Tracker.RejectedInteractions == 2);
        await test.Tracker.PauseAsync();
        test.Resolver.ForInput = test.Resolver.Map(Window("allowed"));
        test.Input.Send(new("allowed", test.At(1), InteractionKind.Click));
        await Until(() => test.Tracker.RejectedInteractions == 3);
        await test.Sample(2, Window("allowed"));
        await test.Tracker.ResumeAsync();
        await test.Sample(3, Window("allowed"));
        await test.Sample(4, Window("allowed"));
        var raw = test.Raw();
        Assert.DoesNotContain(raw.Windows, w => w.ProcessName == "private");
        Assert.Empty(raw.Interactions);
        Assert.DoesNotContain(raw.VisibilitySegments, s => s.StartUtc < test.At(3) && s.EndUtc > test.At(1));
        using var connection = new SqliteConnection($"Data Source={test.Path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM TrackingLifecycle WHERE Kind IN ('Pause','Resume');";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [Fact]
    public async Task ElevatedWindowsWithoutImagePathAreRecordedAsUnknownWithTitle()
    {
        await using var test = new Driver(new TrackerSettings());
        await test.Start();
        var elevated = new WindowInfo("elevated", WorkTimeTracker.Windows.WindowsProcessNames.Unknown, WorkTimeTracker.Windows.WindowsProcessNames.UnknownElevated, null, "Administrator: Command Prompt");
        var pathless = new WindowInfo("pathless", "pathless", "pathless", null, "no path");
        await test.Sample(0, elevated, pathless);
        test.Resolver.ForInput = test.Resolver.Map(elevated);
        test.Input.Send(new("elevated", test.At(0), InteractionKind.Keyboard));
        await test.Sample(1, elevated, pathless);
        await test.Sample(2, elevated, pathless);
        var raw = test.Raw();
        var window = Assert.Single(raw.Windows);
        Assert.Equal(WorkTimeTracker.Windows.WindowsProcessNames.UnknownElevated, window.FriendlyName);
        Assert.Equal("Administrator: Command Prompt", window.Title);
        Assert.NotEmpty(raw.VisibilitySegments);
        Assert.Single(raw.Interactions);
    }

    [Fact]
    public async Task LockAndSuspendAreIndependentAndOutOfOrderEventsAreSorted()
    {
        await using var test = new Driver();
        await test.Start();
        await test.Sample(0, Window("one"));
        test.Clock.Set(test.At(4));
        test.Session.Send(new(test.At(1), SessionEventKind.Lock));
        test.Session.Send(new(test.At(3), SessionEventKind.Unlock));
        test.Session.Send(new(test.At(2), SessionEventKind.Suspend));
        await Until(() => test.Raw().SessionEvents.Count == 4);
        Assert.Equal(TrackingState.Unavailable, test.Tracker.Status.State);
        test.Session.Send(new(test.At(4), SessionEventKind.Resume));
        await Until(() => test.Tracker.Status.State == TrackingState.Tracking);
        await test.Sample(5, Window("one"));
        await test.Sample(6, Window("one"));
        Assert.All(test.Raw().VisibilitySegments, s => Assert.True(s.StartUtc >= test.At(5)));
    }

    [Fact]
    public async Task FourHourReturnClosesAtUnavailableStartBeforeCreatingANewDay()
    {
        await using var test = new Driver();
        await test.Start();
        var firstId = test.Tracker.Status.WorkDayId;
        test.Clock.Set(test.At(1));
        test.Session.Send(new(test.At(1), SessionEventKind.Lock));
        await Until(() => test.Tracker.Status.State == TrackingState.Unavailable);
        var returned = test.At(4 * 3600 + 2);
        test.Clock.Set(returned);
        test.Session.Send(new(returned, SessionEventKind.Unlock));
        await Until(() => test.Tracker.Status.WorkDayId != firstId);
        using var repository = test.Repository();
        var old = repository.GetWorkDay(firstId!.Value)!;
        Assert.Equal(test.At(1), old.WorkDay.EndUtc);
        Assert.Equal(WorkDayEndReason.Auto4Hours, old.WorkDay.EndReason);
        Assert.Equal(returned, test.Tracker.Status.WorkDay!.StartUtc);
    }

    [Fact]
    public async Task InactivityClosesAtLastObservedInputAndNewInputStartsANewDay()
    {
        var lastInput = Driver.Epoch;
        var current = Driver.Epoch;
        await using var test = new Driver(new TrackerSettings { AutoEndAfter = TimeSpan.FromSeconds(10) },
            getIdleDuration: () => current - lastInput);
        await test.Start();
        await Until(() => test.Raw().GlobalInputs.Count > 0);
        var oldId = test.Tracker.Status.WorkDayId!.Value;
        current = test.At(9); test.Clock.Set(current);
        await Until(() => test.Raw().GlobalInputs.Any(x => x.ObservedUtc == test.At(9)));
        Assert.Equal(TrackingState.Tracking, test.Tracker.Status.State);
        current = test.At(10); test.Clock.Set(current);
        await Until(() => test.Tracker.Status.State == TrackingState.DayEnded);
        using (var repository = test.Repository())
            Assert.Equal(test.At(0), repository.GetWorkDay(oldId)!.WorkDay.EndUtc);
        lastInput = test.At(20);
        current = test.At(20); test.Clock.Set(current);
        await Until(() => test.Tracker.Status.WorkDayId != oldId);
        Assert.Equal(test.At(20), test.Tracker.Status.WorkDay!.StartUtc);
    }

    [Fact]
    public async Task InactivityDoesNotCloseUsingInputFromBeforeThisRun()
    {
        var current = Driver.Epoch;
        var lastInput = Driver.Epoch.AddHours(-8);
        await using var test = new Driver(new TrackerSettings { AutoEndAfter = TimeSpan.FromSeconds(10) },
            getIdleDuration: () => current - lastInput);
        await test.Start();
        await Until(() => test.Raw().GlobalInputs.Count > 0);
        current = test.At(20); test.Clock.Set(current);
        await Until(() => test.Raw().GlobalInputs.Any(x => x.ObservedUtc == current));
        Assert.Equal(TrackingState.Tracking, test.Tracker.Status.State);
        Assert.Null(test.Tracker.Status.WorkDay!.EndUtc);
    }

    [Fact]
    public async Task InputAfterLongGapRollsOverEvenWhenIdlePollWasDelayed()
    {
        var current = Driver.Epoch;
        var lastInput = Driver.Epoch;
        await using var test = new Driver(new TrackerSettings { AutoEndAfter = TimeSpan.FromSeconds(10) },
            getIdleDuration: () => current - lastInput);
        await test.Start();
        await Until(() => test.Raw().GlobalInputs.Count > 0);
        var previous = test.Tracker.Status.WorkDayId!.Value;
        current = lastInput = test.At(20); test.Clock.Set(current);
        await Until(() => test.Tracker.Status.WorkDayId != previous);
        using var repository = test.Repository();
        Assert.Equal(test.At(0), repository.GetWorkDay(previous)!.WorkDay.EndUtc);
        Assert.Equal(test.At(20), test.Tracker.Status.WorkDay!.StartUtc);
    }

    [Fact]
    public async Task InvalidDayTransitionsDoNotStopTrackingAndManualEndRequiresExplicitStart()
    {
        await using var test = new Driver();
        await test.Start();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(test.Tracker.StartDayAsync);
        Assert.Equal(TrackingState.Tracking, test.Tracker.Status.State);
        test.Clock.Set(test.At(1));
        await test.Tracker.EndDayAsync();
        Assert.Equal(TrackingState.DayEnded, test.Tracker.Status.State);
        var firstId = test.Tracker.Status.WorkDayId;
        test.Session.Send(new(test.At(1), SessionEventKind.Unlock));
        await test.Sample(2, Window("one"));
        Assert.Equal(firstId, test.Tracker.Status.WorkDayId);
        await test.Tracker.StartDayAsync();
        Assert.NotEqual(firstId, test.Tracker.Status.WorkDayId);
        Assert.Equal(TrackingState.Tracking, test.Tracker.Status.State);
    }

    [Fact]
    public async Task RecoveryPreservesCommittedRowsWithoutBridgingOfflineTimeAndPreservesPause()
    {
        await using var first = new Driver();
        await first.Start();
        await first.Sample(0, Window("one"));
        await first.Sample(1, Window("one"));
        var dayId = first.Tracker.Status.WorkDayId;
        await first.Tracker.PauseAsync();
        await first.Tracker.StopAsync();
        await using var second = new Driver(path: first.Path, start: first.At(100));
        await second.Start();
        Assert.Equal(dayId, second.Tracker.Status.WorkDayId);
        Assert.Equal(TrackingState.Paused, second.Tracker.Status.State);
        await second.Tracker.ResumeAsync();
        await second.Sample(0, Window("one"));
        await second.Sample(1, Window("one"));
        var raw = second.Raw();
        Assert.Equal(2, raw.VisibilitySegments.Count);
        Assert.DoesNotContain(raw.VisibilitySegments, s => s.StartUtc < first.At(100) && s.EndUtc > first.At(1));
    }

    [Fact]
    public async Task CollectorFailuresAreSurfacedAndAllCollectorsAreDisposed()
    {
        var test = new Driver();
        try
        {
            await test.Start();
            var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            test.Tracker.Error += e => observed.TrySetResult(e);
            test.Input.Fail(new IOException("test hook failure"));
            Assert.IsType<IOException>(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAnyAsync<Exception>(() => test.Tracker.Completion);
            await Assert.ThrowsAsync<AggregateException>(test.Tracker.StopAsync);
            Assert.True(test.Input.Disposed && test.Session.Disposed && test.Resolver.Disposed);
            Assert.Equal(TrackingState.Faulted, test.Tracker.Status.State);
        }
        finally { await test.DisposeAsync(); }
    }

    [Fact]
    public async Task CleanupFailureFaultsCompletionAndIsReportedToTheHost()
    {
        await using var test = new Driver();
        await test.Start();
        var observed = new List<Exception>();
        test.Tracker.Error += observed.Add;
        test.Resolver.DisposeFailure = new IOException("test unhook failure");
        await Assert.ThrowsAsync<AggregateException>(test.Tracker.StopAsync);
        await Assert.ThrowsAsync<IOException>(() => test.Tracker.Completion);
        Assert.Equal(TrackingState.Faulted, test.Tracker.Status.State);
        Assert.Single(observed);
        Assert.True(test.Input.Disposed && test.Session.Disposed && test.Resolver.Disposed);
    }

    [Fact]
    public async Task StopDrainsBufferedInput()
    {
        await using var test = new Driver();
        await test.Start();
        await test.Sample(0, Window("one"));
        await test.Sample(1, Window("one"));
        test.Clock.Set(test.At(3));
        test.Resolver.ForInput = test.Resolver.Map(Window("one"));
        for (var index = 0; index < 1000; index++)
            test.Input.Send(new("one", test.At(1), index % 2 == 0 ? InteractionKind.Click : InteractionKind.Scroll));
        await test.Tracker.StopAsync();
        Assert.Equal(InteractionKind.Click | InteractionKind.Scroll, Assert.Single(test.Raw().Interactions).Kind);
        Assert.Equal(2, test.Raw().SessionEvents.Count);
        await test.Tracker.Completion;
    }

    [Fact]
    public async Task ManualEndSurvivesRestartOnSameLocalDate()
    {
        await using var first = new Driver();
        await first.Start();
        await first.Tracker.EndDayAsync();
        var id = first.Tracker.Status.WorkDayId;
        await first.Tracker.StopAsync();
        await using var second = new Driver(path: first.Path, start: first.At(10));
        await second.Start();
        Assert.Equal(TrackingState.DayEnded, second.Tracker.Status.State);
        Assert.Equal(id, second.Tracker.Status.WorkDayId);
    }

    [Fact]
    public async Task ManualEndAfterMidnightUsesTheEndDateOnRestart()
    {
        await using var first = new Driver(start: Driver.Epoch - TimeSpan.FromHours(9));
        await first.Start();
        first.Clock.Set(first.At(5 * 3600));
        await first.Tracker.EndDayAsync();
        var id = first.Tracker.Status.WorkDayId;
        await first.Tracker.StopAsync();
        await using var second = new Driver(path: first.Path, start: first.At(5 * 3600 + 1));
        await second.Start();
        Assert.Equal(TrackingState.DayEnded, second.Tracker.Status.State);
        Assert.Equal(id, second.Tracker.Status.WorkDayId);
    }

    [Fact]
    public async Task RecoveryAutoEndsBeforeReconcilingAnObservedUnlockedStartup()
    {
        await using var first = new Driver();
        await first.Start();
        first.Clock.Set(first.At(1));
        first.Session.Send(new(first.At(1), SessionEventKind.Lock));
        await Until(() => first.Tracker.Status.State == TrackingState.Unavailable);
        var id = first.Tracker.Status.WorkDayId!.Value;
        await first.Tracker.StopAsync();
        await using var second = new Driver(path: first.Path, start: first.At(4 * 3600 + 2), initiallyLocked: false);
        await second.Start();
        using var repository = second.Repository();
        Assert.Equal(first.At(1), repository.GetWorkDay(id)!.WorkDay.EndUtc);
        Assert.NotEqual(id, second.Tracker.Status.WorkDayId);
        Assert.Equal(TrackingState.Tracking, second.Tracker.Status.State);
    }

    [Fact]
    public async Task StartupUsesSameDaySessionLogonButDoesNotFabricateEarlierObservations()
    {
        await using var test = new Driver(logon: Driver.Epoch - TimeSpan.FromHours(1));
        await test.Start();
        Assert.Equal(Driver.Epoch - TimeSpan.FromHours(1), test.Tracker.Status.WorkDay!.StartUtc);
        Assert.Equal(Driver.Epoch, test.Raw().TrackingStartedUtc);
        Assert.Empty(test.Raw().VisibilitySegments);
    }

    [Fact]
    public async Task AnInitiallyUnavailableDayIsNotTrackedUntilAvailabilityReturns()
    {
        await using var test = new Driver(initiallyLocked: true);
        await test.Start();
        Assert.Null(test.Raw().TrackingStartedUtc);
        test.Clock.Set(test.At(10));
        test.Session.Send(new(test.At(10), SessionEventKind.Unlock));
        await Until(() => test.Tracker.Status.State == TrackingState.Tracking);
        Assert.Equal(test.At(10), test.Raw().TrackingStartedUtc);
    }

    [Fact]
    public async Task StopDuringStartupStillDisposesCollectors()
    {
        await using var test = new Driver();
        var start = test.Start();
        var stop = test.Tracker.StopAsync();
        await start;
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(test.Input.Disposed && test.Session.Disposed && test.Resolver.Disposed);
    }

    [Fact]
    public async Task ASecondTrackerCannotWriteIntoTheSameRecording()
    {
        await using var first = new Driver();
        await first.Start();
        await using var second = new Driver(path: first.Path);
        await Assert.ThrowsAnyAsync<IOException>(second.Start);
        await Assert.ThrowsAsync<AggregateException>(second.Tracker.StopAsync);
        Assert.Equal(TrackingState.Tracking, first.Tracker.Status.State);
        await first.Sample(0, Window("one"));
        await first.Sample(1, Window("one"));
        Assert.Single(first.Raw().VisibilitySegments);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(300)]
    public async Task LongSamplingIntervalsUseIndependentThirtySecondDurableCheckpoints(int seconds)
    {
        await using var test = new Driver(new() { SampleInterval = TimeSpan.FromSeconds(seconds) });
        await test.Start();
        await test.Sample(0, Window("one"));
        await Until(() => test.Clock.HasCheckpointTimer);
        test.Clock.Set(test.At(30));
        await Until(() => test.Tracker.LastDurableFlushUtc == test.At(30));
        Assert.Equal(test.At(0), test.Tracker.LastSampleUtc);
        Assert.Empty((await test.Tracker.GetRawDataAsync()).VisibilitySegments);
        test.Clock.Set(test.At(60));
        await Until(() => test.Tracker.LastDurableFlushUtc == test.At(60));
        Assert.Equal(test.At(0), test.Tracker.LastSampleUtc);
        using var connection = new SqliteConnection($"Data Source={test.Path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.LastFlushedUtc = s.LastFlushedUtc FROM TrackingRuns r
            JOIN TrackingWriterContext c ON c.RunId = r.Id CROSS JOIN RepositoryState s;
            """;
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task SamplingBeyondTheNativeTimerRangeStillCheckpointsAndStopsWithoutOverflow()
    {
        await using var test = new Driver(new() { SampleInterval = TimeSpan.MaxValue });
        await test.Start();
        await test.Sample(0, Window("one"));
        await Until(() => test.Clock.HasCheckpointTimer);
        test.Clock.Set(test.At(30));
        await Until(() => test.Tracker.LastDurableFlushUtc == test.At(30));
        await test.Tracker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(test.Raw().VisibilitySegments);
    }

    [Fact]
    public async Task CleanStopRestartCountsTheOfflineGapAsNotTrackedWithoutARecoveryEvent()
    {
        await using var first = new Driver();
        await first.Start();
        await first.Sample(0, Window("one"));
        await first.Sample(1, Window("one"));
        first.Clock.Set(first.At(10));
        await first.Tracker.StopAsync();
        await using var second = new Driver(path: first.Path, start: first.At(20));
        await second.Start();
        var raw = await second.Tracker.GetRawDataAsync();
        Assert.DoesNotContain(raw.TrackingEvents, e => e.Kind == TrackingEventKind.Recovery);
        var report = CalculationEngine.Calculate(raw, new(), first.At(22));
        Assert.Equal(TimeSpan.FromSeconds(10), report.NotTrackedTime);
        Assert.Equal(TimeSpan.FromSeconds(12), report.AwayTime);
    }

    [Fact]
    public async Task PauseSignalsAreDurableAndCoreDoesNotCountThemAsAwayAcrossRestart()
    {
        await using var first = new Driver();
        await first.Start();
        first.Clock.Set(first.At(1));
        await first.Tracker.PauseAsync();
        first.Clock.Set(first.At(2));
        await first.Tracker.StopAsync();
        await using var second = new Driver(path: first.Path, start: first.At(10));
        await second.Start();
        Assert.Equal(TrackingState.Paused, second.Tracker.Status.State);
        var paused = await second.Tracker.GetRawDataAsync();
        var pausedReport = CalculationEngine.Calculate(paused, new(), first.At(11));
        Assert.Equal(TimeSpan.FromSeconds(10), pausedReport.NotTrackedTime);
        Assert.Equal(TimeSpan.FromSeconds(1), pausedReport.AwayTime);
        second.Clock.Set(first.At(12));
        await second.Tracker.ResumeAsync();
        var resumed = TrackingRawDataReader.GetRawData(first.Path, second.Tracker.Status.WorkDayId!.Value);
        Assert.Contains(resumed.TrackingEvents, e => e.Kind == TrackingEventKind.Resume && e.TimeUtc == first.At(12));
        var resumedReport = CalculationEngine.Calculate(resumed, new(), first.At(13));
        Assert.Equal(TimeSpan.FromSeconds(11), resumedReport.NotTrackedTime);
        Assert.Equal(TimeSpan.FromSeconds(2), resumedReport.AwayTime);
        Assert.DoesNotContain(resumed.SessionEvents, e => e.Kind is SessionEventKind.Lock or SessionEventKind.Unlock);
    }

    [Fact]
    public async Task IdleCrossCheckRecordsGlobalPresenceWhenTargetedInputHasNoAllowedIdentity()
    {
        await using var test = new Driver(new()
        {
            SampleInterval = TimeSpan.FromMinutes(5),
            ExcludedApplications = new HashSet<string> { "private.exe" }
        }, getIdleDuration: () => TimeSpan.Zero);
        await test.Start();
        await Until(() => test.Raw().GlobalInputs.Count != 0);
        test.Input.Send(new("private-or-own-window", test.At(0), InteractionKind.Keyboard));
        await Until(() => test.Tracker.RejectedInteractions != 0);
        var raw = await test.Tracker.GetRawDataAsync();
        Assert.Empty(raw.Windows);
        Assert.Empty(raw.Interactions);
        Assert.Equal(test.At(0), raw.GlobalInputs[0].LastInputUtc);
        var report = CalculationEngine.Calculate(raw, new(), test.At(1));
        Assert.Equal(TimeSpan.Zero, report.AwayTime);
        Assert.Equal(TimeSpan.Zero, report.TotalActiveTime);
    }

    [Fact]
    public async Task IdleCrossCheckDoesNotRecordDuringPauseAndUsesANewObservationOnResume()
    {
        await using var test = new Driver(getIdleDuration: () => TimeSpan.Zero);
        await test.Start();
        await Until(() => test.Raw().GlobalInputs.Count == 1);
        await test.Tracker.PauseAsync();
        test.Clock.Set(test.At(5));
        await test.Tracker.FlushAsync();
        Assert.DoesNotContain(test.Raw().GlobalInputs, i => i.ObservedUtc == test.At(5));
        test.Clock.Set(test.At(6));
        await test.Tracker.ResumeAsync();
        await Until(() => test.Raw().GlobalInputs.Any(i => i.ObservedUtc == test.At(6)));
        var raw = await test.Tracker.GetRawDataAsync();
        Assert.DoesNotContain(raw.GlobalInputs, i => i.ObservedUtc == test.At(5));
        Assert.DoesNotContain(raw.SessionEvents, e => e.Kind is SessionEventKind.Lock or SessionEventKind.Unlock);
        Assert.Equal(TimeSpan.FromSeconds(6), CalculationEngine.Calculate(raw, new(), test.At(7)).NotTrackedTime);
    }

    [Fact]
    public async Task LastInputInfoFailureIsReportedAndStopsTheCollectors()
    {
        await using var test = new Driver(getIdleDuration: () => throw new Win32Exception(5));
        await test.Start();
        await Assert.ThrowsAsync<Win32Exception>(() => test.Tracker.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<AggregateException>(test.Tracker.StopAsync);
        Assert.True(test.Input.Disposed && test.Session.Disposed && test.Resolver.Disposed);
    }

    [Fact]
    public async Task MissingHistoricalDayDoesNotStopTheLiveTracker()
    {
        await using var test = new Driver();
        await test.Start();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => test.Tracker.GetRawDataAsync(long.MaxValue));
        Assert.False(test.Tracker.Completion.IsCompleted);
        Assert.Equal(TrackingState.Tracking, test.Tracker.Status.State);
        await test.Sample(0, Window("one"));
        await test.Sample(1, Window("one"));
        Assert.NotEmpty((await test.Tracker.GetRawDataAsync()).VisibilitySegments);
    }

    [Fact]
    public async Task LegacyUncleanPauseJournalMigratesAndRecoversAtItsPersistedFlush()
    {
        await using var test = new Driver();
        using (var repository = new SqliteWorkTimeRepository(test.Path, test.Clock))
        {
            var stored = repository.CreateWorkDay(WorkDayFactory.StartNew(test.At(0), TimeSpan.Zero), test.At(0));
            repository.WriteBatch(stored.Id, new() { SessionEvents = [new(test.At(0), SessionEventKind.AppStart)] });
            using var connection = new SqliteConnection($"Data Source={test.Path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE TrackingLifecycle (Id INTEGER PRIMARY KEY, WorkDayId INTEGER, TimeUtc INTEGER NOT NULL, Kind TEXT NOT NULL);
                INSERT INTO TrackingLifecycle (WorkDayId, TimeUtc, Kind) VALUES ($day, $pause, 'Pause');
                """;
            command.Parameters.AddWithValue("$day", stored.Id);
            command.Parameters.AddWithValue("$pause", test.At(1).UtcTicks);
            command.ExecuteNonQuery();
        }
        test.Clock.Set(test.At(10));
        await test.Start();
        Assert.Equal(TrackingState.Paused, test.Tracker.Status.State);
        var raw = await test.Tracker.GetRawDataAsync();
        var recovery = Assert.Single(raw.TrackingEvents, e => e.Kind == TrackingEventKind.Recovery);
        Assert.Equal(test.At(0), recovery.TimeUtc);
        Assert.Equal(test.At(10), recovery.EndUtc);
        Assert.Equal(TimeSpan.FromSeconds(11), CalculationEngine.Calculate(raw, new(), test.At(11)).NotTrackedTime);
    }

    private static WindowInfo Window(string name, string title = "title") =>
        new(name, name, name, $@"C:\apps\{name}.exe", title);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition did not become true.");
            await Task.Delay(5);
        }
    }

    private sealed class Driver : IAsyncDisposable
    {
        internal static readonly DateTimeOffset Epoch = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
        internal readonly FastClock Clock;
        internal readonly Source<InteractionSecond> Input = new();
        internal readonly Source<SessionEvent> Session = new();
        internal readonly Snapshots Snapshots = new();
        internal readonly Resolver Resolver = new();
        internal readonly TrackingService Tracker;
        internal readonly string Path;
        private readonly string? ownedDirectory;
        private readonly DateTimeOffset start;

        internal Driver(TrackerSettings? settings = null, string? path = null, DateTimeOffset? start = null,
            DateTimeOffset? logon = null, bool? initiallyLocked = null, Func<TimeSpan>? getIdleDuration = null)
        {
            this.start = start ?? Epoch;
            Clock = new(this.start);
            if (path == null)
            {
                ownedDirectory = System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ownedDirectory);
            }
            Path = path ?? System.IO.Path.Combine(ownedDirectory!, "test.sqlite3");
            Tracker = new(new()
            {
                DatabasePath = Path, TimeZone = TimeZoneInfo.Utc, SessionLogonUtc = logon,
                Settings = settings ?? new()
            }, () => new(Snapshots, Input, Session, Resolver)
            {
                InitiallyLocked = initiallyLocked, GetIdleDuration = getIdleDuration
            }, Clock);
        }

        internal Task Start() => Tracker.StartAsync();
        internal DateTimeOffset At(int seconds) => start + TimeSpan.FromSeconds(seconds);
        internal async Task Sample(int second, params WindowInfo[] windows)
        {
            var now = At(second);
            Clock.Set(now);
            Snapshots.Items.Writer.TryWrite(windows.Select((w, index) => new WindowSnapshot(w, now, 100m, index == 0)).ToArray());
            await Until(() => Tracker.LastSampleUtc >= now);
        }
        internal SqliteWorkTimeRepository Repository() => new(Path);
        internal WorkDayRawData Raw()
        {
            // High-frequency signals are buffered; a flush is the durability barrier.
            if (Tracker.Status.State is not (TrackingState.Created or TrackingState.Stopped or TrackingState.Faulted))
                try { Tracker.FlushAsync().GetAwaiter().GetResult(); } catch (InvalidOperationException) { }
            return TrackingRawDataReader.GetRawData(Path, Tracker.Status.WorkDayId!.Value);
        }
        public async ValueTask DisposeAsync()
        {
            try { await Tracker.DisposeAsync(); } catch (AggregateException) { }
            if (ownedDirectory != null) Directory.Delete(ownedDirectory, recursive: true);
        }
    }

    private sealed class FastClock(DateTimeOffset initial) : TimeProvider
    {
        private long ticks = initial.UtcTicks;
        private readonly List<ManualTimer> timers = [];
        internal bool HasCheckpointTimer { get { lock (timers) return timers.Any(t => t.Period == TimeSpan.FromSeconds(30)); } }
        internal void Set(DateTimeOffset value)
        {
            Interlocked.Exchange(ref ticks, value.UtcTicks);
            ManualTimer[] pending;
            lock (timers) pending = timers.ToArray();
            foreach (var timer in pending) timer.Fire(value.UtcTicks);
        }
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.FromSeconds(30))
                return new System.Threading.Timer(callback, state, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (timers) timers.Add(timer);
            return timer;
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly FastClock owner;
            private readonly TimerCallback callback;
            private readonly object? state;
            private long due;
            private bool disposed;
            internal TimeSpan Period { get; private set; }
            internal ManualTimer(FastClock owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                this.owner = owner; this.callback = callback; this.state = state;
                Change(dueTime, period);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (this)
                {
                    if (disposed) return false;
                    due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.GetUtcNow().UtcTicks + dueTime.Ticks;
                    Period = period;
                    return true;
                }
            }
            internal void Fire(long now)
            {
                lock (this)
                {
                    if (disposed || now < due) return;
                    due = Period > TimeSpan.Zero ? now + Period.Ticks : long.MaxValue;
                }
                callback(state);
            }
            public void Dispose() { lock (this) disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Snapshots : IWindowSnapshotProvider
    {
        internal readonly Channel<IReadOnlyList<WindowSnapshot>> Items = Channel.CreateUnbounded<IReadOnlyList<WindowSnapshot>>();
        public async ValueTask<IReadOnlyList<WindowSnapshot>> CaptureAsync(CancellationToken cancellationToken = default) =>
            await Items.Reader.ReadAsync(cancellationToken);
    }

    private sealed class Source<T> : IInputMonitor, ISessionMonitor, IDisposable
    {
        private readonly Channel<T> items = Channel.CreateUnbounded<T>();
        internal bool Disposed;
        internal void Send(T item) => items.Writer.TryWrite(item);
        internal void Fail(Exception error) => items.Writer.TryComplete(error);
        public async IAsyncEnumerable<InteractionSecond> ReadInteractionsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var item in items.Reader.ReadAllAsync(cancellationToken)) yield return (InteractionSecond)(object)item!;
        }
        public async IAsyncEnumerable<SessionEvent> ReadSessionEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var item in items.Reader.ReadAllAsync(cancellationToken)) yield return (SessionEvent)(object)item!;
        }
        public void Dispose() { Disposed = true; items.Writer.TryComplete(); }
    }

    private sealed class Resolver : ITrackingWindowResolver
    {
        private readonly Dictionary<WindowInfo, WindowInfo> windows = [];
        internal volatile WindowInfo? ForInput;
        internal bool Disposed;
        internal Exception? DisposeFailure;
        internal WindowInfo Map(WindowInfo info)
        {
            lock (windows)
            {
                if (!windows.TryGetValue(info, out var mapped)) windows[info] = mapped = info with { Id = Guid.NewGuid().ToString("N") };
                return mapped;
            }
        }
        public void CheckHealth() => ObjectDisposedException.ThrowIf(Disposed, this);
        public WindowInfo? ResolveSnapshot(WindowSnapshot snapshot) { CheckHealth(); return Map(snapshot.Window); }
        public WindowInfo? ResolveInteraction(InteractionSecond interaction) { CheckHealth(); return ForInput; }
        public DateTimeOffset? GetKnownEndUtc(string windowId) { CheckHealth(); return null; }
        public void Dispose()
        {
            Disposed = true;
            if (DisposeFailure != null) throw DisposeFailure;
        }
    }
}
