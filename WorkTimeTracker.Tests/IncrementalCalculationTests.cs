using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Tests;

public sealed class IncrementalCalculationTests
{
    [Fact]
    public void LiveSnapshotsMatchFullCalculationAndReuseStableHistory()
    {
        var start = new DateTimeOffset(2025, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var settings = new TrackerSettings
        {
            ActiveTimeout = TimeSpan.FromSeconds(20),
            AwayTimeout = TimeSpan.FromSeconds(60),
            InteractionWindow = TimeSpan.FromSeconds(90)
        };
        var engine = new IncrementalCalculationEngine();
        var reused = false;
        for (var seconds = 180; seconds <= 360; seconds += 5)
        {
            var raw = Snapshot(start, seconds);
            var now = start.AddSeconds(seconds);
            AssertSame(CalculationEngine.Calculate(raw, settings, now), engine.Calculate(raw, settings, now));
            reused |= engine.LastRecalculatedFromUtc > start;
        }
        Assert.True(reused);

        var changed = Snapshot(start, 365) with
        {
            SessionEvents =
            [
                new(start.AddSeconds(25), SessionEventKind.Lock),
                new(start.AddSeconds(28), SessionEventKind.Unlock)
            ]
        };
        AssertSame(CalculationEngine.Calculate(changed, settings, start.AddSeconds(365)),
            engine.Calculate(changed, settings, start.AddSeconds(365)));
        Assert.Equal(start, engine.LastRecalculatedFromUtc);
    }

    [Fact]
    public void IdleAndLateObservationsMatchFullRecalculation()
    {
        var start = new DateTimeOffset(2025, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var settings = new TrackerSettings { ActiveTimeout = TimeSpan.FromSeconds(10), AwayTimeout = TimeSpan.FromSeconds(30) };
        var engine = new IncrementalCalculationEngine();
        for (var seconds = 95; seconds <= 200; seconds += 5)
        {
            var raw = Snapshot(start, seconds);
            raw = raw with
            {
                Interactions = raw.Interactions.Where(x => x.SecondUtc < start.AddSeconds(70) || x.SecondUtc >= start.AddSeconds(120)).ToArray(),
                GlobalInputs = raw.GlobalInputs.Select(x => x.ObservedUtc >= start.AddSeconds(70) && x.ObservedUtc < start.AddSeconds(120)
                    ? x with { LastInputUtc = start.AddSeconds(69) } : x).ToArray(),
                SessionEvents = seconds >= 150
                    ? [new(start.AddSeconds(45), SessionEventKind.Lock), new(start.AddSeconds(50), SessionEventKind.Unlock)]
                    : []
            };
            if (seconds >= 175)
                raw = raw with { TrackingEvents = [new(start.AddSeconds(20), TrackingEventKind.Recovery) { EndUtc = start.AddSeconds(25) }] };
            var effectiveSettings = seconds >= 185 ? settings with { AwayTimeout = TimeSpan.FromSeconds(40) } : settings;
            if (seconds >= 195) raw = raw with { WorkDay = raw.WorkDay with { StartUtc = start.AddSeconds(5), EndUtc = start.AddSeconds(190) } };
            AssertSame(CalculationEngine.Calculate(raw, effectiveSettings, start.AddSeconds(seconds)),
                engine.Calculate(raw, effectiveSettings, start.AddSeconds(seconds)));
        }
    }

    [Fact]
    public void ClosedDayCacheIsInvalidatedWhenHistoricalDataChanges()
    {
            var start = new DateTimeOffset(2025, 1, 1, 8, 0, 0, TimeSpan.Zero);
            var raw = Snapshot(start, 200);
            raw = raw with { WorkDay = raw.WorkDay with { EndUtc = start.AddSeconds(200) } };
            var engine = new IncrementalCalculationEngine();
            var settings = new TrackerSettings();
            var expected = CalculationEngine.Calculate(raw, settings, start.AddSeconds(200));
            AssertSame(expected, engine.Calculate(raw, settings, start.AddSeconds(200)));
            AssertSame(expected, engine.Calculate(raw, settings, start.AddSeconds(210)));
            Assert.Equal(start.AddSeconds(200), engine.LastRecalculatedFromUtc);
            raw = raw with { SessionEvents = [new(start.AddSeconds(30), SessionEventKind.Lock), new(start.AddSeconds(50), SessionEventKind.Unlock)] };
            AssertSame(CalculationEngine.Calculate(raw, settings, start.AddSeconds(210)), engine.Calculate(raw, settings, start.AddSeconds(210)));
            Assert.Equal(start, engine.LastRecalculatedFromUtc);
    }

    private static WorkDayRawData Snapshot(DateTimeOffset start, int seconds)
    {
        var end = start.AddSeconds(seconds);
        var switchAt = start.AddSeconds(90);
        return new WorkDayRawData(
            new WorkDay(DateOnly.FromDateTime(start.Date), start, null, WorkDayEndReason.Open), start,
            [new("one", "one", "One", null, "one"), new("two", "two", "Two", null, "two")],
            [
                new("one", start, switchAt, 100, true),
                new("one", switchAt, end, 100, false),
                new("two", start, switchAt, 100, false),
                new("two", switchAt, end, 100, true)
            ],
            Enumerable.Range(0, seconds / 3).Select(i =>
                new InteractionSecond(i * 3 < 90 ? "one" : "two", start.AddSeconds(i * 3), InteractionKind.Keyboard)).ToArray(),
            [])
        {
            GlobalInputs = Enumerable.Range(0, seconds).Select(i =>
                new GlobalInputObservation(start.AddSeconds(i), start.AddSeconds(i / 3 * 3))).ToArray(),
            TrackingEvents = seconds >= 260
                ? [new(start.AddSeconds(240), TrackingEventKind.Pause), new(start.AddSeconds(260), TrackingEventKind.Resume)]
                : seconds >= 240 ? [new(start.AddSeconds(240), TrackingEventKind.Pause)] : []
        };
    }

    private static void AssertSame(DayReport expected, DayReport actual)
    {
        Assert.Equal(expected.WorkDay, actual.WorkDay);
        Assert.Equal(expected.EffectiveEndUtc, actual.EffectiveEndUtc);
        Assert.Equal(expected.TotalSpan, actual.TotalSpan);
        Assert.Equal(expected.NotTrackedTime, actual.NotTrackedTime);
        Assert.Equal(expected.AwayTime, actual.AwayTime);
        Assert.Equal(expected.TotalActiveTime, actual.TotalActiveTime);
        Assert.Equal(expected.TotalDisplayedTime, actual.TotalDisplayedTime);
        Assert.Equal(expected.Windows, actual.Windows);
    }
}
