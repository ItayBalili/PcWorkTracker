namespace WorkTimeTracker.Data;

public sealed partial class SqliteWorkTimeRepository
{
    public WorkDayEdit? GetWorkDayEdit(long workDayId) => Read(transaction =>
    {
        using var command = Command(transaction,
            "SELECT StartUtc, EndUtc FROM WorkDayEdits WHERE WorkDayId = $id;", ("$id", workDayId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new WorkDayEdit(workDayId, Utc(reader.GetInt64(0)),
            reader.IsDBNull(1) ? null : Utc(reader.GetInt64(1))) : null;
    });

    /// <summary>Changes report boundaries without altering the collector's original work day or signals.</summary>
    public void SaveWorkDayEdit(WorkDayEdit edit) => Write(transaction =>
    {
        ArgumentNullException.ThrowIfNull(edit);
        var original = RequireDay(transaction, edit.WorkDayId).WorkDay;
        if ((original.EndUtc is null) != (edit.EndUtc is null))
            throw new ArgumentException("An open day must remain open and a closed day must remain closed.", nameof(edit));
        if (original.EndUtc is null && edit.StartUtc > original.StartUtc)
            throw new ArgumentException("An open day's start can only be moved earlier until it closes.", nameof(edit));
        if (edit.EndUtc is { } end && end <= edit.StartUtc)
            throw new ArgumentException("The end must be after the start.", nameof(edit));
        if (edit.StartUtc > timeProvider.GetUtcNow() || edit.EndUtc > timeProvider.GetUtcNow())
            throw new ArgumentException("Day boundaries cannot be in the future.", nameof(edit));

        using var overlap = Command(transaction, """
            SELECT 1 FROM WorkDays d LEFT JOIN WorkDayEdits e ON e.WorkDayId = d.Id
            WHERE d.Id <> $id
              AND ($end IS NULL OR COALESCE(e.StartUtc, d.StartUtc) < $end)
              AND (COALESCE(e.EndUtc, d.EndUtc) IS NULL OR COALESCE(e.EndUtc, d.EndUtc) > $start)
            LIMIT 1;
            """, ("$id", edit.WorkDayId), ("$start", Ticks(edit.StartUtc)), ("$end", NullableTicks(edit.EndUtc)));
        if (overlap.ExecuteScalar() is not null)
            throw new ArgumentException("The edited day overlaps another day.", nameof(edit));

        using var command = Command(transaction, """
            INSERT INTO WorkDayEdits (WorkDayId, StartUtc, EndUtc) VALUES ($id, $start, $end)
            ON CONFLICT (WorkDayId) DO UPDATE SET StartUtc = excluded.StartUtc, EndUtc = excluded.EndUtc;
            """, ("$id", edit.WorkDayId), ("$start", Ticks(edit.StartUtc)), ("$end", NullableTicks(edit.EndUtc)));
        command.ExecuteNonQuery();
        return 0;
    });
}
