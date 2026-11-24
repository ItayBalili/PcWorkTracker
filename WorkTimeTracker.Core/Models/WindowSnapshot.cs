namespace WorkTimeTracker.Core.Models;

public sealed record WindowSnapshot(
    WindowInfo Window,
    DateTimeOffset CapturedUtc,
    decimal VisiblePercent,
    bool IsForeground);
