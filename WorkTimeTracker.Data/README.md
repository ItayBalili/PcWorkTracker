# WorkTimeTracker.Data

Standalone .NET 8 local SQLite persistence, referencing only Core and
`Microsoft.Data.Sqlite`. No Windows APIs, application dependency, networking,
timers, input hooks, key values, or calculated report storage.

## Public API

Namespace: `WorkTimeTracker.Data`. The synchronous, thread-safe
`SqliteWorkTimeRepository : IDisposable` exposes:

```csharp
SqliteWorkTimeRepository(string databasePath, TimeProvider? timeProvider = null);

StoredWorkDay CreateWorkDay(WorkDay day, DateTimeOffset? trackingStartedUtc = null);
void UpdateWorkDay(long workDayId, WorkDay day);
void WriteBatch(long workDayId, RawSignalBatch batch);
void Flush();

StoredWorkDay? GetWorkDay(long workDayId);
StoredWorkDay? GetOpenWorkDay();
IReadOnlyList<StoredWorkDay> ListWorkDays(DateOnly? fromDate = null, DateOnly? toDate = null);
WorkDayRawData GetRawData(long workDayId);
RawSignalBatch ReadSignals(long workDayId, DateTimeOffset fromUtc, DateTimeOffset toUtc);
RecoveryState GetRecoveryState();

int ApplyRetention(DateTimeOffset cutoffUtc);
void Dispose();
```

Public records:

- `StoredWorkDay(long Id, WorkDay WorkDay, DateTimeOffset? TrackingStartedUtc)`.
- `RawSignalBatch`: init-only `Windows`, `VisibilitySegments`, `Interactions`,
  `SessionEvents`, and `GlobalInputs` (all `IReadOnlyList<Core model>`, default empty), and nullable
  `TrackingStartedUtc` (default null).
- `RecoveryState(DateTimeOffset? LastFlushedUtc, StoredWorkDay? OpenWorkDay)`.

Example (paths are relative to the caller's working directory):

```csharp
using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Data;

using var repository = new SqliteWorkTimeRepository("worktime.db");
var now = DateTimeOffset.UtcNow;
var day = repository.GetOpenWorkDay()
    ?? repository.CreateWorkDay(WorkDayFactory.StartNew(now, TimeSpan.Zero), now);

repository.WriteBatch(day.Id, new RawSignalBatch
{
    TrackingStartedUtc = now,
    Windows = [new WindowInfo("editor-1", "editor", "Editor", null, "Notes")],
    VisibilitySegments =
        [new VisibilitySegment("editor-1", now, now.AddSeconds(1), 40.4m, true)],
    Interactions = [new InteractionSecond("editor-1", now, InteractionKind.Keyboard)]
});
repository.Flush();
var raw = repository.GetRawData(day.Id);
var report = CalculationEngine.Calculate(raw, new TrackerSettings(), now.AddSeconds(1));
```

## Storage and ingestion contract

- Schema v2 has `Apps`, per-work-day `Windows`, `VisibilitySegments`,
  `InteractionSeconds`, `SessionEvents`, `GlobalInputObservations`, `WorkDays`,
  and `RepositoryState`.
  Migrations use transactional `PRAGMA user_version`; newer versions are rejected.
  Initialization, migration, writes, reads, and retention do not need the app.
- All instants are UTC integer .NET ticks, preserving 100 ns precision. Dates
  remain the Core-provided **local work-day date**, not a derived UTC date.
  Day ids never get reused, including after retention. Several closed days can
  share a date; at most one work day can be open across all repository instances.
- Visibility stores Core-rounded **0.1 percentage points** as integer
  `VisibleTenths` (0..1000), with no 5% quantization or threshold filtering.
  Matching contiguous runs merge in memory and against persisted neighbors.
  Same-state overlapping replays merge too; gaps never merge. Conflicting
  overlaps and concurrent foreground windows within a day are rejected.
- Interactions floor to their UTC second and bitwise-OR flags per day/window/
  second, including across batches. `None` is retained without clearing other
  flags. The start's containing second is allowed for fractional day starts.
- Identical session events deduplicate by day/time/kind. Equal-time distinct
  events retain insertion order; callers must submit meaningful ordering.
- Metadata belongs to a day/window identity and is immutable within that day;
  retries must match. Use a new window id for a changed title, process incarnation,
  or application metadata. Different days may reuse an id with different metadata.
  Windows must be registered before, or in the same batch as, their signals.
- Set tracking start at creation or in a batch: null means "not tracked" to Core.
  Later batches preserve the earliest tracking start, never move it forward.
  Raw visibility and events cannot precede the day start. Closing a day preserves
  observations after its end (important for backdated automatic closure); Core
  applies the boundary when calculating. Closed days accept historical batches.
  A day's original start/date and a closed day's boundary cannot change.
- A batch is all-or-nothing, including new identities and its checkpoint. Reads
  use a SQLite snapshot, so reports cannot mix versions from concurrent writes.
  Prepared commands and a single transaction amortize each batch; indexes cover
  day dates, retention, visibility time/foreground/neighbors, interactions, and
  session-event time.

## Durability, recovery, concurrency

Every successful mutation, including `WriteBatch`, commits immediately using WAL,
`synchronous=FULL`, and foreign keys. There is **no unflushed repository buffer**.
`Flush` commits a durable barrier/checkpoint, not a WAL truncation. No disposal is
required to make successful writes durable. On restart SQLite recovers committed
WAL transactions and rolls back interrupted transactions. `GetRecoveryState`
returns the last committed checkpoint and open day; it does not fabricate a
shutdown, extend visibility, or close the day. `GetRawData` retrieves exactly the
persisted raw history for Core recalculation.

The capture host must call `WriteBatch` **at least every 30 seconds** (and on
shutdown) to limit loss of its own observations to 30 seconds. Calling `Flush`
alone cannot save observations not submitted to the repository. There is no
background scheduler here. `LastFlushedUtc` is a monotonic commit wall-clock
checkpoint, **not** a guarantee that signals are complete through that instant.
The usual SQLite guarantees depend on the OS/filesystem/device honoring sync.

One instance serializes calls with a lock, and SQLite immediate write
transactions serialize distinct instances/processes. Concurrent reads get
consistent snapshots; busy writers wait up to the provider's 30-second timeout.
Do not mutate batch lists during submission. Calls block: the app should use a
worker thread if needed. Operational errors (including busy/disk-full/corruption/
FK constraints) propagate as `SqliteException`; rejected boundaries/signals use
argument/invalid-operation exceptions; unknown ids use `KeyNotFoundException`
except `GetWorkDay`, which returns null. A failed operation does not advance the
checkpoint. Disposed instances reject operations.

## Queries and retention

`ListWorkDays` includes both endpoints and sorts newest date/start/id first.
`GetRawData` returns a full-day, internally consistent Core `WorkDayRawData`;
recalculate it with whatever `TrackerSettings` the user chooses.

`ReadSignals` is an indexed **[from, to)** UTC inspection/export query. It returns
entire intersecting visibility runs, interactions/events inside the range, all
day window identities, and the day's tracking start. It deliberately does not
reconstruct pre-range session/input state. **Do not calculate a sliced day from
this method**; use `GetRawData` to preserve Core's historical context.

`ApplyRetention(cutoffUtc)` atomically deletes closed days ending **strictly
before** the cutoff, cascades their raw rows, removes orphan apps, and returns
the number of deleted days. Open days are always retained. The host can translate
`TrackerSettings.RetentionPeriod` into `nowUtc - period`; null means skip it.
Retention is not automatic and is not secure erasure: database/WAL/free pages
or backups may retain deleted bytes. No automatic vacuum is performed.

## Limits

Use an ordinary local disk path; URI, in-memory, and UNC paths are rejected.
The caller must avoid mapped network drives/network mounts. WAL sidecars must
stay beside the database: do not copy just the main file while it is open.
There is no encryption, online backup API, automatic corruption repair, or
cross-day state synthesis. Window titles/executable paths are stored as supplied
raw metadata; callers own exclusions, sanitization, and filesystem permissions.
There is no schema importer for unrelated/unversioned existing databases.

## Build and tests

```powershell
dotnet build .\WorkTimeTracker.Data\WorkTimeTracker.Data.csproj
dotnet test .\WorkTimeTracker.Data\tests\WorkTimeTracker.Data.Tests.csproj
```

Tests cover precision/recalculation, UTC,
combined flags, transactional rollback, cross-flush/replay RLE, foreground
conflicts, identities, indexed range semantics, retention/cascades, concurrent
instances, migrations, and actual subprocess exit with an uncommitted write.
