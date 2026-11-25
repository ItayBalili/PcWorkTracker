using System.ComponentModel;
using System.Runtime.InteropServices;
using WorkTimeTracker.Core.Abstractions;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Windows;

/// <summary>Captures eligible top-level windows in front-to-back Z order.</summary>
public sealed class WindowsWindowSnapshotProvider : IWindowSnapshotProvider
{
    private readonly WindowIdentity _identity = new();
    private readonly object _gate = new();

    public ValueTask<IReadOnlyList<WindowSnapshot>> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var oldDpi = Native.SetThreadDpiAwarenessContext((nint)(-4));
            if (oldDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return ValueTask.FromResult(Capture(cancellationToken)); }
            finally { Native.SetThreadDpiAwarenessContext(oldDpi); }
        }
    }

    private IReadOnlyList<WindowSnapshot> Capture(CancellationToken cancellationToken)
    {
        var monitors = new List<Native.Rect>();
        if (!Native.EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Native.Rect rect, nint data) =>
        {
            monitors.Add(rect);
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());

        var windows = new List<(nint Handle, Native.Rect Bounds)>();
        if (!Native.EnumWindows((hwnd, parameter) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || Native.GetWindow(hwnd, Native.GwOwner) != 0)
                return true;
            if (WindowFilter.IsExcluded(hwnd, out _)) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return true;
            if (Native.DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) != 0 || cloaked != 0)
                return true;
            if (Native.DwmGetWindowAttribute(hwnd, 9, out Native.Rect rect, Marshal.SizeOf<Native.Rect>()) != 0 &&
                !Native.GetWindowRect(hwnd, out rect)) return true;
            if (rect.Area > 0) windows.Add((hwnd, rect));
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());

        var now = DateTimeOffset.UtcNow;
        var foreground = Native.GetAncestor(Native.GetForegroundWindow(), Native.GaRoot);
        var result = new List<WindowSnapshot>(windows.Count);
        var covered = new List<Native.Rect>();
        foreach (var window in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var percent = VisiblePercent(window.Bounds, monitors, covered);
            result.Add(new WindowSnapshot(_identity.Describe(window.Handle), now, percent, window.Handle == foreground));
            covered.Add(window.Bounds);
        }
        _identity.Prune(windows.Select(w => w.Handle).ToHashSet());
        return result;
    }

    internal static decimal VisiblePercent(Native.Rect bounds, IEnumerable<Native.Rect> monitors, IEnumerable<Native.Rect> covered)
    {
        var onScreen = new List<Native.Rect>();
        foreach (var monitor in monitors)
        {
            var part = bounds.Intersect(monitor);
            var pieces = part.Area > 0 ? new List<Native.Rect> { part } : new List<Native.Rect>();
            foreach (var existing in onScreen) pieces = Subtract(pieces, existing);
            onScreen.AddRange(pieces);
        }
        foreach (var occluder in covered) onScreen = Subtract(onScreen, occluder);
        return decimal.Round(100m * onScreen.Sum(r => r.Area) / bounds.Area, 1, MidpointRounding.AwayFromZero);
    }

    private static List<Native.Rect> Subtract(List<Native.Rect> source, Native.Rect cut)
    {
        var result = new List<Native.Rect>();
        foreach (var rect in source)
        {
            var intersection = rect.Intersect(cut);
            if (intersection.Area == 0) { result.Add(rect); continue; }
            Add(rect.Left, rect.Top, rect.Right, intersection.Top);
            Add(rect.Left, intersection.Bottom, rect.Right, rect.Bottom);
            Add(rect.Left, intersection.Top, intersection.Left, intersection.Bottom);
            Add(intersection.Right, intersection.Top, rect.Right, intersection.Bottom);
        }
        return result;

        void Add(int left, int top, int right, int bottom)
        {
            if (right > left && bottom > top) result.Add(new Native.Rect { Left = left, Top = top, Right = right, Bottom = bottom });
        }
    }
}
