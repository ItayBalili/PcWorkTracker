using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Data;

/// <summary>A serialized, synchronous, local SQLite repository. Every successful write is durable.</summary>
public sealed partial class SqliteWorkTimeRepository : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private readonly TimeProvider timeProvider;
    private bool disposed;

    public SqliteWorkTimeRepository(string databasePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (databasePath == ":memory:" ||
            databasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            databasePath.StartsWith(@"\\", StringComparison.Ordinal) ||
            databasePath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("Use a local filesystem database path, not memory, a URI, or a network path.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            if (!string.Equals(Convert.ToString(command.ExecuteScalar()), "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The database must support WAL mode.");
            }

            command.CommandText = "PRAGMA synchronous = FULL; PRAGMA busy_timeout = 30000;";
            command.ExecuteNonQuery();
            Schema.Migrate(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public StoredWorkDay CreateWorkDay(WorkDay day, DateTimeOffset? trackingStartedUtc = null) =>
        Write(transaction =>
        {
            ValidateDay(day);
            if (trackingStartedUtc < day.StartUtc)
            {
                throw new ArgumentOutOfRangeException(nameof(trackingStartedUtc));
            }

            using var command = Command(transaction, """
                INSERT INTO WorkDays (LocalDate, StartUtc, EndUtc, EndReason, TrackingStartedUtc)
                VALUES ($date, $start, $end, $reason, $tracking);
                SELECT last_insert_rowid();
                """,
                ("$date", DateText(day.Date)), ("$start", Ticks(day.StartUtc)),
                ("$end", NullableTicks(day.EndUtc)), ("$reason", (int)day.EndReason),
                ("$tracking", NullableTicks(trackingStartedUtc)));
            var id = (long)command.ExecuteScalar()!;
            return ReadDay(transaction, id)!;
        });

    /// <summary>Updates the boundary only. The original start/date and an ended day are immutable.</summary>
    public void UpdateWorkDay(long workDayId, WorkDay day) =>
        Write(transaction =>
        {
            ValidateDay(day);
            var existing = RequireDay(transaction, workDayId).WorkDay;
            if (existing.StartUtc != day.StartUtc || existing.Date != day.Date)
            {
                throw new ArgumentException("A work day's original start and date cannot change.", nameof(day));
            }

            if (existing.EndUtc is not null && existing != Normalize(day))
            {
                throw new InvalidOperationException("A closed work day cannot be changed or reopened.");
            }

            using var command = Command(transaction,
                "UPDATE WorkDays SET EndUtc = $end, EndReason = $reason WHERE Id = $id;",
                ("$end", NullableTicks(day.EndUtc)), ("$reason", (int)day.EndReason), ("$id", workDayId));
            command.ExecuteNonQuery();
            return 0;
        });

    /// <summary>Atomic, immediately durable ingestion. Safe to retry a successfully committed batch.</summary>
    public void WriteBatch(long workDayId, RawSignalBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        // Snapshot caller-owned lists before entering the transaction; mutation during this copy is unsupported.
        var windows = batch.Windows.ToArray();
        var visibility = CoalesceVisibility(batch.VisibilitySegments.ToArray());
        var interactions = batch.Interactions.ToArray();
        var globalInputs = batch.GlobalInputs.ToArray();
        var events = batch.SessionEvents.ToArray();
        Write(transaction =>
        {
            var day = RequireDay(transaction, workDayId);
            if (batch.TrackingStartedUtc is { } tracking)
            {
                if (tracking < day.WorkDay.StartUtc)
                {
                    throw new ArgumentOutOfRangeException(nameof(batch), "Tracking cannot start before the work day.");
                }

                using var trackingCommand = Command(transaction, """
                    UPDATE WorkDays SET TrackingStartedUtc =
                        CASE WHEN TrackingStartedUtc IS NULL THEN $tracking
                             ELSE MIN(TrackingStartedUtc, $tracking) END WHERE Id = $id;
                    """, ("$tracking", Ticks(tracking)), ("$id", workDayId));
                trackingCommand.ExecuteNonQuery();
            }

            if (windows.Length != 0) WriteWindows(transaction, workDayId, windows);
            if (visibility.Length != 0) WriteVisibility(transaction, workDayId, day.WorkDay.StartUtc, visibility);
            if (interactions.Length != 0) WriteInteractions(transaction, workDayId, day.WorkDay.StartUtc, interactions);
            if (globalInputs.Length != 0) WriteGlobalInputs(transaction, workDayId, day.WorkDay.StartUtc, globalInputs);
            if (events.Length != 0) WriteSessionEvents(transaction, workDayId, day.WorkDay.StartUtc, events);
            return 0;
        });
    }

    /// <summary>Durable barrier, not a WAL truncation. Previous writes were already committed.</summary>
    public void Flush() => Write(_ => 0);

    /// <summary>Deletes closed days whose end is strictly before the UTC cutoff; open days are preserved.</summary>
    public int ApplyRetention(DateTimeOffset cutoffUtc) =>
        Write(transaction =>
        {
            using var command = Command(transaction,
                "DELETE FROM WorkDays WHERE EndUtc < $cutoff;", ("$cutoff", Ticks(cutoffUtc)));
            var count = command.ExecuteNonQuery();
            using var cleanup = Command(transaction,
                "DELETE FROM Apps WHERE NOT EXISTS (SELECT 1 FROM Windows WHERE AppId = Apps.Id);");
            cleanup.ExecuteNonQuery();
            return count;
        });

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            connection.Dispose();
            disposed = true;
        }
    }

    private T Write<T>(Func<SqliteTransaction, T> action)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var transaction = connection.BeginTransaction(deferred: false);
            var result = action(transaction);
            using var checkpoint = Command(transaction, """
                INSERT INTO RepositoryState (Id, LastFlushedUtc) VALUES (1, $now)
                ON CONFLICT (Id) DO UPDATE SET LastFlushedUtc = MAX(LastFlushedUtc, excluded.LastFlushedUtc);
                """, ("$now", Ticks(timeProvider.GetUtcNow())));
            checkpoint.ExecuteNonQuery();
            transaction.Commit();
            return result;
        }
    }

    private T Read<T>(Func<SqliteTransaction, T> action)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var transaction = connection.BeginTransaction(deferred: true);
            var result = action(transaction);
            transaction.Commit();
            return result;
        }
    }

    private SqliteCommand Command(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static long Ticks(DateTimeOffset value) => value.UtcDateTime.Ticks;
    private static object? NullableTicks(DateTimeOffset? value) => value is { } time ? Ticks(time) : null;
    private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);
    private static string DateText(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static WorkDay Normalize(WorkDay day) =>
        day with { StartUtc = day.StartUtc.ToUniversalTime(), EndUtc = day.EndUtc?.ToUniversalTime() };

    private static void ValidateDay(WorkDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (!Enum.IsDefined(day.EndReason) || day.EndUtc < day.StartUtc ||
            (day.EndUtc is null) != (day.EndReason == WorkDayEndReason.Open))
        {
            throw new ArgumentException("Invalid work day boundary or end reason.", nameof(day));
        }
    }
}
