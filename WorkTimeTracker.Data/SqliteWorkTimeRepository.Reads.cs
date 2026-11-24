using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Data;

public sealed partial class SqliteWorkTimeRepository
{
    public StoredWorkDay? GetWorkDay(long workDayId) => Read(transaction => ReadDay(transaction, workDayId));

    public StoredWorkDay? GetOpenWorkDay() => Read(ReadOpenDay);

    /// <summary>Inclusive local date filter. Multiple days on the same local date retain distinct ids.</summary>
    public IReadOnlyList<StoredWorkDay> ListWorkDays(DateOnly? fromDate = null, DateOnly? toDate = null)
    {
        if (fromDate > toDate)
        {
            throw new ArgumentException("The date range is reversed.");
        }

        return Read<IReadOnlyList<StoredWorkDay>>(transaction =>
        {
            using var command = Command(transaction, """
                SELECT Id, LocalDate, StartUtc, EndUtc, EndReason, TrackingStartedUtc FROM WorkDays
                WHERE LocalDate >= $from AND LocalDate <= $to ORDER BY LocalDate DESC, StartUtc DESC, Id DESC;
                """, ("$from", DateText(fromDate ?? DateOnly.MinValue)), ("$to", DateText(toDate ?? DateOnly.MaxValue)));
            using var reader = command.ExecuteReader();
            var days = new List<StoredWorkDay>();
            while (reader.Read())
            {
                days.Add(ReadDayRow(reader));
            }

            return days;
        });
    }

    public RecoveryState GetRecoveryState() => Read(transaction =>
    {
        using var command = Command(transaction, "SELECT LastFlushedUtc FROM RepositoryState WHERE Id = 1;");
        var flushed = command.ExecuteScalar();
        return new RecoveryState(flushed is long ticks ? Utc(ticks) : null, ReadOpenDay(transaction));
    });

    /// <summary>A coherent full-day snapshot suitable for the pure Core CalculationEngine.</summary>
    public WorkDayRawData GetRawData(long workDayId) => Read(transaction =>
    {
        var day = RequireDay(transaction, workDayId);
        var signals = ReadSignals(transaction, workDayId, 0, DateTimeOffset.MaxValue.UtcDateTime.Ticks + 1);
        return new WorkDayRawData(day.WorkDay, day.TrackingStartedUtc, signals.Windows,
            signals.VisibilitySegments, signals.Interactions, signals.SessionEvents)
        {
            GlobalInputs = signals.GlobalInputs
        };
    });

    /// <summary>
    /// Half-open UTC range. Returns whole intersecting visibility runs and all day window identities.
    /// Does not include pre-range input/session state; use GetRawData for recalculation.
    /// </summary>
    public RawSignalBatch ReadSignals(long workDayId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (toUtc <= fromUtc)
        {
            throw new ArgumentException("The time range must have positive duration.");
        }

        return Read(transaction =>
        {
            RequireDay(transaction, workDayId);
            return ReadSignals(transaction, workDayId, Ticks(fromUtc), Ticks(toUtc));
        });
    }

    private RawSignalBatch ReadSignals(SqliteTransaction transaction, long dayId, long from, long to)
    {
        var windows = new List<WindowInfo>();
        using (var command = Command(transaction, """
            SELECT w.WindowId, a.ProcessName, a.FriendlyName, a.ExecutablePath, a.PathIsNull, w.Title
            FROM Windows w JOIN Apps a ON a.Id = w.AppId WHERE w.WorkDayId = $day ORDER BY w.WindowId;
            """, ("$day", dayId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                windows.Add(new WindowInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetBoolean(4) ? null : reader.GetString(3), reader.GetString(5)));
            }
        }

        var visibility = new List<VisibilitySegment>();
        using (var command = Command(transaction, """
            SELECT WindowId, StartUtc, EndUtc, VisibleTenths, IsForeground FROM VisibilitySegments
            WHERE WorkDayId = $day AND EndUtc > $from AND StartUtc < $to ORDER BY StartUtc, WindowId;
            """, ("$day", dayId), ("$from", from), ("$to", to)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                visibility.Add(new VisibilitySegment(reader.GetString(0), Utc(reader.GetInt64(1)),
                    Utc(reader.GetInt64(2)), reader.GetInt32(3) / 10m, reader.GetBoolean(4)));
            }
        }

        var interactions = new List<InteractionSecond>();
        using (var command = Command(transaction, """
            SELECT WindowId, SecondUtc, Kind FROM InteractionSeconds
            WHERE WorkDayId = $day AND SecondUtc >= $from AND SecondUtc < $to ORDER BY SecondUtc, WindowId;
            """, ("$day", dayId), ("$from", from), ("$to", to)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                interactions.Add(new InteractionSecond(reader.GetString(0), Utc(reader.GetInt64(1)),
                    (InteractionKind)reader.GetInt32(2)));
            }
        }

        var events = new List<SessionEvent>();
        using (var command = Command(transaction, """
            SELECT TimeUtc, Kind FROM SessionEvents
            WHERE WorkDayId = $day AND TimeUtc >= $from AND TimeUtc < $to ORDER BY TimeUtc, Id;
            """, ("$day", dayId), ("$from", from), ("$to", to)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                events.Add(new SessionEvent(Utc(reader.GetInt64(0)), (SessionEventKind)reader.GetInt32(1)));
            }
        }

        var globalInputs = new List<GlobalInputObservation>();
        using (var command = Command(transaction, """
            SELECT ObservedUtc, LastInputUtc FROM GlobalInputObservations
            WHERE WorkDayId = $day AND ObservedUtc >= $from AND ObservedUtc < $to ORDER BY ObservedUtc;
            """, ("$day", dayId), ("$from", from), ("$to", to)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) globalInputs.Add(new(Utc(reader.GetInt64(0)), Utc(reader.GetInt64(1))));
        }

        return new RawSignalBatch
        {
            Windows = windows,
            VisibilitySegments = visibility,
            Interactions = interactions,
            GlobalInputs = globalInputs,
            SessionEvents = events,
            TrackingStartedUtc = RequireDay(transaction, dayId).TrackingStartedUtc
        };
    }

    private StoredWorkDay RequireDay(SqliteTransaction transaction, long id) =>
        ReadDay(transaction, id) ?? throw new KeyNotFoundException($"Work day {id} does not exist.");

    private StoredWorkDay? ReadDay(SqliteTransaction transaction, long id)
    {
        using var command = Command(transaction, """
            SELECT Id, LocalDate, StartUtc, EndUtc, EndReason, TrackingStartedUtc FROM WorkDays WHERE Id = $id;
            """, ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDayRow(reader) : null;
    }

    private StoredWorkDay? ReadOpenDay(SqliteTransaction transaction)
    {
        using var command = Command(transaction, """
            SELECT Id, LocalDate, StartUtc, EndUtc, EndReason, TrackingStartedUtc FROM WorkDays WHERE EndUtc IS NULL;
            """);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDayRow(reader) : null;
    }

    private static StoredWorkDay ReadDayRow(SqliteDataReader reader) =>
        new(reader.GetInt64(0),
            new WorkDay(DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Utc(reader.GetInt64(2)), reader.IsDBNull(3) ? null : Utc(reader.GetInt64(3)),
                (WorkDayEndReason)reader.GetInt32(4)),
            reader.IsDBNull(5) ? null : Utc(reader.GetInt64(5)));
}
