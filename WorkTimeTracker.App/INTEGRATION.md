# Phase 2 host integration

The tracking service lives in the `net8.0-windows` WPF/WinForms-capable App project,
referencing Core, Windows and Data. Its UI-independent lifecycle is separate from the
host/dashboard/settings implementation. Keep one service in the WPF
`Application` lifetime, not in a `Window`. The native hooks/notification message loops
run on their own threads; closing or hiding a dashboard must not stop this service.

## API

```csharp
await using var tracker = new TrackingService(new TrackingOptions());
tracker.StateChanged += status => { /* marshal onto the host dispatcher */ };
tracker.Error += error => { /* report/log locally */ };
await tracker.StartAsync();
// await tracker.PauseAsync(); await tracker.ResumeAsync();
// await tracker.EndDayAsync(); await tracker.StartDayAsync();
await tracker.FlushAsync();
await tracker.StopAsync();
```

Namespace: `WorkTimeTracker.App`.

* `TrackingService(TrackingOptions? options = null,
  Func<TrackingCollectors>? collectorsFactory = null, TimeProvider? timeProvider = null)`.
* Async commands: `StartAsync`, `StartDayAsync`, `EndDayAsync`, `PauseAsync`,
  `ResumeAsync`, `FlushAsync`, `StopAsync`; implements `IAsyncDisposable`.
  `GetRawDataAsync(long? workDayId = null)` reads a coherent raw snapshot on the actor,
  including App-owned tracking lifecycle events.
* `Status`: immutable `TrackingStatus(State, WorkDayId, WorkDay)`.
  States: Created, Tracking, Paused, Unavailable, DayEnded, Stopping, Stopped, Faulted.
* `LastSampleUtc`, `LastDurableFlushUtc`, `RejectedInteractions`, `Completion`.
  Completion faults immediately on a failure; Stop/Dispose aggregate all observed
  collection, storage, callback and cleanup failures. Await Completion in a host
  background observer, and always await Stop/Dispose.
* `StateChanged` and `Error` run on background threads. Do not synchronously block
  their callbacks on service commands or UI dispatch. Subscriber exceptions are
  surfaced as service failures, never silently discarded.
* `TrackingOptions`: `DatabasePath`, `Settings` (Core `TrackerSettings`),
  `TimeZone`, optional `SessionLogonUtc` override.
* `TrackingCollectors(Snapshots, Input, Sessions, Windows)` permits test injection.
  Optional `SessionLogonUtc` and `InitiallyLocked` supply observed startup state.
  Optional `GetIdleDuration` supplies a session-global idle cross-check; the Windows
  factory uses `WindowsIdleTime.GetIdleDuration` (`GetLastInputInfo`).
  Injected input/session collectors must implement `IDisposable` and complete their
  async streams when disposed; snapshot capture must honor cancellation.
* `ITrackingWindowResolver`: `CheckHealth()`, `ResolveSnapshot(WindowSnapshot)`,
  `ResolveInteraction(InteractionSecond)`, `GetKnownEndUtc(string)`, `Dispose()`.
  Resolvers return the **same immutable WindowInfo identity** for matching metadata,
  or null for unknown/ambiguous/excluded observations.
* `TrackingRawDataReader.GetRawData(string databasePath, long workDayId)` loads Data
  signals plus App-owned `TrackingEvents` for offline/history consumers. It checks
  SQLite data_version and retries mixed snapshots; very busy callers should retry
  its explicit error or use the service actor reader.
* Core `WorkDayRawData.TrackingEvents`: `TrackingEvent(TimeUtc, Kind)` where Kind is
  `TrackingEventKind.Pause`, `Resume` or `Recovery`. Recovery has an optional `EndUtc`
  (next durable AppStart); an unfinished recovery remains untracked.
* Core `TrackingAvailability.GetNotTrackedIntervals(raw, endUtc)` is the shared pure
  source of merged untracked intervals for calculations and timelines. AppStart and
  AppStop remain OS-independent `SessionEventKind` lifecycle markers.
* Core `WorkDayRawData.GlobalInputs` / Data `RawSignalBatch.GlobalInputs` contain
  `GlobalInputObservation(ObservedUtc, LastInputUtc)`, with no target identity.

The default path is `%LOCALAPPDATA%\WorkTimeTracker\tracking.sqlite3`. No network
transport or key values/text are collected. Exclusions match process names,
executable filenames (with or without `.exe`) or full paths, case-insensitively;
configuration is snapshotted at construction. Excluded/unknown metadata never enters
raw SQLite signals. A local exclusive `.tracking.lock` prevents concurrent tracker
writers; it is released/deleted on close or process exit.

## Persistence, boundaries and identity

One bounded channel actor owns every batch and repository write. The sampler copies
results before enqueueing; input/session/idle readers never mutate batches. Each completed
sample and accepted window/global-input/session observation is immediately committed
using Data's WAL + synchronous FULL transactions. A separate 30-second checkpoint
worker flushes even when sampling is slower, paused, blocked or no input arrives.
All positive sampling intervals are accepted (default one second); sub-millisecond
timer periods use a one-millisecond minimum, and periods beyond the native timer
range use cancellable chunks rather than overflowing. Flush is a durable actor
barrier for messages already queued, not a promise that upstream collectors have
finished their delayed seconds.

Visibility covers completed consecutive sample intervals only, with at most one
foreground window per interval. Known identity destruction/rename boundaries clip
segments. Gaps longer than two sampling intervals, pause, OS availability changes,
manual day boundaries and Stop break the chain. Stop does not extrapolate the last
sample. Restart never bridges the offline interval or fabricates a crash/end time.
The last unfinished interval and uncommitted in-memory input can be lost on a crash;
previously committed observations survive.

The additional identity ledger inventories eligible **top-level** windows (including
owned/minimized windows omitted by visibility sampling) and consumes native
create/destroy/name-change notifications. PID/HWND monitor IDs are reconciled using
host/target process creation times, executable metadata and immutable per-title GUID
versions. UWP application-frame children are resolved and checked separately.
Input is accepted only when the entire aggregated second fits one known version,
the live process is still identified and allowed, and a recording period covers the
second. Renames/reuse/unknown processes, excluded targets, boundary seconds,
destroyed targets, incomplete final seconds and delayed data beyond the one-minute
ledger can be rejected rather than guessed. An interaction does not require the
target to be present in the latest visibility sample. Rejection counts are exposed.

Workday startup/logon selection, manual ending and four-hour OS-unavailability
closure use Core's factories/calculator. The Windows factory queries the current WTS
lock state and logon time before collecting; unknown startup session state fails
closed. Startup state reconciliation records observations at startup, never backdates
an inferred unlock/resume. OS states are independent flags and recomputed in
event-time order. Auto-end is checked **before** processing a returning unlock/resume,
so a long absence cannot reopen the old day. A manual end stays ended across a
same-local-date restart until StartDay. There is no invented midnight boundary.
TrackingStartedUtc is set only when actual recording becomes available, not when
starting while paused/locked.

App-owned `TrackingLifecycle` rows in the same SQLite file durably record Start,
Stop, StartDay, EndDay, Pause, Resume and bounded Recovery signals. Manual pause
survives clean and unclean restart. Core counts their union with the initial prefix
and clean AppStop/AppStart gaps as NotTrackedTime, never AwayTime or window time.
AppStart does not clear a manual pause, Resume does not start a stopped app, and
neither transition invents OS lock/unlock events. Input recency resets across gaps;
fresh global idle observations after resume can establish current presence.

### Atomic watermark and lifecycle ordering

App-owned `TrackingRuns` and `TrackingWriterContext` journal each run. Two App-owned
triggers on Data's `RepositoryState` update the active run's LastFlushedUtc **inside
the same SQLite transaction** as every raw write/flush. There is no separate
post-commit watermark that could lag committed signals.

On startup, after exclusive writer ownership and before any new repository write,
unclean runs create a Recovery at that exact watermark, with detection time in
TrackingRuns.RecoveredUtc. A legacy unclean database falls back to its persisted
repository flush. Recovery remains open until the next durable raw AppStart; only
then is EndUtc committed and collection admitted. A crash between those commits
leaves a conservative open recovery, not fabricated tracked time. Visibility and
global observations from the old run never extend beyond its watermark.

Pause/Resume intent is committed before the durable flush and before changing
in-memory recording state or acknowledging the command. A crash in that ordering
window restores the durable intent; recovery still excludes activity after the old
watermark. Stop drains sources, writes AppStop and flushes before atomically marking
the run stopped and clearing writer context. A crash after AppStop but before the
journal's clean marker is harmless: the earlier raw AppStop still excludes that gap.
All repository writes must remain owned by the service actor while its run is active;
uncoordinated external writers are unsupported.

### Privacy-safe global idle cross-check

A separate one-second worker calls the real GetLastInputInfo wrapper even with long
window sampling intervals. It stores ObservedUtc and derived LastInputUtc in Data
schema v2's `GlobalInputObservations`, linked only to the workday. It records no
PID, HWND, app name, title, coordinates, keys or contents. Input in the tracker's own
windows or excluded apps therefore establishes global presence without assigning a
target or adding excluded metadata. There are no fake WindowInfo/InteractionSecond
identities. Pure Core uses the observation at ObservedUtc to cross-check global
idle/active timeouts, never to seed background-window interaction recency.

Observations made during pause/offline/OS unavailability are not persisted by the
service. Core ignores observations from untracked intervals and does not backdate
an observation across a tracking or session boundary. When both the input and its
observation are in the same available interval, retrospective calculations can use
the recorded input time, including the fraction before its polling observation.
Version-one Data databases migrate in place; existing session kinds remain 0–8.
Raw retention uses Data's closed-day retention (including global observations);
App lifecycle/run metadata is not pruned.

## Headless verification

All harness/test databases live in unique directories under the current checkout and
are deleted with their WAL/SHM/lock files. They never use the default user database.

```powershell
dotnet test WorkTimeTracker.App.HeadlessTests\WorkTimeTracker.App.HeadlessTests.csproj
dotnet run --project WorkTimeTracker.App.HeadlessTests\WorkTimeTracker.App.HeadlessTests.csproj -- --record 8
```

The recorder uses real Windows collectors, verifies window/segment/session rows and
prints counts and UTC ranges. It does not synthesize user input.

Opt-in integration tests exercise real desktop collection with a disposable
WinForms fixture, immutable title changes, live app exclusion, and abrupt
child-process termination followed by recovery. They never inject keyboard or
mouse input:

```powershell
$env:WORKTIMETRACKER_LIVE_TESTS = '1'
dotnet test WorkTimeTracker.App.HeadlessTests\WorkTimeTracker.App.HeadlessTests.csproj --filter Category=Live --logger 'console;verbosity=detailed'
Remove-Item Env:\WORKTIMETRACKER_LIVE_TESTS
```

Only these opt-in tests show temporary fixture windows. They do not inject input,
move the cursor, lock the workstation or suspend/log off the machine.

Validated on the interactive Windows desktop on 2026-11-24:

* Full App/headless suite: 64 passed, including 27 tracking-service regressions;
  Core suite: 27 passed; Data suite: 11 passed. This includes the host/dashboard/settings
  tests contributed by their owners as well as tracking and all three desktop cases.
* One live run persisted 11 window identities, 10 visibility runs, 9 hover seconds
  and 2 lifecycle session events. Segment UTC range:
  `2026-11-24T16:20:54.6100267Z`–`2026-11-24T16:21:06.6211942Z`;
  durable checkpoint: `2026-11-24T16:21:06.6863368Z`.
* The excluded-fixture run rejected 4 interaction seconds and persisted no
  dotnet fixture metadata/visibility/input. Abrupt child exit code 73 preserved
  8 visibility runs; restart yielded 16 runs without bridging the offline gap,
  changing the workday end, or inventing an AppStop before the crash. Paused and
  unpaused crashes both passed: recovery began exactly at journal watermarks
  `16:20:42.0053244Z` and `16:20:50.3852198Z`, respectively, and ended at the next
  durable AppStart. Paused state survived and required explicit Resume.
* The excluded-fixture run also persisted 8 real, identity-free last-input
  observations; recent input was detected despite no excluded app/interaction rows.
* The standalone `--record 4` harness also passed: 8 windows, 8 visibility runs,
  2 session events, no natural input during that non-synthesizing run.

## Limitations and hazards

* Read through TrackingRawDataReader or TrackingService.GetRawDataAsync when
  calculating reports: Data's repository returns its own raw/global signals but
  deliberately does not know App-owned pause/recovery events.
* Global idle precision is bounded by the one-second polling cadence, native tick
  granularity and wall-clock conversion. Observations establish presence at their
  observation time, not retroactively at the inferred last-input timestamp.
  GetLastInputInfo is session-local and can have non-monotonic/injected timestamps;
  the existing Windows wrapper uses a 32-bit tick difference, so extremely long
  idle periods near the 49.7-day wrap are inherently ambiguous. No target can be
  reconstructed for this global signal. Existing Core foreground/global-active
  rules still apply; global presence never creates a window-specific interaction.
* Windows input exposes only PID/HWND and second-level categories, not event-time
  metadata. The extra ledger is conservative, not a historical reconstruction
  engine. A transient window or title transition before the ledger observes it
  cannot be recovered; aggregated transition seconds are deliberately discarded.
  Asynchronous OS notification delivery and sampling impose temporal resolution
  limits; late events remain timestamped raw observations and Core applies their
  availability masks. There is no transaction spanning the separate OS streams.
* Physical lock/suspend/logoff/shutdown notification delivery and forced hardware
  power loss are not induced by tests. Their ordering is covered with deterministic
  injected streams; native subscriptions are the existing Windows implementation.
  PID/HWND reuse is guarded by birth-time validation and lifetime notifications,
  not forced by recycling system process IDs in a live desktop test.
* SQLite cannot make an uncommitted signal durable if Windows kills the process
  before the actor receives it. An overloaded or stalled desktop/storage device
  may delay processing; bounded source queues fail explicitly rather than silently
  dropping accepted batches. Clock regressions skip backward samples; startup
  refuses a durable watermark in the future rather than rewriting history.
  Large wall-clock corrections are not a monotonic-timeline guarantee and can
  affect flush/input timestamp ordering; no synthetic timestamps repair them.
  A crash loses the unfinished visibility interval, even if a checkpoint within a
  long sample interval recorded the collector's continuing liveness.
* Hardware power-loss delivery and native lock/suspend are not simulated on this
  user's workstation. A host must own service startup, failure reporting and awaited
  shutdown independently of any visible window.
