using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Tests;

public sealed class WorkDayBoundaryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AutomaticallyEndsAtLastLockAfterContinuousThreshold()
    {
        var day = WorkDayFactory.StartNew(Start, TimeSpan.Zero);
        var lastLock = Start.AddHours(9).AddMinutes(10);
        var events = new[] { new SessionEvent(lastLock, SessionEventKind.Lock) };

        var notDue = WorkDayBoundaryCalculator.CloseAutomaticallyIfDue(
            day,
            events,
            TimeSpan.FromHours(4),
            lastLock.AddHours(4).AddSeconds(-1));
        var closed = WorkDayBoundaryCalculator.CloseAutomaticallyIfDue(
            day,
            events,
            TimeSpan.FromHours(4),
            lastLock.AddHours(4));

        Assert.Null(notDue);
        Assert.Equal(lastLock, closed?.EndUtc);
        Assert.Equal(WorkDayEndReason.Auto4Hours, closed?.EndReason);
    }

    [Fact]
    public void UnlockBeforeThresholdKeepsSameDayOpen()
    {
        var day = WorkDayFactory.StartNew(Start, TimeSpan.Zero);
        var events = new[]
        {
            new SessionEvent(Start.AddHours(2), SessionEventKind.Lock),
            new SessionEvent(Start.AddHours(3), SessionEventKind.Unlock)
        };

        var result = WorkDayBoundaryCalculator.CloseAutomaticallyIfDue(
            day,
            events,
            TimeSpan.FromHours(4),
            Start.AddHours(10));

        Assert.Null(result);
    }

    [Fact]
    public void StartupUsesTodaysSessionLogonOtherwiseAppStart()
    {
        var appStart = Start.AddHours(1);
        var todaysLogon = Start.AddMinutes(-30);
        var yesterdayLogon = Start.AddDays(-1);
        var today = DateOnly.FromDateTime(Start.DateTime);

        var fromLogon = WorkDayFactory.CreateForStartup(appStart, todaysLogon, today, TimeSpan.Zero);
        var fromAppStart = WorkDayFactory.CreateForStartup(appStart, yesterdayLogon, today, TimeSpan.Zero);

        Assert.Equal(todaysLogon, fromLogon.StartUtc);
        Assert.Equal(appStart, fromAppStart.StartUtc);
    }

    [Fact]
    public void DayStartedBeforeMidnightKeepsItsStartDate()
    {
        var startUtc = new DateTimeOffset(2026, 11, 24, 22, 0, 0, TimeSpan.Zero);
        var day = WorkDayFactory.StartNew(startUtc, TimeSpan.Zero);
        var ended = WorkDayFactory.EndManually(day, startUtc.AddHours(3.5));

        Assert.Equal(new DateOnly(2026, 11, 24), ended.Date);
        Assert.Equal(startUtc.AddHours(3.5), ended.EndUtc);
        Assert.Equal(WorkDayEndReason.Manual, ended.EndReason);
    }

    [Fact]
    public void AutomaticThresholdChangesDoNotReopenOrMoveClosedDays()
    {
        var day = WorkDayFactory.EndManually(
            WorkDayFactory.StartNew(Start, TimeSpan.Zero),
            Start.AddHours(8));

        var result = WorkDayBoundaryCalculator.CloseAutomaticallyIfDue(
            day,
            [new SessionEvent(Start.AddHours(9), SessionEventKind.Lock)],
            TimeSpan.FromHours(1),
            Start.AddHours(10));

        Assert.Equal(day, result);
    }
}
