using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.App;

/// <summary>
/// Keeps a short event-time identity ledger, including windows absent from visibility samples.
/// A second spanning a rename, create, destroy, exclusion, or unknown metadata is rejected.
/// </summary>
public sealed class WindowsTrackingWindowResolver : ITrackingWindowResolver
{
    private sealed record ProcessIdentity(long HostCreation, uint TargetPid, long TargetCreation);
    private sealed record ProcessMetadata(long Creation, string Executable, string Name, string Friendly);
    private sealed record Version(DateTimeOffset Start, DateTimeOffset? End, WindowInfo Window, ProcessIdentity Birth);
    private readonly Dictionary<string, List<Version>> versions = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, string> handles = [];
    private readonly Dictionary<uint, ProcessMetadata> processes = [];
    private readonly HashSet<string> excluded;
    private readonly object gate = new();
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Native.EventProc callback;
    private uint threadId;
    private DateTimeOffset nextProcessPrune;
    private Exception? failure;
    private int disposed;

    public WindowsTrackingWindowResolver(IEnumerable<string> excludedApplications)
    {
        excluded = new(excludedApplications.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        callback = OnEvent;
        thread = new Thread(Run) { IsBackground = true, Name = "WorkTimeTracker window identity ledger" };
        thread.Start();
        try { ready.Task.GetAwaiter().GetResult(); }
        catch { thread.Join(); throw; }
    }

    public WindowInfo? ResolveSnapshot(WindowSnapshot snapshot)
    {
        lock (gate)
        {
            Check();
            if (!TryParse(snapshot.Window.Id, out _, out var hwnd)) return null;
            // Renames/destroys arrive through the WinEvent ledger, so an unchanged known window
            // needs no per-second process re-query.
            if (handles.TryGetValue(hwnd, out var known) && known == snapshot.Window.Id &&
                versions.TryGetValue(known, out var current) && current.Count > 0 &&
                current[^1] is { End: null } open && open.Start <= snapshot.CapturedUtc &&
                SameMetadata(open.Window, snapshot.Window))
                return open.Window;
            if (!Observe(hwnd, DateTimeOffset.UtcNow, false)) return null;
            if (!versions.TryGetValue(snapshot.Window.Id, out var history)) return null;
            var version = history.LastOrDefault(v => v.Start <= snapshot.CapturedUtc &&
                (v.End == null || v.End > snapshot.CapturedUtc));
            if (version == null || version != history[^1] || version.End != null ||
                !SameMetadata(version.Window, snapshot.Window)) return null;
            return version.Window;
        }
    }

    public WindowInfo? ResolveInteraction(InteractionSecond interaction)
    {
        lock (gate)
        {
            Check();
            if (!TryParse(interaction.WindowId, out _, out var hwnd)) return null;
            var known = handles.TryGetValue(hwnd, out var knownId) && knownId == interaction.WindowId &&
                versions.TryGetValue(knownId, out var open) && open.Count > 0 && open[^1].End == null;
            if (!known && (!Observe(hwnd, DateTimeOffset.UtcNow, false) ||
                !handles.TryGetValue(hwnd, out var currentId) || currentId != interaction.WindowId)) return null;
            if (!versions.TryGetValue(interaction.WindowId, out var history)) return null;
            var start = DateTimeOffset.FromUnixTimeSeconds(interaction.SecondUtc.ToUnixTimeSeconds());
            var end = start + TimeSpan.FromSeconds(1);
            var matches = history.Where(v => v.Start <= start && (v.End == null || v.End >= end)).ToArray();
            return matches.Length == 1 && history[^1].End == null && matches[0].Birth == history[^1].Birth
                ? matches[0].Window : null;
        }
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (failure != null) throw new InvalidOperationException("Window identity monitoring failed.", failure);
    }

    public void CheckHealth()
    {
        lock (gate) Check();
    }

    public DateTimeOffset? GetKnownEndUtc(string windowId)
    {
        lock (gate)
        {
            Check();
            return versions.Values.SelectMany(v => v).FirstOrDefault(v => v.Window.Id == windowId)?.End;
        }
    }

    private void Run()
    {
        nint hook = 0, nameHook = 0;
        try
        {
            threadId = Native.GetCurrentThreadId();
            Native.PeekMessage(out _, 0, 0, 0, 0);
            hook = Native.SetWinEventHook(0x8000, 0x8001, 0, callback, 0, 0, 2);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Separate narrow ranges: 0x8002-0x800B includes chatty cursor/caret/state events.
            nameHook = Native.SetWinEventHook(0x800C, 0x800C, 0, callback, 0, 0, 2);
            if (nameHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            lock (gate)
            {
                if (!Native.EnumWindows((hwnd, _) => { Observe(hwnd, DateTimeOffset.UtcNow, false); return true; }, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            ready.TrySetResult();
            int result;
            while ((result = Native.GetMessage(out var message, 0, 0, 0)) > 0)
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { lock (gate) failure ??= ex; ready.TrySetException(ex); }
        finally
        {
            if (nameHook != 0 && !Native.UnhookWinEvent(nameHook))
                lock (gate) failure ??= new Win32Exception(Marshal.GetLastWin32Error());
            if (hook != 0 && !Native.UnhookWinEvent(hook))
                lock (gate) failure ??= new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private void OnEvent(nint hook, uint kind, nint hwnd, int objectId, int childId, uint eventThread, uint eventTicks)
    {
        if (hwnd == 0 || objectId != 0 || childId != 0 || kind is not (0x8000 or 0x8001 or 0x800C)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = unchecked((uint)Environment.TickCount - eventTicks);
            var occurred = now - TimeSpan.FromMilliseconds(elapsed);
            lock (gate)
            {
                // Invalidate from the OS event time, but only trust freshly read metadata
                // from the callback time. Delayed callbacks must not backdate a new title.
                Close(hwnd, occurred);
                if (kind != 0x8001) Observe(hwnd, now, true);
                foreach (var key in versions.Keys.ToArray())
                {
                    versions[key].RemoveAll(v => v.End < now - TimeSpan.FromMinutes(1));
                    if (versions[key].Count == 0) versions.Remove(key);
                }
                if (now >= nextProcessPrune)
                {
                    var active = versions.Values.SelectMany(history => history)
                        .Where(version => version.End == null).Select(version => version.Birth.TargetPid).ToHashSet();
                    foreach (var pid in processes.Keys.Where(pid => !active.Contains(pid)).ToArray())
                        processes.Remove(pid);
                    nextProcessPrune = now + TimeSpan.FromMinutes(1);
                }
            }
        }
        catch (Exception ex)
        {
            lock (gate) failure ??= ex;
            Native.PostThreadMessage(threadId, 0x12, 0, 0);
        }
    }

    private void Close(nint hwnd, DateTimeOffset time)
    {
        if (!handles.Remove(hwnd, out var rawId) || !versions.TryGetValue(rawId, out var history) || history.Count == 0) return;
        if (history[^1].End == null)
            history[^1] = history[^1] with { End = time < history[^1].Start ? history[^1].Start : time };
    }

    private bool Observe(nint hwnd, DateTimeOffset now, bool force)
    {
        var metadata = Describe(hwnd);
        if (metadata == null) { Close(hwnd, now); return false; }
        var (raw, birth) = metadata.Value;
        if (handles.TryGetValue(hwnd, out var previousRaw) && previousRaw != raw.Id) Close(hwnd, now);
        if (!versions.TryGetValue(raw.Id, out var history)) versions[raw.Id] = history = [];
        if (!force && history.LastOrDefault() is { End: null } prior &&
            prior.Birth == birth && SameMetadata(prior.Window, raw))
            return true;
        var closeAt = now;
        if (history.LastOrDefault() is { End: null } old && old.Birth != birth)
        {
            var born = old.Birth.HostCreation != birth.HostCreation
                ? DateTimeOffset.FromFileTime(birth.HostCreation).ToUniversalTime()
                : old.Birth.TargetPid == birth.TargetPid && old.Birth.TargetCreation != birth.TargetCreation
                    ? DateTimeOffset.FromFileTime(birth.TargetCreation).ToUniversalTime() : now;
            if (born < closeAt) closeAt = born;
        }
        Close(hwnd, closeAt);
        var identity = raw with { Id = Guid.NewGuid().ToString("N") };
        history.Add(new(now, null, identity, birth));
        handles[hwnd] = raw.Id;
        return true;
    }

    private static bool SameMetadata(WindowInfo a, WindowInfo b) =>
        a.ProcessName == b.ProcessName && a.FriendlyName == b.FriendlyName &&
        a.ExecutablePath == b.ExecutablePath && a.Title == b.Title;

    private (WindowInfo Window, ProcessIdentity Birth)? Describe(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var hostPid);
        if (hostPid == 0 || hostPid == Environment.ProcessId || !Native.IsWindow(hwnd) ||
            Native.GetAncestor(hwnd, 2) != hwnd ||
            hwnd == Native.GetShellWindow() || hwnd == Native.GetDesktopWindow()) return null;
        var className = ClassName(hwnd);
        if (className is "" or "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
        Marshal.SetLastPInvokeError(0);
        var style = Native.GetWindowLong(hwnd, -20);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) return null;
        if ((style & (0x80 | 0x20)) != 0) return null;
        var pid = hostPid;
        nint coreHandle = 0;
        if (className == "ApplicationFrameWindow")
        {
            Native.EnumChildWindows(hwnd, (child, _) =>
            {
                if (ClassName(child) != "Windows.UI.Core.CoreWindow") return true;
                Native.GetWindowThreadProcessId(child, out pid);
                coreHandle = child;
                return false;
            }, 0);
            if (pid == hostPid) return null;
        }
        var hostBirth = ProcessBirth(hostPid);
        var process = Native.OpenProcess(0x1000, false, pid);
        if (process == 0 || hostBirth == null)
        {
            if (process != 0) Native.CloseHandle(process);
            return Unknown(hwnd, hostPid, pid, hostBirth);
        }
        try
        {
            if (!Native.GetProcessTimes(process, out var creation, out _, out _, out _)) return Unknown(hwnd, hostPid, pid, hostBirth);
            if (!processes.TryGetValue(pid, out var metadata) || metadata.Creation != creation)
            {
                processes.Remove(pid);
                var path = new StringBuilder(32768);
                var size = path.Capacity;
                if (!Native.QueryFullProcessImageName(process, 0, path, ref size)) return Unknown(hwnd, hostPid, pid, hostBirth);
                var executablePath = path.ToString();
                var processName = Path.GetFileNameWithoutExtension(executablePath);
                var friendlyName = processName;
                try
                {
                    var description = FileVersionInfo.GetVersionInfo(executablePath).FileDescription;
                    if (!string.IsNullOrWhiteSpace(description)) friendlyName = description.Trim();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException) { }
                metadata = new(creation, executablePath, processName, friendlyName);
                processes[pid] = metadata;
            }
            var (birth, executable, name, friendly) = metadata;
            if (excluded.Contains(Normalize(name)) || excluded.Contains(Normalize(executable)) ||
                excluded.Contains(Normalize(Path.GetFileName(executable)))) return null;
            var title = new StringBuilder(Math.Max(1, Native.GetWindowTextLength(hwnd) + 1));
            Native.GetWindowText(hwnd, title, title.Capacity);
            Native.GetWindowThreadProcessId(hwnd, out var afterPid);
            if (afterPid != hostPid || ProcessBirth(hostPid) != hostBirth || ProcessBirth(pid) != creation) return null;
            if (coreHandle != 0)
            {
                Native.GetWindowThreadProcessId(coreHandle, out var afterChildPid);
                if (afterChildPid != pid || Native.GetAncestor(coreHandle, 2) != hwnd) return null;
            }
            return (new($"{hostPid:X8}:{(ulong)(nuint)hwnd:X}", name, friendly, executable, title.ToString()),
                new(hostBirth.Value, pid, birth));
        }
        finally { Native.CloseHandle(process); }
    }

    private (WindowInfo Window, ProcessIdentity Birth)? Unknown(nint hwnd, uint hostPid, uint pid, long? hostBirth)
    {
        // Elevated/protected processes: metadata is unreadable, but the title usually is.
        if (excluded.Contains(UnknownProcessName)) return null;
        var title = new StringBuilder(Math.Max(1, Native.GetWindowTextLength(hwnd) + 1));
        Native.GetWindowText(hwnd, title, title.Capacity);
        Native.GetWindowThreadProcessId(hwnd, out var afterPid);
        if (afterPid != hostPid || !Native.IsWindow(hwnd)) return null;
        return (new($"{hostPid:X8}:{(ulong)(nuint)hwnd:X}", UnknownProcessName, UnknownFriendlyName, null, title.ToString()),
            new(hostBirth ?? 0, pid, 0));
    }

    internal const string UnknownProcessName = WorkTimeTracker.Windows.WindowsProcessNames.Unknown;
    internal const string UnknownFriendlyName = WorkTimeTracker.Windows.WindowsProcessNames.UnknownElevated;

    private static long? ProcessBirth(uint pid)
    {
        var process = Native.OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        try { return Native.GetProcessTimes(process, out var creation, out _, out _, out _) ? creation : null; }
        finally { Native.CloseHandle(process); }
    }

    private static string ClassName(nint hwnd)
    {
        var text = new StringBuilder(256);
        Native.GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }

    private static bool TryParse(string id, out uint pid, out nint hwnd)
    {
        pid = 0; hwnd = 0;
        var split = id.Split(':');
        if (split.Length != 2 || !uint.TryParse(split[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid) ||
            !ulong.TryParse(split[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;
        hwnd = (nint)(nuint)value;
        return true;
    }

    private static string Normalize(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (!Native.PostThreadMessage(threadId, 0x12, 0, 0) && thread.IsAlive)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        thread.Join();
        if (failure != null) throw new InvalidOperationException("Window identity monitor failed.", failure);
    }

    private static class Native
    {
        internal delegate bool EnumProc(nint hwnd, nint parameter);
        internal delegate void EventProc(nint hook, uint kind, nint hwnd, int objectId, int childId, uint thread, uint time);
        [StructLayout(LayoutKind.Sequential)] internal struct Message
        {
            internal nint Hwnd; internal uint Id; internal nuint WParam; internal nint LParam;
            internal uint Time; internal int X, Y; internal uint Private;
        }
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWinEventHook(uint min, uint max, nint module, EventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(nint hook);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool EnumWindows(EnumProc callback, nint parameter);
        [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint hwnd, EnumProc callback, nint parameter);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
        [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern nint GetShellWindow();
        [DllImport("user32.dll")] internal static extern nint GetDesktopWindow();
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] internal static extern int GetWindowLong(nint hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(nint hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int length);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder text, int length);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
        [DllImport("kernel32.dll")] internal static extern bool GetProcessTimes(nint handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageName(nint handle, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] internal static extern bool PeekMessage(out Message message, nint hwnd, uint min, uint max, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostThreadMessage(uint thread, uint message, nuint wparam, nint lparam);
    }
}
