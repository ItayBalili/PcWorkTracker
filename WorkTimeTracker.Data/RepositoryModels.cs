using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.Data;

public sealed record StoredWorkDay(
    long Id,
    WorkDay WorkDay,
    DateTimeOffset? TrackingStartedUtc);

/// <summary>Raw observations only; never key values, input text, or calculated metrics.</summary>
public sealed record RawSignalBatch
{
    public IReadOnlyList<WindowInfo> Windows { get; init; } = [];
    public IReadOnlyList<VisibilitySegment> VisibilitySegments { get; init; } = [];
    public IReadOnlyList<InteractionSecond> Interactions { get; init; } = [];
    public IReadOnlyList<GlobalInputObservation> GlobalInputs { get; init; } = [];
    public IReadOnlyList<SessionEvent> SessionEvents { get; init; } = [];
    public DateTimeOffset? TrackingStartedUtc { get; init; }
}

/// <param name="LastFlushedUtc">Commit wall-clock time, not a signal completeness watermark.</param>
/// <param name="OpenWorkDay">The persisted open day, if any. Recovery never invents an end time.</param>
public sealed record RecoveryState(DateTimeOffset? LastFlushedUtc, StoredWorkDay? OpenWorkDay);
