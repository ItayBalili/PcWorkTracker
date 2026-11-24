using Microsoft.Data.Sqlite;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Data;

namespace WorkTimeTracker.App;

/// <summary>Loads Data's raw signals together with App-owned pause/recovery signals.</summary>
public static class TrackingRawDataReader
{
    public static WorkDayRawData GetRawData(string databasePath, long workDayId)
    {
        using var repository = new SqliteWorkTimeRepository(databasePath);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = System.IO.Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA data_version;";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var before = (long)version.ExecuteScalar()!;
            var raw = repository.GetRawData(workDayId);
            var tracking = TrackingLifecycleStore.ReadEvents(connection, workDayId);
            if ((long)version.ExecuteScalar()! == before) return raw with { TrackingEvents = tracking };
        }
        throw new InvalidOperationException("Tracking changed during the read; retry or use TrackingService.GetRawDataAsync.");
    }
}
