using Microsoft.Data.Sqlite;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Data;

public sealed partial class SqliteWorkTimeRepository
{
    private static VisibilitySegment[] CoalesceVisibility(VisibilitySegment[] segments)
    {
        foreach (var segment in segments)
        {
            ArgumentNullException.ThrowIfNull(segment);
        }

        var result = new List<VisibilitySegment>();
        foreach (var window in segments.GroupBy(segment => segment.WindowId, StringComparer.Ordinal))
        {
            foreach (var next in window.OrderBy(segment => segment.StartUtc))
            {
                if (result.Count > 0 && result[^1] is var previous &&
                    previous.WindowId == next.WindowId && previous.EndUtc >= next.StartUtc)
                {
                    if (previous.VisiblePercent == next.VisiblePercent && previous.IsForeground == next.IsForeground)
                    {
                        result[^1] = new VisibilitySegment(previous.WindowId, previous.StartUtc,
                            previous.EndUtc > next.EndUtc ? previous.EndUtc : next.EndUtc,
                            previous.VisiblePercent, previous.IsForeground);
                        continue;
                    }

                    if (previous.EndUtc > next.StartUtc)
                    {
                        throw new ArgumentException($"Conflicting visibility overlaps for window '{next.WindowId}'.");
                    }
                }

                result.Add(next);
            }
        }

        return result.ToArray();
    }

    private void WriteWindows(SqliteTransaction transaction, long dayId, WindowInfo[] windows)
    {
        using var app = Command(transaction, """
            INSERT INTO Apps (ProcessName, FriendlyName, ExecutablePath, PathIsNull)
            VALUES ($process, $friendly, $path, $null)
            ON CONFLICT (ProcessName, FriendlyName, ExecutablePath, PathIsNull) DO UPDATE SET Id = Id
            RETURNING Id;
            """, ("$process", ""), ("$friendly", ""), ("$path", ""), ("$null", 0));
        using var window = Command(transaction, """
            INSERT INTO Windows (WorkDayId, WindowId, AppId, Title) VALUES ($day, $window, $app, $title)
            ON CONFLICT (WorkDayId, WindowId) DO NOTHING;
            """, ("$day", dayId), ("$window", ""), ("$app", 0L), ("$title", ""));
        using var existing = Command(transaction,
            "SELECT AppId, Title FROM Windows WHERE WorkDayId = $day AND WindowId = $window;",
            ("$day", dayId), ("$window", ""));
        app.Prepare();
        window.Prepare();
        existing.Prepare();
        foreach (var info in windows)
        {
            ArgumentNullException.ThrowIfNull(info);
            ArgumentException.ThrowIfNullOrWhiteSpace(info.Id);
            ArgumentNullException.ThrowIfNull(info.ProcessName);
            ArgumentNullException.ThrowIfNull(info.FriendlyName);
            ArgumentNullException.ThrowIfNull(info.Title);
            app.Parameters["$process"].Value = info.ProcessName;
            app.Parameters["$friendly"].Value = info.FriendlyName;
            app.Parameters["$path"].Value = info.ExecutablePath ?? "";
            app.Parameters["$null"].Value = info.ExecutablePath is null ? 1 : 0;
            var appId = (long)app.ExecuteScalar()!;
            window.Parameters["$window"].Value = info.Id;
            window.Parameters["$app"].Value = appId;
            window.Parameters["$title"].Value = info.Title;
            window.ExecuteNonQuery();
            existing.Parameters["$window"].Value = info.Id;
            using var reader = existing.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != appId || reader.GetString(1) != info.Title)
            {
                throw new ArgumentException($"Window '{info.Id}' already has different metadata in this work day.");
            }
        }
    }

    private void WriteVisibility(SqliteTransaction transaction, long dayId, DateTimeOffset dayStart, VisibilitySegment[] segments)
    {
        using var foreground = Command(transaction, """
            SELECT 1 FROM VisibilitySegments
            WHERE WorkDayId = $day AND IsForeground = 1 AND WindowId <> $window
              AND StartUtc < $end AND EndUtc > $start LIMIT 1;
            """, ("$day", dayId), ("$window", ""), ("$start", 0L), ("$end", 0L));
        using var neighbors = Command(transaction, """
            SELECT StartUtc, EndUtc, VisibleTenths, IsForeground FROM VisibilitySegments
            WHERE WorkDayId = $day AND WindowId = $window AND StartUtc <= $end AND EndUtc >= $start
            ORDER BY StartUtc;
            """, ("$day", dayId), ("$window", ""), ("$start", 0L), ("$end", 0L));
        using var delete = Command(transaction, """
            DELETE FROM VisibilitySegments WHERE WorkDayId = $day AND WindowId = $window AND StartUtc = $start;
            """, ("$day", dayId), ("$window", ""), ("$start", 0L));
        using var insert = Command(transaction, """
            INSERT INTO VisibilitySegments (WorkDayId, WindowId, StartUtc, EndUtc, VisibleTenths, IsForeground)
            VALUES ($day, $window, $start, $end, $visible, $foreground);
            """, ("$day", dayId), ("$window", ""), ("$start", 0L), ("$end", 0L),
            ("$visible", 0), ("$foreground", 0));
        foreground.Prepare();
        neighbors.Prepare();
        delete.Prepare();
        insert.Prepare();
        foreach (var segment in segments)
        {
            if (segment.StartUtc < dayStart)
            {
                throw new ArgumentException("Visibility cannot start before the work day.");
            }

            var start = Ticks(segment.StartUtc);
            var end = Ticks(segment.EndUtc);
            var visible = decimal.ToInt32(segment.VisiblePercent * 10m);
            if (segment.IsForeground)
            {
                foreground.Parameters["$window"].Value = segment.WindowId;
                foreground.Parameters["$start"].Value = start;
                foreground.Parameters["$end"].Value = end;
                if (foreground.ExecuteScalar() is not null)
                {
                    throw new ArgumentException("Foreground visibility segments cannot overlap across windows.");
                }
            }

            neighbors.Parameters["$window"].Value = segment.WindowId;
            neighbors.Parameters["$start"].Value = start;
            neighbors.Parameters["$end"].Value = end;
            var mergedStarts = new List<long>();
            using (var reader = neighbors.ExecuteReader())
            {
                while (reader.Read())
                {
                    var previousStart = reader.GetInt64(0);
                    var previousEnd = reader.GetInt64(1);
                    var sameState = reader.GetInt32(2) == visible && reader.GetBoolean(3) == segment.IsForeground;
                    if (!sameState && previousStart < end && previousEnd > start)
                    {
                        throw new ArgumentException($"Conflicting visibility overlaps for window '{segment.WindowId}'.");
                    }

                    if (sameState)
                    {
                        start = Math.Min(start, previousStart);
                        end = Math.Max(end, previousEnd);
                        mergedStarts.Add(previousStart);
                    }
                }
            }

            delete.Parameters["$window"].Value = segment.WindowId;
            foreach (var mergedStart in mergedStarts)
            {
                delete.Parameters["$start"].Value = mergedStart;
                delete.ExecuteNonQuery();
            }

            insert.Parameters["$window"].Value = segment.WindowId;
            insert.Parameters["$start"].Value = start;
            insert.Parameters["$end"].Value = end;
            insert.Parameters["$visible"].Value = visible;
            insert.Parameters["$foreground"].Value = segment.IsForeground;
            insert.ExecuteNonQuery();
        }
    }

    private void WriteInteractions(SqliteTransaction transaction, long dayId, DateTimeOffset dayStart, InteractionSecond[] interactions)
    {
        using var command = Command(transaction, """
            INSERT INTO InteractionSeconds (WorkDayId, WindowId, SecondUtc, Kind) VALUES ($day, $window, $second, $kind)
            ON CONFLICT (WorkDayId, WindowId, SecondUtc) DO UPDATE SET Kind = Kind | excluded.Kind;
            """, ("$day", dayId), ("$window", ""), ("$second", 0L), ("$kind", 0));
        command.Prepare();
        var firstSecond = Ticks(dayStart) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond;
        foreach (var interaction in interactions)
        {
            ArgumentNullException.ThrowIfNull(interaction);
            ArgumentException.ThrowIfNullOrWhiteSpace(interaction.WindowId);
            var second = Ticks(interaction.SecondUtc) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond;
            if (second < firstSecond || ((int)interaction.Kind & ~15) != 0)
            {
                throw new ArgumentException("Invalid interaction time or flags.");
            }

            command.Parameters["$window"].Value = interaction.WindowId;
            command.Parameters["$second"].Value = second;
            command.Parameters["$kind"].Value = (int)interaction.Kind;
            command.ExecuteNonQuery();
        }
    }

    private void WriteGlobalInputs(SqliteTransaction transaction, long dayId, DateTimeOffset dayStart, GlobalInputObservation[] observations)
    {
        using var command = Command(transaction, """
            INSERT INTO GlobalInputObservations (WorkDayId, ObservedUtc, LastInputUtc) VALUES ($day, $observed, $input)
            ON CONFLICT (WorkDayId, ObservedUtc) DO UPDATE SET LastInputUtc = MAX(LastInputUtc, excluded.LastInputUtc);
            """, ("$day", dayId), ("$observed", 0L), ("$input", 0L));
        command.Prepare();
        foreach (var observation in observations)
        {
            ArgumentNullException.ThrowIfNull(observation);
            if (observation.ObservedUtc < dayStart || observation.LastInputUtc > observation.ObservedUtc)
                throw new ArgumentException("Invalid global input observation.");
            command.Parameters["$observed"].Value = Ticks(observation.ObservedUtc);
            command.Parameters["$input"].Value = Ticks(observation.LastInputUtc);
            command.ExecuteNonQuery();
        }
    }

    private void WriteSessionEvents(SqliteTransaction transaction, long dayId, DateTimeOffset dayStart, SessionEvent[] events)
    {
        using var command = Command(transaction, """
            INSERT INTO SessionEvents (WorkDayId, TimeUtc, Kind) VALUES ($day, $time, $kind)
            ON CONFLICT (WorkDayId, TimeUtc, Kind) DO NOTHING;
            """, ("$day", dayId), ("$time", 0L), ("$kind", 0));
        command.Prepare();
        foreach (var sessionEvent in events)
        {
            ArgumentNullException.ThrowIfNull(sessionEvent);
            if (sessionEvent.TimeUtc < dayStart || !Enum.IsDefined(sessionEvent.Kind))
            {
                throw new ArgumentException("Invalid session event time or kind.");
            }

            command.Parameters["$time"].Value = Ticks(sessionEvent.TimeUtc);
            command.Parameters["$kind"].Value = (int)sessionEvent.Kind;
            command.ExecuteNonQuery();
        }
    }
}
