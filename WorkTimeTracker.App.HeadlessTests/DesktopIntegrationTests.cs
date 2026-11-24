using System.Diagnostics;
using System.Runtime.InteropServices;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Data;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace WorkTimeTracker.App.HeadlessTests;

public sealed class DesktopIntegrationTests(ITestOutputHelper output)
{
    [DesktopFact]
    public async Task RealCollectorsPersistSnapshotsHoverTitleVersionsAndUnsampledWindowInput()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "desktop.sqlite3");
        using var fixture = StartChild("--fixture");
        Native.GetCursorPos(out var cursor);
        try
        {
            var ready = await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith("READY ", ready);
            var handles = ready!.Split(' ');
            var mainHandle = nint.Parse(handles[1]);
            var ownedHandle = nint.Parse(handles[2]);
            await using var tracker = new TrackingService(new() { DatabasePath = path });
            await tracker.StartAsync();
            await Task.Delay(1500);
            await Hover(ownedHandle);
            fixture.StandardInput.WriteLine("rename");
            fixture.StandardInput.Flush();
            await Task.Delay(1500);
            await Hover(mainHandle);
            await Task.Delay(2500);
            await tracker.StopAsync();
            using var repository = new SqliteWorkTimeRepository(path);
            var raw = repository.GetRawData(repository.ListWorkDays().Single().Id);
            output.WriteLine($"Captured windows={raw.Windows.Count}, input={raw.Interactions.Count}, rejected={tracker.RejectedInteractions}; fixture rows={raw.Windows.Count(w => w.Title.StartsWith("WorkTimeTracker"))}.");
            var owned = Assert.Single(raw.Windows, w => w.Title == "WorkTimeTracker unsampled owned fixture");
            Assert.Contains(raw.Interactions, i => i.WindowId == owned.Id && i.Kind.HasFlag(InteractionKind.Hover));
            Assert.DoesNotContain(raw.VisibilitySegments, s => s.WindowId == owned.Id);
            var renamed = Assert.Single(raw.Windows, w => w.Title == "WorkTimeTracker renamed fixture");
            var original = Assert.Single(raw.Windows, w => w.Title == "WorkTimeTracker disposable fixture");
            Assert.NotEqual(original.Id, renamed.Id);
            Assert.Contains(raw.Interactions, i => i.WindowId == renamed.Id);
            Assert.All(raw.Interactions, i => Assert.Contains(raw.Windows, w => w.Id == i.WindowId));
            Assert.NotEmpty(raw.VisibilitySegments);
            Assert.Contains(raw.SessionEvents, e => e.Kind == SessionEventKind.AppStop);
            output.WriteLine($"Real desktop: windows={raw.Windows.Count}, segments={raw.VisibilitySegments.Count}, interactions={raw.Interactions.Count}, sessions={raw.SessionEvents.Count}.");
            output.WriteLine($"Observation UTC range {raw.VisibilitySegments.Min(s => s.StartUtc):O} .. {raw.VisibilitySegments.Max(s => s.EndUtc):O}; committed {repository.GetRecoveryState().LastFlushedUtc:O}.");
            await using var excludedTracker = new TrackingService(new()
            {
                DatabasePath = Path.Combine(directory, "excluded.sqlite3"),
                Settings = new() { ExcludedApplications = new HashSet<string> { "DOTNET.EXE" } }
            });
            await excludedTracker.StartAsync();
            await Task.Delay(1500);
            await Hover(ownedHandle);
            await Task.Delay(2500);
            await excludedTracker.StopAsync();
            using var excludedRepository = new SqliteWorkTimeRepository(Path.Combine(directory, "excluded.sqlite3"));
            var excludedRaw = excludedRepository.GetRawData(excludedRepository.ListWorkDays().Single().Id);
            Assert.DoesNotContain(excludedRaw.Windows, w => w.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase));
            Assert.True(excludedTracker.RejectedInteractions > 0);
            Assert.Contains(excludedRaw.GlobalInputs, i => i.ObservedUtc - i.LastInputUtc < TimeSpan.FromSeconds(1));
            Assert.Empty(excludedRaw.Interactions);
            output.WriteLine($"Excluded live fixture: no dotnet metadata/visibility/input rows; rejected interaction seconds={excludedTracker.RejectedInteractions}.");
            output.WriteLine($"Real GetLastInputInfo cross-check persisted {excludedRaw.GlobalInputs.Count} identity-free idle observations, including input over the excluded fixture.");
        }
        finally
        {
            Native.SetCursorPos(cursor.X, cursor.Y);
            if (!fixture.HasExited)
            {
                fixture.StandardInput.WriteLine("exit");
                fixture.StandardInput.Flush();
                try { await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { fixture.Kill(entireProcessTree: true); await fixture.WaitForExitAsync(); }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [DesktopTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbruptProcessExitKeepsCommittedRowsWithoutInventingAnEndOrRestartBridge(bool paused)
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "crash.sqlite3");
        using var child = StartChild(paused ? "--crash-record-paused" : "--crash-record", path);
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(73, child.ExitCode);
            WorkDayRawData before;
            long id;
            DateTimeOffset watermark;
            using (var repository = new SqliteWorkTimeRepository(path))
            {
                id = repository.ListWorkDays().Single().Id;
                before = repository.GetRawData(id);
                Assert.NotEmpty(before.VisibilitySegments);
                Assert.Null(before.WorkDay.EndUtc);
                Assert.DoesNotContain(before.SessionEvents, e => e.Kind == SessionEventKind.AppStop);
            }
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT LastFlushedUtc FROM TrackingRuns ORDER BY Id DESC LIMIT 1;";
                watermark = new((long)command.ExecuteScalar()!, TimeSpan.Zero);
            }
            Assert.All(before.VisibilitySegments, s => Assert.True(s.EndUtc <= watermark));
            Assert.All(before.GlobalInputs, i => Assert.True(i.ObservedUtc <= watermark));
            var stoppedThrough = before.VisibilitySegments.Max(s => s.EndUtc);
            await Task.Delay(1500);
            var restarted = DateTimeOffset.UtcNow;
            await using (var tracker = new TrackingService(new() { DatabasePath = path }))
            {
                await tracker.StartAsync();
                Assert.Equal(id, tracker.Status.WorkDayId);
                var recoveredRaw = await tracker.GetRawDataAsync();
                var recovery = Assert.Single(recoveredRaw.TrackingEvents, e => e.Kind == TrackingEventKind.Recovery);
                Assert.Equal(watermark, recovery.TimeUtc);
                Assert.Equal(recoveredRaw.SessionEvents.Last(e => e.Kind == SessionEventKind.AppStart).TimeUtc, recovery.EndUtc);
                if (paused)
                {
                    Assert.Equal(TrackingState.Paused, tracker.Status.State);
                    var report = CalculationEngine.Calculate(recoveredRaw, new(), DateTimeOffset.UtcNow);
                    Assert.True(report.NotTrackedTime >= DateTimeOffset.UtcNow - watermark - TimeSpan.FromSeconds(1));
                    await tracker.ResumeAsync();
                }
                await Task.Delay(2300);
                await tracker.StopAsync();
            }
            using var recovered = new SqliteWorkTimeRepository(path);
            var after = TrackingRawDataReader.GetRawData(path, id);
            Assert.True(after.VisibilitySegments.Count >= before.VisibilitySegments.Count);
            Assert.DoesNotContain(after.VisibilitySegments, s => s.StartUtc < restarted && s.EndUtc > stoppedThrough);
            var boundary = Assert.Single(after.TrackingEvents, e => e.Kind == TrackingEventKind.Recovery);
            Assert.DoesNotContain(after.VisibilitySegments, s => s.StartUtc < boundary.EndUtc && s.EndUtc > boundary.TimeUtc);
            Assert.Contains(TrackingAvailability.GetNotTrackedIntervals(after, DateTimeOffset.UtcNow),
                i => i.StartUtc <= boundary.TimeUtc && i.EndUtc >= boundary.EndUtc);
            output.WriteLine($"Abrupt child exit=73; paused={paused}; pre-crash segments={before.VisibilitySegments.Count}; recovered segments={after.VisibilitySegments.Count}; no invented day end.");
            output.WriteLine($"Recovery untracked range is exactly last durable flush {watermark:O} .. AppStart {boundary.EndUtc:O}.");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Environment.CurrentDirectory, "artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Process StartChild(params string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory
        };
        info.ArgumentList.Add(typeof(DesktopIntegrationTests).Assembly.Location);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }

    private static async Task Hover(nint hwnd)
    {
        var old = Native.SetThreadDpiAwarenessContext((nint)(-4));
        Native.GetWindowRect(hwnd, out var rect);
        Native.SetThreadDpiAwarenessContext(old);
        var x = (rect.Left + rect.Right) / 2;
        var y = (rect.Top + rect.Bottom) / 2;
        for (var index = 0; index < 30; index++)
        {
            var input = new Native.Input
            {
                Mouse = new()
                {
                    X = (int)((long)(x + index % 2 - Native.GetSystemMetrics(76)) * 65535 / (Native.GetSystemMetrics(78) - 1)),
                    Y = (int)((long)(y - Native.GetSystemMetrics(77)) * 65535 / (Native.GetSystemMetrics(79) - 1)),
                    Flags = 0xC001
                }
            };
            if (Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) != 1)
                throw new InvalidOperationException("Cannot send a hover-only mouse event on this desktop.");
            await Task.Delay(100);
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Input { internal uint Type; internal MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
        {
            internal int X, Y;
            internal uint Data, Flags, Time;
            internal nuint ExtraInfo;
        }
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] internal static extern uint SendInput(uint count, Input[] input, int size);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    }
}

public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WORKTIMETRACKER_LIVE_TESTS") != "1")
            Skip = "Opt in on an interactive desktop with WORKTIMETRACKER_LIVE_TESTS=1. Moves/restores the cursor over a disposable fixture.";
    }
}

public sealed class DesktopTheoryAttribute : TheoryAttribute
{
    public DesktopTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("WORKTIMETRACKER_LIVE_TESTS") != "1")
            Skip = "Opt in on an interactive desktop with WORKTIMETRACKER_LIVE_TESTS=1.";
    }
}
