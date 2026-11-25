using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using WorkTimeTracker.Core.Abstractions;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Windows;

/// <summary>Collects interaction categories, never key codes or typed content.</summary>
public sealed class WindowsInputMonitor : IInputMonitor, IDisposable
{
    private readonly record struct Input(string WindowId, DateTimeOffset Time, InteractionKind Kind);
    private readonly Channel<Input> _input = Channel.CreateBounded<Input>(new BoundedChannelOptions(4096)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<InteractionSecond> _output = Channel.CreateBounded<InteractionSecond>(new BoundedChannelOptions(4096)
        { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly Task _aggregation;
    private uint _threadId;
    private int _disposed, _reading;
    private Exception? _callbackError;
    private Exception? _threadError;
    private Exception? _aggregationError;
    private string? _lastWindow;
    private long _lastSecond;
    private InteractionKind _lastKinds;

    public WindowsInputMonitor()
    {
        _aggregation = AggregateAsync();
        _thread = new Thread(HookThread) { IsBackground = true, Name = "WorkTimeTracker input hooks" };
        _thread.Start();
        try { _started.Task.GetAwaiter().GetResult(); }
        catch { _thread.Join(); _aggregation.GetAwaiter().GetResult(); throw; }
    }

    public async IAsyncEnumerable<InteractionSecond> ReadInteractionsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Only one interaction reader is supported.");
        await foreach (var item in _output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
    }

    private void HookThread()
    {
        nint keyboard = 0, mouse = 0;
        Exception? error = null;
        Native.HookProc keyboardCallback = KeyboardCallback, mouseCallback = MouseCallback;
        try
        {
            _threadId = Native.GetCurrentThreadId();
            if (Native.SetThreadDpiAwarenessContext((nint)(-4)) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.PeekMessage(out _, 0, 0, 0, 0);
            keyboard = Native.SetWindowsHookEx(Native.WhKeyboardLl, keyboardCallback, 0, 0);
            if (keyboard == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            mouse = Native.SetWindowsHookEx(Native.WhMouseLl, mouseCallback, 0, 0);
            if (mouse == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _started.TrySetResult();
            int status;
            while ((status = Native.GetMessage(out var msg, 0, 0, 0)) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            if (status < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (_callbackError != null) throw _callbackError;
        }
        catch (Exception ex) { error = ex; _started.TrySetException(ex); }
        finally
        {
            if (mouse != 0 && !Native.UnhookWindowsHookEx(mouse)) error ??= new Win32Exception(Marshal.GetLastWin32Error());
            if (keyboard != 0 && !Native.UnhookWindowsHookEx(keyboard)) error ??= new Win32Exception(Marshal.GetLastWin32Error());
            GC.KeepAlive(keyboardCallback);
            GC.KeepAlive(mouseCallback);
            _threadError = error;
            _input.Writer.TryComplete(error);
        }
    }

    private nint KeyboardCallback(int code, nuint message, nint data)
    {
        if (code >= 0 && (message == Native.WmKeyDown || message == Native.WmSysKeyDown))
            Record(Native.GetForegroundWindow(), InteractionKind.Keyboard);
        return Native.CallNextHookEx(0, code, message, data);
    }

    private nint MouseCallback(int code, nuint message, nint data)
    {
        if (code >= 0)
        {
            var kind = message switch
            {
                Native.WmMouseMove => InteractionKind.Hover,
                Native.WmLbuttonDown or Native.WmRbuttonDown or Native.WmMbuttonDown or Native.WmXbuttonDown => InteractionKind.Click,
                Native.WmMouseWheel or Native.WmMouseHWheel => InteractionKind.Scroll,
                _ => InteractionKind.None
            };
            // Mouse moves can arrive at hundreds of Hz; hover is a per-second flag, so resolving
            // the window under the cursor 10 times a second is ample and keeps the hook cheap.
            if (kind == InteractionKind.Hover)
            {
                var now = Environment.TickCount64;
                if (now - _lastHoverTick < HoverSampleMilliseconds) kind = InteractionKind.None;
                else _lastHoverTick = now;
            }
            if (kind != InteractionKind.None)
                Record(Native.WindowFromPoint(Marshal.PtrToStructure<Native.MouseHook>(data).Point), kind);
        }
        return Native.CallNextHookEx(0, code, message, data);
    }

    private const long HoverSampleMilliseconds = 100;
    private long _lastHoverTick = long.MinValue / 2;
    private nint _lastHandle;

    private void Record(nint target, InteractionKind kind)
    {
        try
        {
            var time = DateTimeOffset.UtcNow;
            var hwnd = Native.GetAncestor(target, Native.GaRoot);
            if (hwnd == 0) return;
            var second = time.ToUnixTimeSeconds();
            if (_lastHandle == hwnd && _lastSecond == second && (_lastKinds & kind) == kind) return;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return;
            var id = $"{pid:X8}:{(ulong)(nuint)hwnd:X}";
            if (_lastWindow == id && _lastSecond == second && (_lastKinds & kind) == kind) return;
            if (!_input.Writer.TryWrite(new Input(id, time, kind)))
                throw new InvalidOperationException("Input queue capacity exceeded; collection stopped rather than silently dropping events.");
            _lastKinds = _lastWindow == id && _lastSecond == second ? _lastKinds | kind : kind;
            _lastWindow = id;
            _lastHandle = hwnd;
            _lastSecond = second;
        }
        catch (Exception ex)
        {
            _callbackError = ex;
            Native.PostThreadMessage(_threadId, Native.WmQuit, 0, 0);
        }
    }

    private async Task AggregateAsync()
    {
        var pending = new Dictionary<(string Id, long Second), InteractionKind>();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000));
        var tick = timer.WaitForNextTickAsync().AsTask();
        var read = _input.Reader.WaitToReadAsync().AsTask();
        Exception? error = null;
        try
        {
            while (true)
            {
                var completed = await Task.WhenAny(read, tick).ConfigureAwait(false);
                if (completed == read)
                {
                    if (!await read.ConfigureAwait(false)) break;
                    var batchCount = 0;
                    while (batchCount++ < 4096 && _input.Reader.TryRead(out var item))
                    {
                        var second = item.Time.ToUnixTimeSeconds();
                        var key = (item.WindowId, second);
                        pending.TryGetValue(key, out var previous);
                        if (pending.Count >= 8192 && previous == InteractionKind.None)
                            throw new InvalidOperationException("Pending interaction capacity exceeded; collection stopped.");
                        pending[key] = previous | item.Kind;
                    }
                    read = _input.Reader.WaitToReadAsync().AsTask();
                }
                if (tick.IsCompleted)
                {
                    await tick.ConfigureAwait(false);
                    if (!Flush(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1))
                        throw new InvalidOperationException("Interaction output capacity exceeded; consume the stream continuously.");
                    tick = timer.WaitForNextTickAsync().AsTask();
                }
            }
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            if (!Flush(long.MaxValue)) error ??= new InvalidOperationException("Interaction output capacity exceeded while flushing.");
            _aggregationError = error;
            if (error != null) Native.PostThreadMessage(_threadId, Native.WmQuit, 0, 0);
            _output.Writer.TryComplete(error);
        }
        bool Flush(long before)
        {
            foreach (var pair in pending.Where(p => p.Key.Second < before).ToArray())
            {
                if (!_output.Writer.TryWrite(new InteractionSecond(pair.Key.Id, DateTimeOffset.FromUnixTimeSeconds(pair.Key.Second), pair.Value)))
                    return false;
                pending.Remove(pair.Key);
            }
            return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!Native.PostThreadMessage(_threadId, Native.WmQuit, 0, 0) && _thread.IsAlive)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _thread.Join();
        _aggregation.GetAwaiter().GetResult();
        if (_threadError != null) throw new InvalidOperationException("The input hook thread failed.", _threadError);
        if (_aggregationError != null) throw new InvalidOperationException("Input aggregation failed.", _aggregationError);
    }
}
