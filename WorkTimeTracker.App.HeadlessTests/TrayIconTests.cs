using System.Drawing;
using System.Reflection;

namespace WorkTimeTracker.App.HeadlessTests;

public sealed class TrayIconTests
{
    [Fact]
    public void TrayVariantsUseTaskbarContrastAndShowTrackingState()
    {
        var method = typeof(TrackingService).Assembly.GetType("WorkTimeTracker.App.App", true)!
            .GetMethod("CreateTrayIconForTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var dark = (Icon)method.Invoke(null, [TrackingState.Tracking, false])!;
        using var light = (Icon)method.Invoke(null, [TrackingState.Tracking, true])!;
        using var paused = (Icon)method.Invoke(null, [TrackingState.Paused, false])!;
        using var stopped = (Icon)method.Invoke(null, [TrackingState.Faulted, false])!;
        using var darkBitmap = dark.ToBitmap();
        using var lightBitmap = light.ToBitmap();
        using var pausedBitmap = paused.ToBitmap();
        using var stoppedBitmap = stopped.ToBitmap();
        var pixels = from y in Enumerable.Range(0, 32)
                     from x in Enumerable.Range(0, 32)
                     select (x, y);
        Assert.Contains(pixels, p => darkBitmap.GetPixel(p.x, p.y).R > 200 && lightBitmap.GetPixel(p.x, p.y).R < 50);
        Assert.Contains(pixels, p => darkBitmap.GetPixel(p.x, p.y).ToArgb() != pausedBitmap.GetPixel(p.x, p.y).ToArgb());
        Assert.Contains(pixels, p => { var c = stoppedBitmap.GetPixel(p.x, p.y); return c.A > 0 && c.R > 200 && c.G < 50 && c.B < 50; });
        Assert.DoesNotContain(pixels, p => { var c = darkBitmap.GetPixel(p.x, p.y); return c.A > 0 && c.R > 200 && c.G < 50 && c.B < 50; });
    }
}
