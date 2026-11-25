using System.IO;
using System.Collections.Concurrent;
using System.Threading.Channels;
using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Data;
using WorkTimeTracker.Windows;

namespace WorkTimeTracker.App;

/// <summary>
/// Single-use, window-independent collector. All mutable signal/day state and SQLite writes
/// belong to one channel reader; producers only send immutable observations.
/// </summary>
public sealed class TrackingService : IAsyncDisposable
{
    private sealed record Message(string Kind, object? Value = null, TaskCompletionSource? Reply = null);
    private sealed record Sample(DateTimeOffset Time, IReadOnlyList<WindowSnapshot> Windows);
    private sealed record ReadRequest(long? WorkDayId, TaskCompletionSource<WorkDayRawData> Result);
    private readonly TrackingOptions options;
    private readonly Func<TrackingCollectors> factory;
    private readonly TimeProvider clock;
    private readonly Channel<Message> messages = Channel.CreateBounded<Message>(
        new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource sampling = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object lifecycle = new();
    private TrackingCollectors? collectors;
    private SqliteWorkTimeRepository? repository;
    private FileStream? ownership;
    private TrackingLifecycleStore? lifecycleStore;
    private Task? actor, sampler, checkpoints, idle, inputs, sessions, stopping, starting;
    private StoredWorkDay? day;
    private readonly List<SessionEvent> sessionHistory = [];
    private readonly List<SessionEvent> availabilityHistory = [];
    private readonly List<(DateTimeOffset Start, DateTimeOffset? End)> recordingPeriods = [];
    private readonly ConcurrentQueue<Exception> errors = new();
    private Sample? previous;
    private GlobalInputObservation? previousGlobal;
    private DateTimeOffset? inactivityLastInput;
    private DateTimeOffset? inactivityClosedAt;
    private bool paused, locked, suspended, loggedOff, manualEnd;
    private DateTimeOffset started;
    private DateTimeOffset? stopBoundary;
    private DateTimeOffset observedThrough = DateTimeOffset.MinValue;
    private TrackingStatus status = new(TrackingState.Created, null, null);
    private long rejectedInteractions;
    private long lastSampleTicks;
    private long lastFlushTicks;
    private volatile bool disposingCollectors;
    private readonly HashSet<string> exclusions;
    // High-frequency signals are committed in small groups (well within the 30 s crash-safety
    // budget). Anything else first commits this buffer, so durable ordering is unchanged.
    private static readonly TimeSpan PendingWriteInterval = TimeSpan.FromSeconds(5);
    private readonly List<WindowInfo> pendingWindows = [];
    private readonly List<VisibilitySegment> pendingSegments = [];
    private readonly List<InteractionSecond> pendingInteractions = [];
    private readonly List<GlobalInputObservation> pendingGlobalInputs = [];
    private long pendingDayId;
    private DateTimeOffset? pendingSince;
    private bool buffering;

    public TrackingService(TrackingOptions? options = null, Func<TrackingCollectors>? collectorsFactory = null,
        TimeProvider? timeProvider = null)
    {
        var supplied = options ?? new TrackingOptions();
        this.options = supplied with
        {
            Settings = supplied.Settings with
            {
                ExcludedApplications = new HashSet<string>(supplied.Settings.ExcludedApplications, StringComparer.OrdinalIgnoreCase)
            }
        };
        this.options.Settings.Validate();
        clock = timeProvider ?? TimeProvider.System;
        exclusions = new HashSet<string>(this.options.Settings.ExcludedApplications.Select(Normalize),
            StringComparer.OrdinalIgnoreCase);
        factory = collectorsFactory ?? CreateWindowsCollectors;
    }

    public TrackingStatus Status => Volatile.Read(ref status);
    public long RejectedInteractions => Interlocked.Read(ref rejectedInteractions);
    public DateTimeOffset? LastSampleUtc => Interlocked.Read(ref lastSampleTicks) is var ticks && ticks != 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    public DateTimeOffset? LastDurableFlushUtc => Interlocked.Read(ref lastFlushTicks) is var ticks && ticks != 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    /// <summary>Completes on Stop, faults on any collector/storage/host callback error.</summary>
    public Task Completion => completion.Task;
    /// <summary>Raised on a worker thread. Hosts must marshal to their UI dispatcher.</summary>
    public event Action<TrackingStatus>? StateChanged;
    public event Action<Exception>? Error;

    public Task StartAsync()
    {
        lock (lifecycle)
        {
            if (actor != null || Status.State != TrackingState.Created)
                throw new InvalidOperationException("The service is single-use.");
            actor = Task.Run(RunAsync);
            return starting = RequestAsync("start");
        }
    }

    public Task StartDayAsync() => RequestAsync("day");
    public Task EndDayAsync() => RequestAsync("end");
    public Task EndAndStartDayAsync() => RequestAsync("restart-day");
    public Task PauseAsync() => RequestAsync("pause");
    public Task ResumeAsync() => RequestAsync("resume");
    public Task FlushAsync() => RequestAsync("flush");
    public async Task<WorkDayRawData> GetRawDataAsync(long? workDayId = null)
    {
        var result = new TaskCompletionSource<WorkDayRawData>(TaskCreationOptions.RunContinuationsAsynchronously);
        await RequestAsync("read", new ReadRequest(workDayId, result)).ConfigureAwait(false);
        return await result.Task.ConfigureAwait(false);
    }

    /// <summary>Returns a coherent snapshot including buffered observations without forcing a write.</summary>
    public async Task<WorkDayRawData> GetSnapshotAsync(long? workDayId = null)
    {
        var result = new TaskCompletionSource<WorkDayRawData>(TaskCreationOptions.RunContinuationsAsynchronously);
        await RequestAsync("snapshot", new ReadRequest(workDayId, result)).ConfigureAwait(false);
        return await result.Task.ConfigureAwait(false);
    }

    public Task StopAsync()
    {
        lock (lifecycle)
        {
            return stopping ??= StopCoreAsync();
        }
    }

    private async Task RequestAsync(string kind, object? value = null)
    {
        if (actor == null) throw new InvalidOperationException("Start the service first.");
        if (actor.IsCompleted) { await actor.ConfigureAwait(false); throw new InvalidOperationException("The service stopped."); }
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await messages.Writer.WriteAsync(new Message(kind, value, reply)).ConfigureAwait(false); }
        catch (ChannelClosedException) { throw new InvalidOperationException("The service stopped."); }
        await reply.Task.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        await foreach (var message in messages.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                buffering = message.Kind is "sample" or "input" or "global-input";
                if (!buffering && message.Kind != "snapshot" && repository != null) FlushPending();
                if (message.Kind == "stop")
                {
                    if (day?.WorkDay.EndUtc == null && day != null)
                        Write(new RawSignalBatch { SessionEvents = [new(stopBoundary ?? clock.GetUtcNow(), SessionEventKind.AppStop)] });
                    if (repository != null) FlushRepository();
                    lifecycleStore?.CompleteRun(day?.Id, stopBoundary ?? clock.GetUtcNow());
                    SetState(errors.Count == 0 ? TrackingState.Stopped : TrackingState.Faulted);
                    message.Reply?.TrySetResult();
                    break;
                }
                if (message.Kind == "error") throw (Exception)message.Value!;
                if (!errors.IsEmpty)
                {
                    message.Reply?.TrySetException(new InvalidOperationException("Tracking has failed.", errors.First()));
                    continue;
                }
                switch (message.Kind)
                {
                    case "start": Initialize(); break;
                    case "sample": ProcessSample((Sample)message.Value!); break;
                    case "checkpoint":
                        if (stopBoundary == null) { collectors!.Windows.CheckHealth(); FlushRepository(); }
                        break;
                    case "input": ProcessInput((InteractionSecond)message.Value!); break;
                    case "global-input": ProcessGlobalInput((GlobalInputObservation)message.Value!); break;
                    case "session": ProcessSession((SessionEvent)message.Value!); break;
                    case "day":
                        AutoClose(clock.GetUtcNow());
                        if (day?.WorkDay.EndUtc == null && day != null) throw new HostTransitionException("A day is already open.");
                        manualEnd = false;
                        OpenDay(clock.GetUtcNow(), false);
                        break;
                    case "end":
                        AutoClose(clock.GetUtcNow());
                        if (day == null || day.WorkDay.EndUtc != null) throw new HostTransitionException("No day is open.");
                        ClosePeriod(clock.GetUtcNow());
                        previous = null;
                        repository!.UpdateWorkDay(day.Id, WorkDayFactory.EndManually(day.WorkDay, clock.GetUtcNow()));
                        day = repository.GetWorkDay(day.Id);
                        lifecycleStore!.Append(day!.Id, clock.GetUtcNow(), "EndDay");
                        manualEnd = true;
                        PublishState();
                        break;
                    case "restart-day":
                        AutoClose(clock.GetUtcNow());
                        if (day == null || day.WorkDay.EndUtc != null) throw new HostTransitionException("No day is open.");
                        var boundary = clock.GetUtcNow();
                        ClosePeriod(boundary);
                        previous = null;
                        repository!.UpdateWorkDay(day.Id, WorkDayFactory.EndManually(day.WorkDay, boundary));
                        lifecycleStore!.Append(day.Id, boundary, "EndDay");
                        manualEnd = false;
                        inactivityLastInput = null;
                        inactivityClosedAt = null;
                        OpenDay(boundary, false);
                        break;
                    case "pause":
                        if (!paused)
                        {
                            var at = clock.GetUtcNow();
                            inactivityLastInput = null;
                            lifecycleStore!.Append(day?.Id, at, "Pause");
                            FlushRepository();
                            ClosePeriod(at); previous = null; paused = true; PublishState();
                        }
                        break;
                    case "resume":
                        if (paused)
                        {
                            var at = clock.GetUtcNow();
                            lifecycleStore!.Append(day?.Id, at, "Resume");
                            FlushRepository();
                            paused = false; previous = null; OpenPeriod(at); PublishState();
                        }
                        break;
                    case "stopping":
                        stopBoundary = (DateTimeOffset)message.Value!;
                        ClosePeriod(stopBoundary.Value);
                        previous = null;
                        SetState(TrackingState.Stopping);
                        break;
                    case "flush": FlushRepository(); break;
                    case "read":
                        var read = (ReadRequest)message.Value!;
                        var id = read.WorkDayId ?? day?.Id ?? throw new HostTransitionException("No day exists.");
                        read.Result.TrySetResult(lifecycleStore!.AttachEvents(repository!.GetRawData(id), id));
                        break;
                    case "snapshot":
                        var snapshot = (ReadRequest)message.Value!;
                        var snapshotId = snapshot.WorkDayId ?? day?.Id ?? throw new HostTransitionException("No day exists.");
                        var raw = lifecycleStore!.AttachEvents(repository!.GetRawData(snapshotId), snapshotId);
                        snapshot.Result.TrySetResult(snapshotId == day?.Id ? MergePending(raw) : raw);
                        break;
                }
                message.Reply?.TrySetResult();
            }
            catch (Exception ex)
            {
                // Invalid host transitions are request failures, not collector failures.
                if (message.Reply != null && (ex is HostTransitionException || (message.Kind is "read" or "snapshot") && ex is KeyNotFoundException))
                    message.Reply.TrySetException(ex);
                else
                {
                    RecordError(ex);
                    message.Reply?.TrySetException(ex);
                    if (message.Kind == "stop") break;
                }
            }
        }
        messages.Writer.TryComplete();
        while (messages.Reader.TryRead(out var pending))
            pending.Reply?.TrySetException(new InvalidOperationException("Tracking stopped."));
    }

    private void Initialize()
    {
        started = clock.GetUtcNow();
        repository = new SqliteWorkTimeRepository(options.DatabasePath, clock);
        ownership = new FileStream(System.IO.Path.GetFullPath(options.DatabasePath) + ".tracking.lock",
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        lifecycleStore = new TrackingLifecycleStore(options.DatabasePath);
        var recovery = repository.GetRecoveryState();
        var existingRaw = recovery.OpenWorkDay is { } existing ? repository.GetRawData(existing.Id) : null;
        var lastAppEvent = existingRaw?.SessionEvents.LastOrDefault(e => e.Kind is SessionEventKind.AppStart or SessionEventKind.AppStop);
        lifecycleStore.Recover(recovery, existingRaw?.TrackingStartedUtc != null && lastAppEvent?.Kind != SessionEventKind.AppStop, started);
        lifecycleStore.BeginRun(started, recovery.OpenWorkDay?.Id);
        paused = lifecycleStore.WasPaused();
        day = recovery.OpenWorkDay;
        if (day != null)
        {
            sessionHistory.AddRange(repository.GetRawData(day.Id).SessionEvents);
            AutoClose(started);
        }
        // AppStop/crash is not an OS unlock/resume. Do not invent availability events.
        foreach (var item in sessionHistory.OrderBy(e => e.TimeUtc)) ApplyAvailability(item.Kind);
        availabilityHistory.AddRange(sessionHistory);
        collectors = factory();
        if (day == null)
        {
            var lastDay = repository.ListWorkDays().FirstOrDefault();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(started, options.TimeZone).DateTime);
            if (lastDay is { WorkDay.EndReason: WorkDayEndReason.Manual, WorkDay.EndUtc: { } manuallyEnded } &&
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(manuallyEnded, options.TimeZone).DateTime) == today)
            {
                day = lastDay;
                manualEnd = true;
            }
        }
        if (collectors.InitiallyLocked is { } initiallyLocked)
        {
            var observedAt = clock.GetUtcNow();
            var observed = new List<SessionEvent>();
            if (locked != initiallyLocked) observed.Add(new(observedAt, initiallyLocked ? SessionEventKind.Lock : SessionEventKind.Unlock));
            if (suspended) observed.Add(new(observedAt, SessionEventKind.Resume));
            if (loggedOff) observed.Add(new(observedAt, SessionEventKind.Logon));
            locked = initiallyLocked; suspended = loggedOff = false;
            if (initiallyLocked && !observed.Any(e => e.Kind == SessionEventKind.Lock))
                observed.Add(new(observedAt, SessionEventKind.Lock));
            availabilityHistory.AddRange(observed);
            if (day is { WorkDay.EndUtc: null })
            {
                sessionHistory.AddRange(observed);
                Write(new RawSignalBatch { SessionEvents = observed });
            }
        }
        if (!manualEnd && (day == null || day.WorkDay.EndUtc != null)) OpenDay(started, true);
        else
        {
            if (day is { WorkDay.EndUtc: null })
                WriteAppStart(clock.GetUtcNow());
            OpenPeriod(clock.GetUtcNow());
        }
        PublishState();
        lifecycleStore.Append(day?.Id, started, "Start");
        if (options.Settings.RetentionPeriod is { } retention)
            repository.ApplyRetention(started - retention);
        inputs = Task.Run(ReadInputAsync);
        sessions = Task.Run(ReadSessionsAsync);
        sampler = Task.Run(SampleAsync);
        checkpoints = Task.Run(CheckpointAsync);
        if (collectors.GetIdleDuration != null) idle = Task.Run(ReadIdleAsync);
    }

    private void OpenDay(DateTimeOffset now, bool startup)
    {
        inactivityLastInput = null;
        inactivityClosedAt = null;
        var offset = options.TimeZone.GetUtcOffset(now);
        var workDay = startup
            ? WorkDayFactory.CreateForStartup(now, options.SessionLogonUtc ?? collectors?.SessionLogonUtc,
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, options.TimeZone).DateTime), offset)
            : WorkDayFactory.StartNew(now, offset);
        day = repository!.CreateWorkDay(workDay);
        lifecycleStore!.SetWorkDay(day.Id);
        lifecycleStore!.Append(day.Id, now, "StartDay");
        sessionHistory.Clear();
        var events = new List<SessionEvent>();
        if (locked) events.Add(new(now, SessionEventKind.Lock));
        if (suspended) events.Add(new(now, SessionEventKind.Suspend));
        if (loggedOff) events.Add(new(now, SessionEventKind.Logoff));
        sessionHistory.AddRange(events);
        Write(new RawSignalBatch { SessionEvents = events });
        WriteAppStart(clock.GetUtcNow());
        previous = null;
        recordingPeriods.Clear();
        OpenPeriod(clock.GetUtcNow());
        PublishState();
    }

    private bool Available => !locked && !suspended && !loggedOff;
    private bool Recording => stopBoundary == null && !paused && Available && day is { WorkDay.EndUtc: null };

    private void OpenPeriod(DateTimeOffset now)
    {
        if (Recording && (recordingPeriods.Count == 0 || recordingPeriods[^1].End != null))
        {
            recordingPeriods.Add((now, null));
            if (day!.TrackingStartedUtc == null)
            {
                Write(new RawSignalBatch { TrackingStartedUtc = now });
                day = day with { TrackingStartedUtc = now };
            }
        }
    }

    private void ClosePeriod(DateTimeOffset now)
    {
        if (recordingPeriods.Count != 0 && recordingPeriods[^1].End == null)
        {
            var start = recordingPeriods[^1].Start;
            recordingPeriods[^1] = (start, now < start ? start : now);
        }
    }

    private void ProcessSample(Sample sample)
    {
        if (stopBoundary != null) return;
        collectors!.Windows.CheckHealth();
        if (sample.Time <= observedThrough) { FlushPending(); return; }
        if (sample.Time > clock.GetUtcNow()) throw new InvalidOperationException("The snapshot timestamp is in the future.");
        observedThrough = sample.Time;
        AutoClose(sample.Time);
        if (!Recording)
        {
            previous = null;
            FlushPending();
            Interlocked.Exchange(ref lastSampleTicks, sample.Time.UtcTicks);
            return;
        }
        var mapped = sample.Windows
            .Select(s => (Sample: s, Window: collectors!.Windows.ResolveSnapshot(s)))
            .Where(s => s.Window != null && Allowed(s.Window))
            .GroupBy(s => s.Window!.Id, StringComparer.Ordinal).Select(g => g.First()).ToArray();
        var normalized = mapped.Select(s => s.Sample with { Window = s.Window! }).ToArray();
        var segments = new List<VisibilitySegment>();
        if (previous is { } prior && sample.Time > prior.Time &&
            (sample.Time - prior.Time <= options.Settings.SampleInterval ||
             sample.Time - prior.Time - options.Settings.SampleInterval <= options.Settings.SampleInterval))
        {
            // Only completed, contiguous observation intervals are stored. Never extrapolate
            // the last sample into Stop, an outage, a crash, or a suspended machine.
            var end = sample.Time;
            if (stopBoundary is { } stop && end > stop) end = stop;
            var period = recordingPeriods.LastOrDefault();
            var start = prior.Time > period.Start ? prior.Time : period.Start;
            string? foreground = null;
            foreach (var old in prior.Windows)
            {
                var knownEnd = collectors.Windows.GetKnownEndUtc(old.Window.Id);
                var windowEnd = knownEnd is { } closed && closed < end ? closed : end;
                if (windowEnd <= start) continue;
                var isForeground = old.IsForeground && foreground == null;
                if (isForeground) foreground = old.Window.Id;
                segments.Add(new(old.Window.Id, start, windowEnd, old.VisiblePercent, isForeground));
            }
        }
        var windows = normalized.Select(s => s.Window).Concat(previous?.Windows.Select(s => s.Window) ?? [])
            .DistinctBy(w => w.Id).ToArray();
        Write(new RawSignalBatch { Windows = windows, VisibilitySegments = segments });
        Interlocked.Exchange(ref lastSampleTicks, sample.Time.UtcTicks);
        previous = new Sample(sample.Time, normalized);
        // Keep only the small history needed by the delayed, per-second input stream.
        recordingPeriods.RemoveAll(p => p.End < sample.Time - TimeSpan.FromMinutes(1));
    }

    private void ProcessInput(InteractionSecond input)
    {
        var second = DateTimeOffset.FromUnixTimeSeconds(input.SecondUtc.ToUnixTimeSeconds());
        var end = second + TimeSpan.FromSeconds(1);
        if (day == null || second < day.WorkDay.StartUtc || end > clock.GetUtcNow() ||
            day.WorkDay.EndUtc is { } dayEnd && end > dayEnd ||
            stopBoundary is { } stop && end > stop ||
            !recordingPeriods.Any(p => p.Start <= second && (p.End == null || p.End >= end)) ||
            sessionHistory.Any(e => e.TimeUtc >= second && e.TimeUtc < end &&
                e.Kind is SessionEventKind.Lock or SessionEventKind.Logoff or SessionEventKind.Suspend or SessionEventKind.Shutdown))
        { Interlocked.Increment(ref rejectedInteractions); return; }
        var window = collectors!.Windows.ResolveInteraction(input);
        if (window == null || !Allowed(window))
        { Interlocked.Increment(ref rejectedInteractions); return; }
        Write(new RawSignalBatch { Windows = [window], Interactions = [input with { WindowId = window.Id, SecondUtc = second }] });
    }

    private void ProcessGlobalInput(GlobalInputObservation observation)
    {
        if (observation == previousGlobal) return;
        if (observation.LastInputUtc > observation.ObservedUtc)
            throw new InvalidOperationException("The last-input cross-check returned a future input time.");
        if (Available && !paused && !manualEnd && stopBoundary == null)
        {
            if (inactivityClosedAt is { } closedAt && observation.LastInputUtc > closedAt + TimeSpan.FromSeconds(1))
            {
                if (day?.WorkDay.EndUtc != null) OpenDay(observation.LastInputUtc, false);
                inactivityClosedAt = null;
                inactivityLastInput = observation.LastInputUtc;
            }
            if (day is { WorkDay.EndUtc: null })
            {
                if (inactivityLastInput is { } last &&
                    (observation.LastInputUtc - last >= options.Settings.AutoEndAfter ||
                     observation.ObservedUtc - observation.LastInputUtc >= options.Settings.AutoEndAfter))
                {
                    var end = observation.LastInputUtc - last >= options.Settings.AutoEndAfter ? last : observation.LastInputUtc;
                    CloseForInactivity(end);
                    if (observation.LastInputUtc > end + TimeSpan.FromSeconds(1))
                    {
                        OpenDay(observation.LastInputUtc, false);
                        inactivityClosedAt = null;
                        inactivityLastInput = observation.LastInputUtc;
                    }
                }
                else if (observation.LastInputUtc >= started && observation.LastInputUtc >= day.WorkDay.StartUtc &&
                         (inactivityLastInput is null || observation.LastInputUtc > inactivityLastInput))
                    inactivityLastInput = observation.LastInputUtc;
            }
        }
        if (day == null || observation.ObservedUtc < day.WorkDay.StartUtc ||
            observation.ObservedUtc > clock.GetUtcNow() ||
            day.WorkDay.EndUtc is { } ended && observation.ObservedUtc >= ended ||
            stopBoundary is { } stop && observation.ObservedUtc >= stop ||
            !recordingPeriods.Any(p => p.Start <= observation.ObservedUtc &&
                (p.End == null || observation.ObservedUtc < p.End))) return;
        Write(new RawSignalBatch { GlobalInputs = [observation] });
        previousGlobal = observation;
    }

    private void CloseForInactivity(DateTimeOffset end)
    {
        FlushPending();
        repository!.UpdateWorkDay(day!.Id, day.WorkDay with { EndUtc = end, EndReason = WorkDayEndReason.Auto4Hours });
        day = repository.GetWorkDay(day.Id);
        ClosePeriod(end);
        previous = null;
        inactivityClosedAt = end;
        inactivityLastInput = null;
        PublishState();
    }

    private void ProcessSession(SessionEvent item)
    {
        if (item.Kind is SessionEventKind.AppStart or SessionEventKind.AppStop) return;
        if (item.TimeUtc < started) return;
        if (stopBoundary is { } stop && item.TimeUtc > stop) return;
        // Delayed OS notifications are retained at their original timestamps. Availability
        // is recomputed in event-time order rather than allowing queue order to unlock us.
        AutoClose(item.TimeUtc);
        if (day is { WorkDay.EndUtc: null } && item.TimeUtc >= day.WorkDay.StartUtc)
        {
            sessionHistory.Add(item);
            Write(new RawSignalBatch { SessionEvents = [item] });
        }
        availabilityHistory.Add(item);
        locked = suspended = loggedOff = false;
        foreach (var e in availabilityHistory.Where(e => e.TimeUtc <= clock.GetUtcNow()).OrderBy(e => e.TimeUtc))
            ApplyAvailability(e.Kind);
        previous = null;
        if (!Available) ClosePeriod(item.TimeUtc); else OpenPeriod(item.TimeUtc);
        AutoClose(clock.GetUtcNow());
        if (day?.WorkDay.EndUtc != null && !manualEnd && Available)
            OpenDay(item.TimeUtc, false);
        PublishState();
    }

    private void ApplyAvailability(SessionEventKind kind)
    {
        switch (kind)
        {
            case SessionEventKind.Lock: locked = true; break;
            case SessionEventKind.Unlock: locked = false; break;
            case SessionEventKind.Suspend: suspended = true; break;
            case SessionEventKind.Resume: suspended = false; break;
            case SessionEventKind.Logoff:
            case SessionEventKind.Shutdown: loggedOff = true; break;
            case SessionEventKind.Logon: loggedOff = false; break;
        }
    }

    private void AutoClose(DateTimeOffset now)
    {
        if (day is not { WorkDay.EndUtc: null }) return;
        var closed = WorkDayBoundaryCalculator.CloseAutomaticallyIfDue(day.WorkDay,
            sessionHistory, options.Settings.AutoEndAfter, now);
        if (closed == null) return;
        FlushPending();
        repository!.UpdateWorkDay(day.Id, closed);
        day = repository.GetWorkDay(day.Id);
        ClosePeriod(closed.EndUtc!.Value);
        previous = null;
        PublishState();
    }

    private void WriteAppStart(DateTimeOffset at)
    {
        if (paused) lifecycleStore!.Append(day!.Id, at, "Pause");
        Write(new RawSignalBatch { SessionEvents = [new(at, SessionEventKind.AppStart)] });
        lifecycleStore!.CloseRecoveries(day!.Id, at);
    }

    private void Write(RawSignalBatch batch)
    {
        if (buffering && batch.SessionEvents.Count == 0 && batch.TrackingStartedUtc == null &&
            (pendingSince == null || pendingDayId == day!.Id))
        {
            pendingDayId = day!.Id;
            pendingWindows.AddRange(batch.Windows);
            pendingSegments.AddRange(batch.VisibilitySegments);
            pendingInteractions.AddRange(batch.Interactions);
            pendingGlobalInputs.AddRange(batch.GlobalInputs);
            var now = clock.GetUtcNow();
            pendingSince ??= now;
            if (now - pendingSince.Value >= PendingWriteInterval) FlushPending();
            return;
        }
        FlushPending();
        repository!.WriteBatch(day!.Id, batch);
        RefreshWatermark();
    }

    private void FlushPending()
    {
        if (pendingSince == null) return;
        var batch = new RawSignalBatch
        {
            Windows = pendingWindows.DistinctBy(w => w.Id).ToArray(),
            VisibilitySegments = pendingSegments.ToArray(),
            Interactions = pendingInteractions.GroupBy(i => (i.WindowId, i.SecondUtc))
                .Select(g => g.First() with { Kind = g.Aggregate(InteractionKind.None, (kind, i) => kind | i.Kind) }).ToArray(),
            GlobalInputs = pendingGlobalInputs.ToArray()
        };
        pendingWindows.Clear(); pendingSegments.Clear(); pendingInteractions.Clear(); pendingGlobalInputs.Clear();
        pendingSince = null;
        repository!.WriteBatch(pendingDayId, batch);
        RefreshWatermark();
    }

    private WorkDayRawData MergePending(WorkDayRawData raw)
    {
        var provisional = new List<VisibilitySegment>();
        if (Recording && previous is { } current)
        {
            var now = clock.GetUtcNow();
            if (now > current.Time && now - current.Time <= options.Settings.SampleInterval)
            {
                string? foreground = null;
                foreach (var window in current.Windows)
                {
                    var knownEnd = collectors!.Windows.GetKnownEndUtc(window.Window.Id);
                    var end = knownEnd is { } closed && closed < now ? closed : now;
                    if (end <= current.Time) continue;
                    var isForeground = window.IsForeground && foreground == null;
                    if (isForeground) foreground = window.Window.Id;
                    provisional.Add(new(window.Window.Id, current.Time, end, window.VisiblePercent, isForeground));
                }
            }
        }
        var visibility = new List<VisibilitySegment>();
        foreach (var group in raw.VisibilitySegments.Concat(pendingSegments).Concat(provisional).GroupBy(x => x.WindowId, StringComparer.Ordinal))
        {
            foreach (var next in group.OrderBy(x => x.StartUtc))
            {
                if (visibility.Count > 0 && visibility[^1] is var prior && prior.WindowId == next.WindowId &&
                    prior.EndUtc >= next.StartUtc && prior.VisiblePercent == next.VisiblePercent && prior.IsForeground == next.IsForeground)
                {
                    visibility[^1] = new VisibilitySegment(prior.WindowId, prior.StartUtc,
                        prior.EndUtc > next.EndUtc ? prior.EndUtc : next.EndUtc, prior.VisiblePercent, prior.IsForeground);
                }
                else
                {
                    visibility.Add(next);
                }
            }
        }

        return raw with
        {
            Windows = raw.Windows.Concat(pendingWindows).Concat(previous?.Windows.Select(x => x.Window) ?? [])
                .DistinctBy(x => x.Id).ToArray(),
            VisibilitySegments = visibility,
            Interactions = raw.Interactions.Concat(pendingInteractions).GroupBy(x => (x.WindowId, x.SecondUtc))
                .Select(g => g.First() with { Kind = g.Aggregate(InteractionKind.None, (kind, item) => kind | item.Kind) }).ToArray(),
            GlobalInputs = raw.GlobalInputs.Concat(pendingGlobalInputs).GroupBy(x => x.ObservedUtc)
                .Select(g => g.MaxBy(x => x.LastInputUtc)!).ToArray()
        };
    }

    private void FlushRepository()
    {
        if (pendingSince != null) { FlushPending(); return; }
        repository!.Flush();
        RefreshWatermark();
    }

    private void RefreshWatermark()
    {
        if (lifecycleStore?.GetWatermark() is { } watermark)
            Interlocked.Exchange(ref lastFlushTicks, watermark.UtcTicks);
    }
    private bool Allowed(WindowInfo window) =>
        !string.IsNullOrWhiteSpace(window.ProcessName) &&
        (window.ExecutablePath != null || window.ProcessName == WindowsProcessNames.Unknown) &&
        !exclusions.Contains(Normalize(window.ProcessName)) &&
        (window.ExecutablePath == null ||
         !exclusions.Contains(Normalize(window.ExecutablePath)) &&
         !exclusions.Contains(Normalize(Path.GetFileName(window.ExecutablePath))));
    private static string Normalize(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    private void PublishState() => SetState(stopBoundary != null ? TrackingState.Stopping :
        day?.WorkDay.EndUtc != null ? TrackingState.DayEnded :
        paused ? TrackingState.Paused : !Available ? TrackingState.Unavailable : TrackingState.Tracking);
    private void SetState(TrackingState value)
    {
        var next = new TrackingStatus(value, day?.Id, day?.WorkDay);
        if (next == Status) return;
        Volatile.Write(ref status, next);
        StateChanged?.Invoke(next);
    }

    private void RecordError(Exception error)
    {
        errors.Enqueue(error);
        sampling.Cancel();
        var faulted = new TrackingStatus(TrackingState.Faulted, day?.Id, day?.WorkDay);
        Volatile.Write(ref status, faulted);
        completion.TrySetException(error);
        try { StateChanged?.Invoke(faulted); } catch (Exception callback) { errors.Enqueue(callback); }
        try { Error?.Invoke(error); } catch (Exception callback) { errors.Enqueue(callback); }
        // Cleanup is never performed on the actor/producer being joined.
        _ = Task.Run(async () =>
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch { /* Failures are available through Completion, Error and the host's Stop/Dispose. */ }
        });
    }

    private async Task SampleAsync()
    {
        try
        {
            var interval = options.Settings.SampleInterval;
            using var timer = interval.TotalMilliseconds <= uint.MaxValue - 1
                ? new PeriodicTimer(interval < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : interval, clock)
                : null;
            do
            {
                var result = (await collectors!.Snapshots.CaptureAsync(sampling.Token).ConfigureAwait(false)).ToArray();
                var captured = result.Length == 0 ? clock.GetUtcNow() : result.Min(s => s.CapturedUtc);
                await messages.Writer.WriteAsync(new("sample", new Sample(captured, result)), sampling.Token).ConfigureAwait(false);
            } while (await WaitForSampleAsync(timer, interval).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        catch (Exception ex) { await SendErrorAsync(ex).ConfigureAwait(false); }
    }

    private async Task<bool> WaitForSampleAsync(PeriodicTimer? timer, TimeSpan interval)
    {
        if (timer != null) return await timer.WaitForNextTickAsync(sampling.Token).ConfigureAwait(false);
        while (interval > TimeSpan.Zero)
        {
            var chunk = interval > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : interval;
            await Task.Delay(chunk, clock, sampling.Token).ConfigureAwait(false);
            interval -= chunk;
        }
        return true;
    }

    private async Task CheckpointAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
            while (await timer.WaitForNextTickAsync(sampling.Token).ConfigureAwait(false))
                await messages.Writer.WriteAsync(new("checkpoint"), sampling.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        catch (Exception ex) { await SendErrorAsync(ex).ConfigureAwait(false); }
    }

    private async Task ReadInputAsync()
    {
        try
        {
            await foreach (var input in collectors!.Input.ReadInteractionsAsync().ConfigureAwait(false))
                await messages.Writer.WriteAsync(new("input", input)).ConfigureAwait(false);
            if (!disposingCollectors) throw new InvalidOperationException("The input collector stopped unexpectedly.");
        }
        catch (Exception ex) { await SendErrorAsync(ex).ConfigureAwait(false); }
    }

    private async Task ReadIdleAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
            do
            {
                var duration = collectors!.GetIdleDuration!();
                if (duration < TimeSpan.Zero) throw new InvalidOperationException("The last-input cross-check returned a negative idle duration.");
                var observed = clock.GetUtcNow();
                await messages.Writer.WriteAsync(new("global-input",
                    new GlobalInputObservation(observed, observed - duration)), sampling.Token).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(sampling.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        catch (Exception ex) { await SendErrorAsync(ex).ConfigureAwait(false); }
    }

    private async Task ReadSessionsAsync()
    {
        try
        {
            await foreach (var item in collectors!.Sessions.ReadSessionEventsAsync().ConfigureAwait(false))
                await messages.Writer.WriteAsync(new("session", item)).ConfigureAwait(false);
            if (!disposingCollectors) throw new InvalidOperationException("The session collector stopped unexpectedly.");
        }
        catch (Exception ex) { await SendErrorAsync(ex).ConfigureAwait(false); }
    }

    private async Task SendErrorAsync(Exception error)
    {
        try { await messages.Writer.WriteAsync(new("error", error)).ConfigureAwait(false); }
        catch (ChannelClosedException) { RecordError(error); }
    }

    private async Task StopCoreAsync()
    {
        if (actor == null)
        {
            Volatile.Write(ref status, new(TrackingState.Stopped, null, null));
            completion.TrySetResult();
            return;
        }
        var stopAt = clock.GetUtcNow();
        if (starting != null)
        {
            try { await starting.ConfigureAwait(false); } catch { /* The actor has reported the initialization error. */ }
        }
        if (!actor.IsCompleted)
        {
            try { await RequestAsync("stopping", stopAt).ConfigureAwait(false); } catch { }
        }
        sampling.Cancel();
        if (sampler != null) await sampler.ConfigureAwait(false);
        if (checkpoints != null) await checkpoints.ConfigureAwait(false);
        if (idle != null) await idle.ConfigureAwait(false);
        disposingCollectors = true;
        // Readers stay alive while native monitors close and drain their buffered events.
        foreach (var monitor in new object?[] { collectors?.Input, collectors?.Sessions, collectors?.Snapshots })
        {
            try { (monitor as IDisposable)?.Dispose(); }
            catch (Exception ex) { await SendErrorAsync(ex).ConfigureAwait(false); }
        }
        if (inputs != null) await inputs.ConfigureAwait(false);
        if (sessions != null) await sessions.ConfigureAwait(false);
        if (!actor.IsCompleted)
        {
            try { await RequestAsync("stop").ConfigureAwait(false); }
            catch (Exception ex) { errors.Enqueue(ex); }
        }
        await actor.ConfigureAwait(false);
        // Enqueued input still needs the identity ledger after its source has drained.
        // Retire that ledger only once the actor's final durable barrier has completed.
        try { collectors?.Windows.Dispose(); } catch (Exception ex) { RecordError(ex); }
        try { lifecycleStore?.Dispose(); } catch (Exception ex) { RecordError(ex); }
        try { repository?.Dispose(); } catch (Exception ex) { RecordError(ex); }
        try { ownership?.Dispose(); } catch (Exception ex) { RecordError(ex); }
        if (errors.Count != 0) throw new AggregateException("Tracking failed; all observed errors are included.", errors.ToArray());
        completion.TrySetResult();
    }

    private sealed class HostTransitionException(string message) : InvalidOperationException(message);

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { sampling.Dispose(); }
    }

    private TrackingCollectors CreateWindowsCollectors()
    {
        WindowsTrackingWindowResolver? resolver = null;
        WindowsInputMonitor? input = null;
        WindowsSessionMonitor? session = null;
        try
        {
            resolver = new WindowsTrackingWindowResolver(options.Settings.ExcludedApplications);
            input = new WindowsInputMonitor();
            session = new WindowsSessionMonitor();
            var initial = WindowsSessionState.Read();
            return new(new WindowsWindowSnapshotProvider(), input, session, resolver)
            {
                SessionLogonUtc = initial.LogonUtc,
                InitiallyLocked = initial.Locked,
                GetIdleDuration = WindowsIdleTime.GetIdleDuration
            };
        }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            foreach (var monitor in new IDisposable?[] { session, input, resolver })
            {
                try { monitor?.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
            }
            throw new AggregateException("Windows collectors could not start.", failures);
        }
    }
}
