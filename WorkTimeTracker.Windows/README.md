# Windows collection

This project targets Windows 10/11 and .NET 8. It references only Core and does not
start sampling, persist data, or contact the network. The host chooses sampling
frequency and owns lifetime and storage.

## Integration API

- `new WindowsWindowSnapshotProvider()` implements `IWindowSnapshotProvider`.
  Await `CaptureAsync(cancellationToken)` for a front-to-back snapshot. Each
  result includes a stable-for-the-window-lifetime `Window.Id`, UTC capture
  time, foreground state, and visible percent rounded to 0.1 percentage point.
- `new WindowsInputMonitor()` implements `IInputMonitor` and `IDisposable`.
  Construction installs global low-level hooks on a dedicated message thread.
  Consume `ReadInteractionsAsync(cancellationToken)` continuously with one
  reader; disposing stops hooks, flushes pending seconds and completes the stream.
  Canceling enumeration does not stop the hooks: dispose the monitor as well.
  Only categories and window IDs are recorded, never key codes or typed content.
- `new WindowsSessionMonitor()` implements `ISessionMonitor` and `IDisposable`.
  It reports current-session WTS lock/unlock/logon/logoff, SystemEvents power
  suspend/resume and shutdown, and monitor AppStart/AppStop. Consume
  `ReadSessionEventsAsync(cancellationToken)` with one reader, then dispose.
- `WindowsIdleTime.GetIdleDuration()` returns the Windows last-input idle
  cross-check; it does not itself synthesize interactions or session events.

Dispose both monitors even after cancellation. Hook or notification setup failures
throw from the constructor; asynchronous thread/cleanup failures complete the
corresponding stream with an exception and are also reported by `Dispose`.
Streams are not broadcast and support only one reader over their lifetime.
Input and interaction output queues each hold at most 4,096 entries; pending
aggregation holds at most 8,192 window/second buckets. Repeated categories for
the same current window/second are coalesced before enqueueing. A full input or
output queue or pending bucket limit stops collection and surfaces an exception
through the interaction stream and `Dispose`, rather than blocking a hook or
silently losing events. Drain the output continuously. The lower-volume session
event stream remains unbounded.

## Interpretation and limitations

Visibility is the window's extended-frame rectangle intersected with the union
of monitor rectangles, minus rectangles of eligible windows in front, divided by
the entire frame area (including any off-screen portion). Overlapping monitors
and occluders are counted once. This is rectangular approximation: shaped,
partly transparent and per-pixel-alpha windows are not modeled. Hidden,
minimized, cloaked, owned, tool, transparent-style and this process's windows,
desktop/shell windows, taskbars and constant-alpha-zero layered windows
are excluded; excluded windows also do not occlude. Windows that disappear or
cannot provide a DWM cloaking state during enumeration are skipped.
Layered attribute query failure is not interpreted as alpha zero: per-pixel
`UpdateLayeredWindow` surfaces do not expose constant alpha and remain a
rectangular approximation, including fully transparent per-pixel surfaces.

IDs combine the top-level HWND and its host PID. They match interaction IDs but
can be reused after a window/process lifetime; UWP frame metadata is resolved to
its CoreWindow process when possible. Protected or exited processes can lack an
executable path and fall back to a process name or PID. Windows
use `FileVersionInfo.FileDescription` as their friendly name when available,
falling back to the process name. Process metadata is cached by PID plus native
process creation time, verified on every access using a newly opened handle.
Metadata is not reused if that identity cannot be verified; unused entries are
pruned after each capture. Interaction targets
are determined at event time (foreground for keyboard, root window under the
pointer for mouse) and may refer to a window excluded from snapshots. Interaction
seconds use UTC wall-clock time, with a short flush delay. Mouse movement counts
as hover. Windows' last-input tick counter wraps after approximately 49.7 days,
so very long idle intervals cannot be distinguished across a wrap.

Notifications and global hooks require an interactive desktop and do not capture
activity in other sessions or secure desktops; OS delivery is best-effort.
Windows can silently remove a slow low-level hook. Sampling is not atomic across
the entire desktop, and system time changes can affect event timestamps.

Offline geometry checks: restore the test project against a local source and run
`dotnet run --project WorkTimeTracker.Windows\Tests --no-restore`.
