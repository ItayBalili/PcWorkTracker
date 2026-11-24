using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Windows;

/// <summary>Labels used when a window's process image cannot be read (elevated/protected).</summary>
public static class WindowsProcessNames
{
    public const string Unknown = "Unknown";
    public const string UnknownElevated = "Unknown (elevated)";
}

internal sealed class WindowIdentity
{
    private readonly Dictionary<nint, (uint HostPid, nint Child, uint ChildPid)> _uwp = new();
    private sealed record ProcessMetadata(long Creation, string Name, string FriendlyName, string? Path);
    private readonly Dictionary<uint, ProcessMetadata> _processes = new();
    private readonly HashSet<uint> _usedProcesses = new();

    internal static string Id(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return $"{pid:X8}:{(ulong)(nuint)hwnd:X}";
    }

    internal WindowInfo Describe(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var hostPid);
        var pid = hostPid;
        if (ClassName(hwnd) == "ApplicationFrameWindow")
        {
            if (!_uwp.TryGetValue(hwnd, out var cached) || cached.HostPid != hostPid ||
                !Native.IsWindow(cached.Child) || Native.GetAncestor(cached.Child, Native.GaRoot) != hwnd ||
                Native.GetWindowThreadProcessId(cached.Child, out var currentPid) == 0 || currentPid != cached.ChildPid)
            {
                nint child = 0;
                uint childPid = 0;
                Native.EnumChildWindows(hwnd, (candidate, _) =>
                {
                    if (ClassName(candidate) == "Windows.UI.Core.CoreWindow")
                    {
                        Native.GetWindowThreadProcessId(candidate, out childPid);
                        child = candidate;
                        return false;
                    }
                    return true;
                }, 0);
                cached = (hostPid, child, childPid);
                if (child != 0) _uwp[hwnd] = cached;
                else _uwp.Remove(hwnd);
            }
            if (cached.Child != 0 && cached.ChildPid != 0) pid = cached.ChildPid;
        }
        var metadata = ProcessDetails(pid);
        var title = new StringBuilder(Math.Max(1, Native.GetWindowTextLength(hwnd) + 1));
        Native.GetWindowText(hwnd, title, title.Capacity);
        return new WindowInfo(Id(hwnd), metadata.Name, metadata.FriendlyName, metadata.Path, title.ToString());
    }

    internal void Prune(ISet<nint> live)
    {
        foreach (var hwnd in _uwp.Keys.Where(h => !live.Contains(h)).ToArray()) _uwp.Remove(hwnd);
        foreach (var pid in _processes.Keys.Where(pid => !_usedProcesses.Contains(pid)).ToArray()) _processes.Remove(pid);
        _usedProcesses.Clear();
    }

    private static string ClassName(nint hwnd)
    {
        var name = new StringBuilder(256);
        Native.GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    private ProcessMetadata ProcessDetails(uint pid)
    {
        _usedProcesses.Add(pid);
        string? path = null;
        long creation = 0;
        var canCache = false;
        var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (process != 0)
        {
            try
            {
                canCache = Native.GetProcessTimes(process, out creation, out _, out _, out _);
                if (canCache && _processes.TryGetValue(pid, out var cached) && cached.Creation == creation)
                    return cached;
                _processes.Remove(pid);
                var buffer = new StringBuilder(32768);
                var length = buffer.Capacity;
                if (Native.QueryFullProcessImageName(process, 0, buffer, ref length)) path = buffer.ToString();
            }
            finally { Native.CloseHandle(process); }
        }
        else _processes.Remove(pid);
        // Elevated/protected processes whose image cannot be queried are still recorded
        // (title comes from GetWindowText) under one stable "Unknown (elevated)" app.
        if (string.IsNullOrEmpty(path))
            return new ProcessMetadata(creation, WindowsProcessNames.Unknown, WindowsProcessNames.UnknownElevated, null);
        var name = Path.GetFileNameWithoutExtension(path);
        var friendlyName = name;
        if (path != null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) friendlyName = description.Trim();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException)
            {
                // Missing/inaccessible version resources fall back to the process name.
            }
        }
        var metadata = new ProcessMetadata(creation, name, friendlyName, path);
        // Never reuse metadata unless process creation time was read from the same
        // handle as the image path. PID-only caches can attribute a replacement process.
        if (canCache && path != null) _processes[pid] = metadata;
        return metadata;
    }
}
