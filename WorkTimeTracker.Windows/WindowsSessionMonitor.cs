using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32;
using WorkTimeTracker.Core.Abstractions;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Windows;

/// <summary>Reports events for the current interactive Windows session.</summary>
public sealed class WindowsSessionMonitor : ISessionMonitor, IDisposable
{
    private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private uint _threadId;
    private int _disposed, _reading;
    private Exception? _threadError;

    public WindowsSessionMonitor()
    {
        _thread = new Thread(MessageThread) { IsBackground = true, Name = "WorkTimeTracker session monitor" };
        _thread.Start();
        try { _started.Task.GetAwaiter().GetResult(); }
        catch { _thread.Join(); throw; }
    }

    public async IAsyncEnumerable<SessionEvent> ReadSessionEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Only one session event reader is supported.");
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
    }

    private void MessageThread()
    {
        NotificationWindow? window = null;
        var registered = false;
        var powerSubscribed = false;
        var endingSubscribed = false;
        Exception? error = null;
        try
        {
            _threadId = Native.GetCurrentThreadId();
            Native.PeekMessage(out _, 0, 0, 0, 0);
            window = new NotificationWindow(Emit);
            if (!Native.WTSRegisterSessionNotification(window.Handle, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            registered = true;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            powerSubscribed = true;
            SystemEvents.SessionEnding += OnSessionEnding;
            endingSubscribed = true;
            Emit(SessionEventKind.AppStart);
            _started.TrySetResult();
            int status;
            while ((status = Native.GetMessage(out var msg, 0, 0, 0)) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            if (status < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { error = ex; _started.TrySetException(ex); }
        finally
        {
            if (endingSubscribed) SystemEvents.SessionEnding -= OnSessionEnding;
            if (powerSubscribed) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            if (registered && !Native.WTSUnRegisterSessionNotification(window!.Handle))
                error ??= new Win32Exception(Marshal.GetLastWin32Error());
            window?.DestroyHandle();
            if (_started.Task.IsCompletedSuccessfully) Emit(SessionEventKind.AppStop);
            _threadError = error;
            _events.Writer.TryComplete(error);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Emit(SessionEventKind.Suspend);
        else if (e.Mode == PowerModes.Resume) Emit(SessionEventKind.Resume);
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        if (e.Reason == SessionEndReasons.SystemShutdown) Emit(SessionEventKind.Shutdown);
        else if (e.Reason == SessionEndReasons.Logoff) Emit(SessionEventKind.Logoff);
    }

    private void Emit(SessionEventKind kind) => _events.Writer.TryWrite(new SessionEvent(DateTimeOffset.UtcNow, kind));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!Native.PostThreadMessage(_threadId, Native.WmQuit, 0, 0) && _thread.IsAlive)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _thread.Join();
        if (_threadError != null) throw new InvalidOperationException("The session notification thread failed.", _threadError);
    }

    private sealed class NotificationWindow : System.Windows.Forms.NativeWindow
    {
        private readonly Action<SessionEventKind> _emit;
        internal NotificationWindow(Action<SessionEventKind> emit)
        {
            _emit = emit;
            CreateHandle(new System.Windows.Forms.CreateParams { Caption = "WorkTimeTracker notifications" });
        }
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == Native.WmWtsSessionChange)
            {
                var kind = (int)m.WParam switch
                {
                    5 => SessionEventKind.Logon,
                    6 => SessionEventKind.Logoff,
                    7 => SessionEventKind.Lock,
                    8 => SessionEventKind.Unlock,
                    _ => (SessionEventKind?)null
                };
                if (kind.HasValue) _emit(kind.Value);
            }
            base.WndProc(ref m);
        }
    }
}
