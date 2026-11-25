using WorkTimeTracker.Windows;

static Native.Rect R(int left, int top, int right, int bottom) =>
    new() { Left = left, Top = top, Right = right, Bottom = bottom };

static void Check(decimal expected, Native.Rect bounds, Native.Rect[] monitors, params Native.Rect[] covered)
{
    var actual = WindowsWindowSnapshotProvider.VisiblePercent(bounds, monitors, covered);
    if (actual != expected) throw new Exception($"Expected {expected}, got {actual}");
}

Check(100m, R(0, 0, 100, 100), [R(0, 0, 75, 100), R(25, 0, 100, 100)]);
Check(25m, R(0, 0, 100, 100), [R(0, 0, 100, 100)], R(0, 0, 50, 100), R(25, 0, 75, 100));
Check(50m, R(-50, 0, 50, 100), [R(0, 0, 100, 100)]);
Check(0m, R(0, 0, 100, 100), [R(200, 0, 300, 100)]);
Check(33.3m, R(0, 0, 3, 10), [R(0, 0, 1, 10)]);
Check(0.1m, R(0, 0, 2000, 1), [R(0, 0, 1, 1)]);
Console.WriteLine("Visibility geometry checks passed.");

if (!WindowFilter.IsShellClass("Shell_SecondaryTrayWnd") || !WindowFilter.IsShellClass("Progman") ||
    WindowFilter.IsShellClass("ApplicationFrameWindow"))
    throw new Exception("Shell window filtering failed.");
if (!WindowFilter.IsFullyTransparent(0, 2) || WindowFilter.IsFullyTransparent(0, 1) ||
    WindowFilter.IsFullyTransparent(255, 2))
    throw new Exception("Layered alpha filtering failed.");
Console.WriteLine("Shell and transparency checks passed.");
