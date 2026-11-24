using System.Diagnostics;
using System.IO;

namespace WorkTimeTracker.App;

internal static class DashboardLog
{
    private static readonly object Gate = new();
    public static bool Write(Exception error)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkTimeTracker");
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "worktimetracker.log"), $"{DateTimeOffset.UtcNow:O} {error}{Environment.NewLine}");
            }
            return true;
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Trace.WriteLine(error);
            Trace.WriteLine(logError);
            return false;
        }
    }
}
