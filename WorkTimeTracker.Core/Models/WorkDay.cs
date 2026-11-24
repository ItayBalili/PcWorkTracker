namespace WorkTimeTracker.Core.Models;

public enum WorkDayEndReason
{
    Open,
    Manual,
    Auto4Hours
}

public sealed record WorkDay(
    DateOnly Date,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    WorkDayEndReason EndReason);

public static class WorkDayFactory
{
    public static WorkDay CreateForStartup(
        DateTimeOffset appStartUtc,
        DateTimeOffset? sessionLogonUtc,
        DateOnly todayLocal,
        TimeSpan localUtcOffset)
    {
        var startUtc = appStartUtc.ToUniversalTime();
        if (sessionLogonUtc is { } logonUtc &&
            logonUtc <= appStartUtc &&
            DateOnly.FromDateTime(logonUtc.ToOffset(localUtcOffset).DateTime) == todayLocal)
        {
            startUtc = logonUtc.ToUniversalTime();
        }

        return CreateOpen(startUtc, localUtcOffset);
    }

    public static WorkDay StartNew(DateTimeOffset startUtc, TimeSpan localUtcOffset) =>
        CreateOpen(startUtc.ToUniversalTime(), localUtcOffset);

    public static WorkDay EndManually(WorkDay day, DateTimeOffset clickedUtc)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (day.EndUtc is not null)
        {
            throw new InvalidOperationException("The work day has already ended.");
        }

        var endUtc = clickedUtc.ToUniversalTime();
        if (endUtc < day.StartUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(clickedUtc));
        }

        return day with { EndUtc = endUtc, EndReason = WorkDayEndReason.Manual };
    }

    private static WorkDay CreateOpen(DateTimeOffset startUtc, TimeSpan localUtcOffset)
    {
        var date = DateOnly.FromDateTime(startUtc.ToOffset(localUtcOffset).DateTime);
        return new WorkDay(date, startUtc, null, WorkDayEndReason.Open);
    }
}
