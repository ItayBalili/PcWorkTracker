using Microsoft.Data.Sqlite;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Data;

if (args is not ["--crash-probe", var databasePath])
{
    return 1;
}

var start = new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
var repository = new SqliteWorkTimeRepository(databasePath);
var day = repository.CreateWorkDay(WorkDayFactory.StartNew(start, TimeSpan.Zero), start);
repository.WriteBatch(day.Id, new RawSignalBatch
{
    Windows = [new WindowInfo("editor", "editor", "Editor", null, "Notes")],
    VisibilitySegments = [new VisibilitySegment("editor", start, start.AddSeconds(30), 40.4m, true)],
    Interactions = [new InteractionSecond("editor", start, InteractionKind.Keyboard)]
});
repository.Flush();

var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
connection.Open();
var transaction = connection.BeginTransaction();
var command = connection.CreateCommand();
command.Transaction = transaction;
command.CommandText = """
    INSERT INTO InteractionSeconds (WorkDayId, WindowId, SecondUtc, Kind) VALUES ($day, 'editor', $second, 2);
    """;
command.Parameters.AddWithValue("$day", day.Id);
command.Parameters.AddWithValue("$second", start.AddSeconds(31).UtcDateTime.Ticks);
command.ExecuteNonQuery();

// Exit without Dispose, leaving both a WAL and an uncommitted transaction behind.
Environment.Exit(0);
return 0;
