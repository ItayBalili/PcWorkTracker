using Microsoft.Data.Sqlite;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Data;

namespace WorkTimeTracker.App;

/// <summary>App-owned lifecycle metadata; does not change Data's schema version or raw signal model.</summary>
internal sealed class TrackingLifecycleStore : IDisposable
{
    private readonly SqliteConnection connection;
    private long? activeRun;

    internal TrackingLifecycleStore(string path)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = System.IO.Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA synchronous = FULL;
                PRAGMA busy_timeout = 30000;
                CREATE TABLE IF NOT EXISTS TrackingLifecycle (
                    Id INTEGER PRIMARY KEY,
                    WorkDayId INTEGER,
                    TimeUtc INTEGER NOT NULL,
                    Kind TEXT NOT NULL,
                    EndUtc INTEGER
                );
                CREATE TABLE IF NOT EXISTS TrackingRuns (
                    Id INTEGER PRIMARY KEY,
                    StartedUtc INTEGER NOT NULL,
                    WorkDayId INTEGER,
                    LastFlushedUtc INTEGER,
                    StoppedUtc INTEGER,
                    RecoveredUtc INTEGER
                );
                CREATE TABLE IF NOT EXISTS TrackingWriterContext (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    RunId INTEGER
                );
                INSERT OR IGNORE INTO TrackingWriterContext (Id, RunId) VALUES (1, NULL);
                CREATE TRIGGER IF NOT EXISTS Tracking_FlushInserted AFTER INSERT ON RepositoryState BEGIN
                    UPDATE TrackingRuns SET LastFlushedUtc = NEW.LastFlushedUtc
                    WHERE Id = (SELECT RunId FROM TrackingWriterContext WHERE Id = 1)
                        AND StoppedUtc IS NULL AND RecoveredUtc IS NULL;
                END;
                CREATE TRIGGER IF NOT EXISTS Tracking_FlushUpdated AFTER UPDATE ON RepositoryState BEGIN
                    UPDATE TrackingRuns SET LastFlushedUtc = NEW.LastFlushedUtc
                    WHERE Id = (SELECT RunId FROM TrackingWriterContext WHERE Id = 1)
                        AND StoppedUtc IS NULL AND RecoveredUtc IS NULL;
                END;
                """;
            command.ExecuteNonQuery();
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('TrackingLifecycle') WHERE name = 'EndUtc';";
            if ((long)command.ExecuteScalar()! == 0)
            {
                command.CommandText = "ALTER TABLE TrackingLifecycle ADD COLUMN EndUtc INTEGER;";
                command.ExecuteNonQuery();
            }
        }
        catch { connection.Dispose(); throw; }
    }

    internal void Recover(RecoveryState recovery, bool legacyUnclean, DateTimeOffset detectedUtc)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE TrackingWriterContext SET RunId = NULL WHERE Id = 1;";
        command.ExecuteNonQuery();
        command.CommandText = """
            SELECT Id, WorkDayId, COALESCE(LastFlushedUtc, StartedUtc)
            FROM TrackingRuns WHERE StoppedUtc IS NULL AND RecoveredUtc IS NULL ORDER BY Id;
            """;
        var runs = new List<(long Id, long? Day, DateTimeOffset Boundary)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) runs.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1),
                new(reader.GetInt64(2), TimeSpan.Zero)));
        }
        command.CommandText = "SELECT COUNT(*) FROM TrackingRuns;";
        if ((long)command.ExecuteScalar()! == 0 && legacyUnclean && recovery.LastFlushedUtc is { } legacyBoundary)
            runs.Add((0, recovery.OpenWorkDay?.Id, legacyBoundary));
        foreach (var run in runs)
        {
            if (run.Boundary > detectedUtc)
                throw new InvalidOperationException("The clock precedes the last durable tracking flush; refusing to rewrite history.");
            var workDayId = recovery.OpenWorkDay is { } open && open.WorkDay.StartUtc <= run.Boundary
                ? open.Id : run.Day;
            if (workDayId is { } id)
            {
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO TrackingLifecycle (WorkDayId, TimeUtc, Kind) VALUES ($day, $time, 'Recovery');";
                command.Parameters.AddWithValue("$day", id);
                command.Parameters.AddWithValue("$time", run.Boundary.UtcTicks);
                command.ExecuteNonQuery();
            }
            if (run.Id == 0) continue;
            command.Parameters.Clear();
            command.CommandText = "UPDATE TrackingRuns SET RecoveredUtc = $now WHERE Id = $id;";
            command.Parameters.AddWithValue("$now", detectedUtc.UtcTicks);
            command.Parameters.AddWithValue("$id", run.Id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal void BeginRun(DateTimeOffset startedUtc, long? workDayId)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TrackingRuns (StartedUtc, WorkDayId) VALUES ($time, $day);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$time", startedUtc.UtcTicks);
        command.Parameters.AddWithValue("$day", (object?)workDayId ?? DBNull.Value);
        var id = (long)command.ExecuteScalar()!;
        command.CommandText = "UPDATE TrackingWriterContext SET RunId = $id WHERE Id = 1;";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        transaction.Commit();
        activeRun = id;
    }

    internal void SetWorkDay(long workDayId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE TrackingRuns SET WorkDayId = $day WHERE Id = $run;";
        command.Parameters.AddWithValue("$day", workDayId);
        command.Parameters.AddWithValue("$run", (object?)activeRun ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    internal void CloseRecoveries(long workDayId, DateTimeOffset appStartUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE TrackingLifecycle SET EndUtc = $end
            WHERE WorkDayId = $day AND Kind = 'Recovery' AND EndUtc IS NULL AND TimeUtc <= $end;
            """;
        command.Parameters.AddWithValue("$end", appStartUtc.UtcTicks);
        command.Parameters.AddWithValue("$day", workDayId);
        command.ExecuteNonQuery();
    }

    internal DateTimeOffset? GetWatermark()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT LastFlushedUtc FROM TrackingRuns WHERE Id = $run;";
        command.Parameters.AddWithValue("$run", (object?)activeRun ?? DBNull.Value);
        return command.ExecuteScalar() is long ticks ? new(ticks, TimeSpan.Zero) : null;
    }

    internal bool WasPaused()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Kind FROM TrackingLifecycle WHERE Kind IN ('Pause', 'Resume') ORDER BY Id DESC LIMIT 1;";
        return command.ExecuteScalar() as string == "Pause";
    }

    internal void Append(long? workDayId, DateTimeOffset time, string kind)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO TrackingLifecycle (WorkDayId, TimeUtc, Kind) VALUES ($day, $time, $kind);";
        command.Parameters.AddWithValue("$day", (object?)workDayId ?? DBNull.Value);
        command.Parameters.AddWithValue("$time", time.UtcTicks);
        command.Parameters.AddWithValue("$kind", kind);
        command.ExecuteNonQuery();
    }

    internal void CompleteRun(long? workDayId, DateTimeOffset stopUtc)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TrackingLifecycle (WorkDayId, TimeUtc, Kind) VALUES ($day, $time, 'Stop');
            UPDATE TrackingRuns SET StoppedUtc = $time WHERE Id = $run;
            UPDATE TrackingWriterContext SET RunId = NULL WHERE Id = 1 AND RunId = $run;
            """;
        command.Parameters.AddWithValue("$day", (object?)workDayId ?? DBNull.Value);
        command.Parameters.AddWithValue("$time", stopUtc.UtcTicks);
        command.Parameters.AddWithValue("$run", (object?)activeRun ?? DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
        activeRun = null;
    }

    internal static IReadOnlyList<TrackingEvent> ReadEvents(SqliteConnection connection, long workDayId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'TrackingLifecycle';";
        if (command.ExecuteScalar() == null) return [];
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('TrackingLifecycle') WHERE name = 'EndUtc';";
        var endColumn = (long)command.ExecuteScalar()! == 0 ? "NULL" : "EndUtc";
        command.CommandText = $"""
            SELECT TimeUtc, Kind, {endColumn} FROM TrackingLifecycle
            WHERE WorkDayId = $day AND Kind IN ('Pause', 'Resume', 'Recovery') ORDER BY TimeUtc, Id;
            """;
        command.Parameters.AddWithValue("$day", workDayId);
        var result = new List<TrackingEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new(new(reader.GetInt64(0), TimeSpan.Zero), Enum.Parse<TrackingEventKind>(reader.GetString(1)))
            {
                EndUtc = reader.IsDBNull(2) ? null : new(reader.GetInt64(2), TimeSpan.Zero)
            });
        return result;
    }

    internal WorkDayRawData AttachEvents(WorkDayRawData raw, long workDayId) =>
        raw with { TrackingEvents = ReadEvents(connection, workDayId) };

    public void Dispose()
    {
        try
        {
            if (activeRun == null) return;
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE TrackingWriterContext SET RunId = NULL WHERE Id = 1 AND RunId = $run;";
            command.Parameters.AddWithValue("$run", activeRun.Value);
            command.ExecuteNonQuery();
        }
        finally { connection.Dispose(); }
    }
}
