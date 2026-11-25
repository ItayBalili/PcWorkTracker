using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WorkTimeTracker.Windows;

/// <summary>Provides the system-wide last-input idle cross-check.</summary>
public static class WindowsIdleTime
{
    public static TimeSpan GetIdleDuration()
    {
        var info = new Native.LastInputInfo { Size = (uint)Marshal.SizeOf<Native.LastInputInfo>() };
        if (!Native.GetLastInputInfo(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return TimeSpan.FromMilliseconds(unchecked((uint)Native.GetTickCount64() - info.Time));
    }
}
