using Microsoft.Data.Sqlite;

namespace WorkTimeTracker.Data;

internal static class Schema
{
    internal const int Version = 3;

    internal static void Migrate(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > Version)
        {
            throw new NotSupportedException($"Database schema {version} is newer than supported schema {Version}.");
        }

        if (version == 0)
        {
            command.CommandText = """
                CREATE TABLE WorkDays (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    LocalDate TEXT NOT NULL,
                    StartUtc INTEGER NOT NULL,
                    EndUtc INTEGER,
                    EndReason INTEGER NOT NULL CHECK (EndReason BETWEEN 0 AND 2),
                    TrackingStartedUtc INTEGER,
                    CHECK (EndUtc IS NULL OR EndUtc >= StartUtc),
                    CHECK ((EndUtc IS NULL AND EndReason = 0) OR (EndUtc IS NOT NULL AND EndReason <> 0)),
                    CHECK (TrackingStartedUtc IS NULL OR TrackingStartedUtc >= StartUtc)
                );
                CREATE UNIQUE INDEX IX_WorkDays_OneOpen ON WorkDays ((1)) WHERE EndUtc IS NULL;
                CREATE INDEX IX_WorkDays_Date ON WorkDays (LocalDate, StartUtc, Id);
                CREATE INDEX IX_WorkDays_EndUtc ON WorkDays (EndUtc) WHERE EndUtc IS NOT NULL;
                CREATE TABLE Apps (
                    Id INTEGER PRIMARY KEY,
                    ProcessName TEXT NOT NULL,
                    FriendlyName TEXT NOT NULL,
                    ExecutablePath TEXT NOT NULL,
                    PathIsNull INTEGER NOT NULL CHECK (PathIsNull IN (0, 1)),
                    UNIQUE (ProcessName, FriendlyName, ExecutablePath, PathIsNull)
                );
                CREATE TABLE Windows (
                    WorkDayId INTEGER NOT NULL REFERENCES WorkDays (Id) ON DELETE CASCADE,
                    WindowId TEXT NOT NULL,
                    AppId INTEGER NOT NULL REFERENCES Apps (Id),
                    Title TEXT NOT NULL,
                    PRIMARY KEY (WorkDayId, WindowId)
                );
                CREATE INDEX IX_Windows_AppId ON Windows (AppId);
                CREATE TABLE VisibilitySegments (
                    WorkDayId INTEGER NOT NULL,
                    WindowId TEXT NOT NULL,
                    StartUtc INTEGER NOT NULL,
                    EndUtc INTEGER NOT NULL CHECK (EndUtc > StartUtc),
                    VisibleTenths INTEGER NOT NULL CHECK (VisibleTenths BETWEEN 0 AND 1000),
                    IsForeground INTEGER NOT NULL CHECK (IsForeground IN (0, 1)),
                    PRIMARY KEY (WorkDayId, WindowId, StartUtc),
                    FOREIGN KEY (WorkDayId, WindowId) REFERENCES Windows (WorkDayId, WindowId) ON DELETE CASCADE
                );
                CREATE INDEX IX_Visibility_Time ON VisibilitySegments (WorkDayId, EndUtc, StartUtc);
                CREATE INDEX IX_Visibility_WindowEnd ON VisibilitySegments (WorkDayId, WindowId, EndUtc, StartUtc);
                CREATE INDEX IX_Visibility_Foreground ON VisibilitySegments (WorkDayId, EndUtc, StartUtc)
                    WHERE IsForeground = 1;
                CREATE TABLE InteractionSeconds (
                    WorkDayId INTEGER NOT NULL,
                    WindowId TEXT NOT NULL,
                    SecondUtc INTEGER NOT NULL CHECK (SecondUtc % 10000000 = 0),
                    Kind INTEGER NOT NULL CHECK (Kind BETWEEN 0 AND 15),
                    PRIMARY KEY (WorkDayId, WindowId, SecondUtc),
                    FOREIGN KEY (WorkDayId, WindowId) REFERENCES Windows (WorkDayId, WindowId) ON DELETE CASCADE
                );
                CREATE INDEX IX_Interactions_Time ON InteractionSeconds (WorkDayId, SecondUtc);
                CREATE TABLE SessionEvents (
                    Id INTEGER PRIMARY KEY,
                    WorkDayId INTEGER NOT NULL REFERENCES WorkDays (Id) ON DELETE CASCADE,
                    TimeUtc INTEGER NOT NULL,
                    Kind INTEGER NOT NULL CHECK (Kind BETWEEN 0 AND 8),
                    UNIQUE (WorkDayId, TimeUtc, Kind)
                );
                CREATE INDEX IX_SessionEvents_Time ON SessionEvents (WorkDayId, TimeUtc, Id);
                CREATE TABLE RepositoryState (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    LastFlushedUtc INTEGER NOT NULL
                );
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 2)
        {
            command.CommandText = """
                CREATE TABLE GlobalInputObservations (
                    WorkDayId INTEGER NOT NULL REFERENCES WorkDays (Id) ON DELETE CASCADE,
                    ObservedUtc INTEGER NOT NULL,
                    LastInputUtc INTEGER NOT NULL CHECK (LastInputUtc <= ObservedUtc),
                    PRIMARY KEY (WorkDayId, ObservedUtc)
                );
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 3)
        {
            command.CommandText = """
                CREATE TABLE WorkDayEdits (
                    WorkDayId INTEGER PRIMARY KEY REFERENCES WorkDays (Id) ON DELETE CASCADE,
                    StartUtc INTEGER NOT NULL,
                    EndUtc INTEGER,
                    CHECK (EndUtc IS NULL OR EndUtc > StartUtc)
                );
                PRAGMA user_version = 3;
                """;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
