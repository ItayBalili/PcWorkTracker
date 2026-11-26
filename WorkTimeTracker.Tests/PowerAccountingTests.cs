using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Tests;

public sealed class PowerAccountingTests
{
    [Fact]
    public void ShutdownToBootIsNeitherAwayNorUntracked()
    {
        var start = new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var day = new WorkDay(new DateOnly(2025, 1, 1), start, start.AddHours(3), WorkDayEndReason.Manual);
        var raw = new WorkDayRawData(day, start, [], [], [],
            [new(start.AddMinutes(30), SessionEventKind.Shutdown), new(start.AddHours(2), SessionEventKind.Boot),
             new(start.AddHours(2.5), SessionEventKind.AppStart)])
        {
            TrackingEvents = [new(start.AddMinutes(30), TrackingEventKind.Recovery) { EndUtc = start.AddHours(2.5) }]
        };
        var report = CalculationEngine.Calculate(raw, new TrackerSettings(), start.AddHours(3));
        Assert.Equal(TimeSpan.FromMinutes(90), report.PoweredOffTime);
        Assert.Equal(TimeSpan.FromMinutes(30), report.NotTrackedTime);
        Assert.Equal(TimeSpan.FromHours(1), report.AwayTime);
    }

    [Fact]
    public void ProcessCrashWithoutBootIsUntracked()
    {
        var start = new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var day = new WorkDay(new DateOnly(2025, 1, 1), start, start.AddHours(2), WorkDayEndReason.Manual);
        var raw = new WorkDayRawData(day, start, [], [], [], [])
        {
            TrackingEvents = [new(start.AddMinutes(30), TrackingEventKind.Recovery) { EndUtc = start.AddHours(1.5) }]
        };
        var report = CalculationEngine.Calculate(raw, new TrackerSettings(), start.AddHours(2));
        Assert.Equal(TimeSpan.Zero, report.PoweredOffTime);
        Assert.Equal(TimeSpan.FromHours(1), report.NotTrackedTime);
    }
}
