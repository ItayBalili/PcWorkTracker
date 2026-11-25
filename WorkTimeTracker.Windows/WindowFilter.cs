using System.Runtime.InteropServices;
using System.Text;

namespace WorkTimeTracker.Windows;

internal static class WindowFilter
{
    internal static bool IsExcluded(nint hwnd, out int style)
    {
        style = 0;
        if (hwnd == Native.GetDesktopWindow() || hwnd == Native.GetShellWindow()) return true;
        var name = new StringBuilder(256);
        if (Native.GetClassName(hwnd, name, name.Capacity) == 0) return true;
        if (IsShellClass(name.ToString())) return true;
        Marshal.SetLastPInvokeError(0);
        style = Native.GetWindowLong(hwnd, Native.GwlExstyle);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) return true;
        if ((style & (Native.WsExToolwindow | Native.WsExTransparent)) != 0) return true;
        if ((style & Native.WsExLayered) != 0)
        {
            if (Native.GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags))
                return IsFullyTransparent(alpha, flags);
            // UpdateLayeredWindow per-pixel surfaces don't expose constant alpha.
            // Keep unknown surfaces unless they vanished; do not infer alpha=0 on failure.
            return !Native.IsWindow(hwnd);
        }
        return false;
    }

    internal static bool IsShellClass(string name) =>
        name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    internal static bool IsFullyTransparent(byte alpha, uint flags) => (flags & 2) != 0 && alpha == 0;
}
