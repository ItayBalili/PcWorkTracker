using System.IO;
using WorkTimeTracker.Core.Abstractions;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.App;

public enum TrackingState { Created, Tracking, Paused, Unavailable, DayEnded, Stopping, Stopped, Faulted }

public sealed record TrackingStatus(TrackingState State, long? WorkDayId, WorkDay? WorkDay);

/// <summary>Resolves native monitor IDs into immutable metadata versions, never guessed identities.</summary>
public interface ITrackingWindowResolver : IDisposable
{
    void CheckHealth();
    WindowInfo? ResolveSnapshot(WindowSnapshot snapshot);
    WindowInfo? ResolveInteraction(InteractionSecond interaction);
    DateTimeOffset? GetKnownEndUtc(string windowId);
}

/// <summary>The service owns and disposes all three collectors.</summary>
public sealed record TrackingCollectors(
    IWindowSnapshotProvider Snapshots,
    IInputMonitor Input,
    ISessionMonitor Sessions,
    ITrackingWindowResolver Windows)
{
    public DateTimeOffset? SessionLogonUtc { get; init; }
    /// <summary>Null for injected collectors; Windows factory queries the current WTS session.</summary>
    public bool? InitiallyLocked { get; init; }
    /// <summary>Session-global idle cross-check, independent of any target window.</summary>
    public Func<TimeSpan>? GetIdleDuration { get; init; }
}

public sealed record TrackingOptions
{
    public string DatabasePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WorkTimeTracker", "tracking.sqlite3");
    public TrackerSettings Settings { get; init; } = new();
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    public DateTimeOffset? SessionLogonUtc { get; init; }
}
