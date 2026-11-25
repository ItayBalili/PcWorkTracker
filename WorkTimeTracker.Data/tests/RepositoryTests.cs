using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Data.Tests;

public sealed class RepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
    private static readonly WindowInfo Editor = new("editor", "editor", "Editor", null, "Notes");
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "test-databases", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(directory, "tracker.db");

    [Fact]
    public void ManualBoundariesPreserveOriginalAndRejectOverlaps()
    {
        using var repository = Open();
        var start = Start.AddYears(-10);
        var first = repository.CreateWorkDay(new WorkDay(DateOnly.FromDateTime(start.Date),
            start, start.AddHours(1), WorkDayEndReason.Manual), start);
        var second = repository.CreateWorkDay(new WorkDay(DateOnly.FromDateTime(start.Date),
            start.AddHours(2), start.AddHours(3), WorkDayEndReason.Manual), start.AddHours(2));
        var edit = new WorkDayEdit(first.Id, start.AddMinutes(10), start.AddMinutes(90));
        repository.SaveWorkDayEdit(edit);
        Assert.Equal(edit, repository.GetWorkDayEdit(first.Id));
        Assert.Equal(first.WorkDay, repository.GetWorkDay(first.Id)!.WorkDay);
        Assert.Throws<ArgumentException>(() => repository.SaveWorkDayEdit(edit with { EndUtc = edit.StartUtc }));
        Assert.Throws<ArgumentException>(() => repository.SaveWorkDayEdit(edit with { EndUtc = start.AddHours(2).AddSeconds(1) }));
        Assert.Throws<ArgumentException>(() => repository.SaveWorkDayEdit(new(second.Id, start.AddMinutes(89), start.AddHours(3))));
        Assert.Equal(edit, repository.GetWorkDayEdit(first.Id));
        repository.SaveWorkDayEdit(new(second.Id, start.AddMinutes(90), start.AddHours(3)));
    }

    [Fact]
    public void OpenDayEditRetainsOriginalEndWhenDayEventuallyCloses()
    {
        using var repository = Open();
        var start = Start.AddYears(-10);
        var day = repository.CreateWorkDay(WorkDayFactory.StartNew(start, TimeSpan.Zero), start);
        repository.SaveWorkDayEdit(new(day.Id, start.AddMinutes(-1), null));
        Assert.Throws<ArgumentException>(() => repository.SaveWorkDayEdit(new(day.Id, start.AddMinutes(1), null)));
        Assert.Throws<ArgumentException>(() => repository.SaveWorkDayEdit(new(day.Id, start, start.AddMinutes(2))));
        repository.UpdateWorkDay(day.Id, WorkDayFactory.EndManually(day.WorkDay, start.AddMinutes(10)));
        Assert.Equal(start.AddMinutes(10), repository.GetWorkDay(day.Id)!.WorkDay.EndUtc);
        Assert.Null(repository.GetWorkDayEdit(day.Id)!.EndUtc);
    }

    [Fact]
    public void RawRoundTripPreservesPrecisionUtcAndRecalculation()
    {
        long id;
        using (var repository = Open())
        {
            id = repository.CreateWorkDay(WorkDayFactory.StartNew(Start.ToOffset(TimeSpan.FromHours(2)), TimeSpan.FromHours(2))).Id;
            repository.WriteBatch(id, new RawSignalBatch
            {
                TrackingStartedUtc = Start,
                Windows = [Editor],
                VisibilitySegments =
                [
                    new("editor", Start, Start.AddSeconds(1), 40.41m, true),
                    new("editor", Start.AddSeconds(1), Start.AddSeconds(2), 40.44m, true),
                    new("editor", Start.AddSeconds(2), Start.AddSeconds(3), 40.5m, true)
                ],
                Interactions =
                [
                    new("editor", Start.AddMilliseconds(10).ToOffset(TimeSpan.FromHours(2)), InteractionKind.Keyboard),
                    new("editor", Start.AddMilliseconds(800), InteractionKind.Click | InteractionKind.Hover),
                    new("editor", Start.AddSeconds(1), InteractionKind.Scroll)
                ],
                SessionEvents = [new(Start, SessionEventKind.AppStart)]
            });
            repository.UpdateWorkDay(id, WorkDayFactory.EndManually(repository.GetWorkDay(id)!.WorkDay, Start.AddSeconds(3)));
        }

        using var reopened = Open();
        var data = reopened.GetRawData(id);
        Assert.Equal(Start, data.TrackingStartedUtc);
        Assert.Equal(TimeSpan.Zero, data.WorkDay.StartUtc.Offset);
        Assert.Equal(Editor, Assert.Single(data.Windows));
        Assert.Equal(2, data.VisibilitySegments.Count);
        Assert.Equal(40.4m, data.VisibilitySegments[0].VisiblePercent);
        Assert.Equal(Start.AddSeconds(2), data.VisibilitySegments[0].EndUtc);
        Assert.Equal(InteractionKind.Keyboard | InteractionKind.Click | InteractionKind.Hover, data.Interactions[0].Kind);
        Assert.Equal(Start, data.Interactions[0].SecondUtc);
        Assert.Single(data.SessionEvents);
        var low = CalculationEngine.Calculate(data, new TrackerSettings { MinimumVisiblePercent = 40.4m }, Start.AddSeconds(3));
        var high = CalculationEngine.Calculate(data, new TrackerSettings { MinimumVisiblePercent = 40.5m }, Start.AddSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), low.TotalDisplayedTime);
        Assert.Equal(TimeSpan.FromSeconds(1), high.TotalDisplayedTime);
    }

    [Fact]
    public void RleBridgesAcrossFlushesAndReplaysButRejectsConflictsAtomically()
    {
        using var repository = Open();
        var id = repository.CreateWorkDay(WorkDayFactory.StartNew(Start, TimeSpan.Zero), Start).Id;
        repository.WriteBatch(id, new RawSignalBatch
        {
            Windows = [Editor],
            VisibilitySegments = [new("editor", Start, Start.AddSeconds(1), 25.3m, false)]
        });
        repository.Flush();
        var batch = new RawSignalBatch
        {
            VisibilitySegments =
            [
                new("editor", Start.AddSeconds(2), Start.AddSeconds(3), 25.3m, false),
                new("editor", Start.AddSeconds(1), Start.AddSeconds(2), 25.3m, false)
            ],
            Interactions = [new("editor", Start, InteractionKind.Hover)]
        };
        repository.WriteBatch(id, batch);
        repository.WriteBatch(id, batch);
        var segment = Assert.Single(repository.GetRawData(id).VisibilitySegments);
        Assert.Equal(Start.AddSeconds(3), segment.EndUtc);
        var checkpoint = repository.GetRecoveryState().LastFlushedUtc;
        Assert.Throws<ArgumentException>(() => repository.WriteBatch(id, new RawSignalBatch
        {
            Windows = [new("other", "other", "Other", "", "Other")],
            VisibilitySegments = [new("editor", Start.AddSeconds(1), Start.AddSeconds(2), 25.4m, false)]
        }));
        Assert.Single(repository.GetRawData(id).Windows);
        Assert.Equal(checkpoint, repository.GetRecoveryState().LastFlushedUtc);
        Assert.Throws<SqliteException>(() => repository.WriteBatch(id, new RawSignalBatch
        {
            Interactions =
            [
                new("editor", Start.AddSeconds(4), InteractionKind.Click),
                new("missing", Start.AddSeconds(5), InteractionKind.Click)
            ]
        }));
        Assert.Single(repository.GetRawData(id).Interactions);
    }

    [Fact]
    public void ForegroundOverlapAndMetadataMutationAreRejected()
    {
        using var repository = Open();
        var id = repository.CreateWorkDay(WorkDayFactory.StartNew(Start, TimeSpan.Zero), Start).Id;
        repository.WriteBatch(id, new RawSignalBatch
        {
            Windows = [Editor],
            VisibilitySegments = [new("editor", Start, Start.AddSeconds(10), 100m, true)]
        });
        Assert.Throws<ArgumentException>(() => repository.WriteBatch(id, new RawSignalBatch
        {
            Windows = [Editor with { Id = "other" }],
            VisibilitySegments = [new("other", Start.AddSeconds(5), Start.AddSeconds(15), 100m, true)]
        }));
        Assert.Throws<ArgumentException>(() => repository.WriteBatch(id, new RawSignalBatch
        {
            Windows = [Editor with { Title = "Changed" }]
        }));
        Assert.Single(repository.GetRawData(id).Windows);
    }

    [Fact]
    public void RangeQueriesAreHalfOpenAndReturnIntersectingWholeRuns()
    {
        using var repository = Open();
        var id = repository.CreateWorkDay(WorkDayFactory.StartNew(Start, TimeSpan.Zero), Start).Id;
        repository.WriteBatch(id, new RawSignalBatch
        {
            Windows = [Editor],
            VisibilitySegments = [new("editor", Start, Start.AddSeconds(10), 40.4m, false)],
            Interactions =
            [
                new("editor", Start, InteractionKind.Keyboard),
                new("editor", Start.AddSeconds(3), InteractionKind.Scroll),
                new("editor", Start.AddSeconds(5), InteractionKind.Click)
            ],
            SessionEvents =
            [
                new(Start.AddSeconds(3), SessionEventKind.Lock),
                new(Start.AddSeconds(5), SessionEventKind.Unlock)
            ]
        });
        var range = repository.ReadSignals(id, Start.AddSeconds(3), Start.AddSeconds(5));
        Assert.Equal(Start, Assert.Single(range.VisibilitySegments).StartUtc);
        Assert.Equal(Start.AddSeconds(3), Assert.Single(range.Interactions).SecondUtc);
        Assert.Equal(SessionEventKind.Lock, Assert.Single(range.SessionEvents).Kind);
        Assert.Empty(repository.ReadSignals(id, Start.AddSeconds(10), Start.AddSeconds(11)).VisibilitySegments);
    }

    [Fact]
    public void RetentionPreservesOpenDayAndDoesNotReuseDeletedIds()
    {
        using var repository = Open();
        var first = repository.CreateWorkDay(WorkDayFactory.StartNew(Start, TimeSpan.Zero), Start);
        repository.WriteBatch(first.Id, new RawSignalBatch
        {
            Windows = [Editor],
            VisibilitySegments = [new("editor", Start, Start.AddMinutes(1), 100m, true)],
            Interactions = [new("editor", Start, InteractionKind.Click)],
            SessionEvents = [new(Start, SessionEventKind.AppStart)]
        });
        Assert.Throws<SqliteException>(() => repository.CreateWorkDay(WorkDayFactory.StartNew(Start, TimeSpan.Zero)));
        var ended = WorkDayFactory.EndManually(first.WorkDay, Start.AddMinutes(1));
        repository.UpdateWorkDay(first.Id, ended);
        Assert.Equal(0, repository.ApplyRetention(Start.AddMinutes(1)));
        Assert.Equal(1, repository.ApplyRetention(Start.AddMinutes(2)));
        var second = repository.CreateWorkDay(WorkDayFactory.StartNew(Start.AddHours(1), TimeSpan.Zero), Start.AddHours(1));
        Assert.True(second.Id > first.Id);
        var third = repository.CreateWorkDay(
            new WorkDay(first.WorkDay.Date, Start.AddDays(-1), Start, WorkDayEndReason.Manual));
        Assert.Equal(2, repository.ListWorkDays(first.WorkDay.Date, first.WorkDay.Date).Count);
        Assert.Equal(second, repository.GetOpenWorkDay());
        Assert.Equal(1, repository.ApplyRetention(Start.AddDays(10)));
        Assert.Equal(second, Assert.Single(repository.ListWorkDays()));
        Assert.Null(repository.GetWorkDay(first.Id));
        Assert.Throws<KeyNotFoundException>(() => repository.GetRawData(third.Id));
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM Apps) + (SELECT COUNT(*) FROM Windows) +
                   (SELECT COUNT(*) FROM VisibilitySegments) + (SELECT COUNT(*) FROM InteractionSeconds) +
                   (SELECT COUNT(*) FROM SessionEvents);
            """;
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task ConcurrentRepositoriesSerializeFlagUnionsAndRle()
    {
        using var first = Open();
        using var second = Open();
        var id = first.CreateWorkDay(WorkDayFactory.StartNew(Start, TimeSpan.Zero), Start).Id;
        first.WriteBatch(id, new RawSignalBatch { Windows = [Editor] });
        await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            var repository = index % 2 == 0 ? first : second;
            repository.WriteBatch(id, new RawSignalBatch
            {
                Interactions = [new("editor", Start, (InteractionKind)(1 << (index % 4)))],
                VisibilitySegments = [new("editor", Start.AddSeconds(index), Start.AddSeconds(index + 1), 40.4m, true)]
            });
        })));
        var data = second.GetRawData(id);
        Assert.Equal((InteractionKind)15, Assert.Single(data.Interactions).Kind);
        Assert.Equal(Start.AddSeconds(64), Assert.Single(data.VisibilitySegments).EndUtc);
    }

    [Fact]
    public async Task AbruptProcessExitRecoversLastFlushNotUncommittedRows()
    {
        Directory.CreateDirectory(directory);
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        info.ArgumentList.Add(typeof(RepositoryTests).Assembly.Location);
        info.ArgumentList.Add("--crash-probe");
        info.ArgumentList.Add(DatabasePath);
        using var process = Process.Start(info)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        using var recovered = Open();
        var state = recovered.GetRecoveryState();
        Assert.NotNull(state.LastFlushedUtc);
        Assert.NotNull(state.OpenWorkDay);
        var data = recovered.GetRawData(state.OpenWorkDay.Id);
        Assert.Single(data.Interactions);
        Assert.Equal(Start.AddSeconds(30), Assert.Single(data.VisibilitySegments).EndUtc);
        Assert.Null(data.WorkDay.EndUtc);
    }

    [Fact]
    public void MigrationIsIdempotentAndNewerSchemaIsNotDowngraded()
    {
        using (Open()) { }
        using (var repository = Open())
        {
            Assert.Empty(repository.ListWorkDays());
            Assert.Null(repository.GetRecoveryState().LastFlushedUtc);
        }

        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 999;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<NotSupportedException>(() => Open());
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void ClosedDaysCannotReopenAndDisposedRepositoryRejectsReads()
    {
        var repository = Open();
        var original = WorkDayFactory.StartNew(Start, TimeSpan.Zero);
        var day = repository.CreateWorkDay(original);
        var ended = WorkDayFactory.EndManually(original, Start.AddMinutes(1));
        repository.UpdateWorkDay(day.Id, ended);
        repository.UpdateWorkDay(day.Id, ended);
        Assert.Throws<InvalidOperationException>(() => repository.UpdateWorkDay(day.Id, original));
        repository.Dispose();
        repository.Dispose();
        Assert.Throws<ObjectDisposedException>(() => repository.GetOpenWorkDay());
    }

    [Fact]
    public void GlobalInputRoundTripRequiresNoAppIdentityAndPreservesUtcAndHalfOpenRanges()
    {
        long id;
        using (var repository = Open())
        {
            id = repository.CreateWorkDay(WorkDayFactory.StartNew(Start, TimeSpan.Zero), Start).Id;
            var batch = new RawSignalBatch
            {
                GlobalInputs =
                [
                    new(Start.AddSeconds(1).ToOffset(TimeSpan.FromHours(2)), Start.AddHours(-1)),
                    new(Start.AddSeconds(2), Start.AddSeconds(1.25)),
                    new(Start.AddSeconds(3), Start.AddSeconds(2))
                ]
            };
            repository.WriteBatch(id, batch);
            repository.WriteBatch(id, batch);
            Assert.Equal(2, repository.ReadSignals(id, Start.AddSeconds(1), Start.AddSeconds(3)).GlobalInputs.Count);
        }
        using var reopened = Open();
        var raw = reopened.GetRawData(id);
        Assert.Equal(3, raw.GlobalInputs.Count);
        Assert.Equal(TimeSpan.Zero, raw.GlobalInputs[0].ObservedUtc.Offset);
        Assert.Equal(Start.AddHours(-1), raw.GlobalInputs[0].LastInputUtc);
        Assert.Empty(raw.Windows);
        Assert.Empty(raw.Interactions);
        var checkpoint = reopened.GetRecoveryState().LastFlushedUtc;
        Assert.Throws<ArgumentException>(() => reopened.WriteBatch(id, new()
        {
            Windows = [Editor],
            GlobalInputs = [new(Start.AddSeconds(4), Start.AddSeconds(3)), new(Start.AddSeconds(5), Start.AddSeconds(6))]
        }));
        Assert.Empty(reopened.GetRawData(id).Windows);
        Assert.Equal(3, reopened.GetRawData(id).GlobalInputs.Count);
        Assert.Equal(checkpoint, reopened.GetRecoveryState().LastFlushedUtc);
    }

    [Fact]
    public void VersionOneDatabaseMigratesGlobalInputWithoutChangingExistingRawSignals()
    {
        long id;
        using (var repository = Open())
        {
            id = repository.CreateWorkDay(WorkDayFactory.StartNew(Start, TimeSpan.Zero), Start).Id;
            repository.WriteBatch(id, new() { Windows = [Editor], Interactions = [new(Editor.Id, Start, InteractionKind.Click)] });
        }
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE GlobalInputObservations; DROP TABLE WorkDayEdits; PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }
        using var migrated = Open();
        Assert.Single(migrated.GetRawData(id).Interactions);
        migrated.WriteBatch(id, new() { GlobalInputs = [new(Start.AddSeconds(1), Start)] });
        Assert.Single(migrated.GetRawData(id).GlobalInputs);
        Assert.Equal(Editor, Assert.Single(migrated.GetRawData(id).Windows));
    }

    private SqliteWorkTimeRepository Open() => new(DatabasePath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
