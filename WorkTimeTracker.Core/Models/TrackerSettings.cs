namespace WorkTimeTracker.Core.Models;

public sealed record TrackerSettings
{
    public decimal MinimumVisiblePercent { get; init; } = 30m;
    public TimeSpan InteractionWindow { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ActiveTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan AwayTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan AutoEndAfter { get; init; } = TimeSpan.FromHours(4);
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(1);
    public IReadOnlySet<string> ExcludedApplications { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public TimeSpan? RetentionPeriod { get; init; }

    public void Validate()
    {
        if (MinimumVisiblePercent is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumVisiblePercent));
        }

        if (InteractionWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InteractionWindow));
        }

        if (ActiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ActiveTimeout));
        }

        if (AwayTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AwayTimeout));
        }

        if (AutoEndAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AutoEndAfter));
        }

        if (SampleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleInterval));
        }

        if (RetentionPeriod is { } retentionPeriod && retentionPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetentionPeriod));
        }
    }
}
