using System.Collections.Frozen;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.App;

public enum ThemePreference { System, Light, Dark }

/// <summary>
/// Restored (not maximized) bounds in WPF device-independent pixels.
/// Negative origins support secondary monitors; the host checks current screen visibility.
/// </summary>
public sealed record WindowPlacement
{
    public required double Left { get; init; }
    public required double Top { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required bool IsMaximized { get; init; }

    public void Validate()
    {
        if (!double.IsFinite(Left))
            throw new ArgumentOutOfRangeException(nameof(Left));
        if (!double.IsFinite(Top))
            throw new ArgumentOutOfRangeException(nameof(Top));
        if (!double.IsFinite(Width) || Width <= 0 || !double.IsFinite(Left + Width))
            throw new ArgumentOutOfRangeException(nameof(Width));
        if (!double.IsFinite(Height) || Height <= 0 || !double.IsFinite(Top + Height))
            throw new ArgumentOutOfRangeException(nameof(Height));
    }
}

public sealed record AppSettings
{
    public TrackerSettings Tracker { get; init; } = new()
    {
        ExcludedApplications = Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase)
    };
    public bool StartWithWindows { get; init; }
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public bool HasShownTrayNotice { get; init; }
    public WindowPlacement? WindowPlacement { get; init; }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Tracker);
        Tracker.Validate();
        WindowPlacement?.Validate();
        if (!Enum.IsDefined(Theme))
            throw new ArgumentOutOfRangeException(nameof(Theme));
        if (Tracker.ExcludedApplications is null ||
            Tracker.ExcludedApplications.Any(value => string.IsNullOrWhiteSpace(value) ||
                value != value.Trim() || value.Any(char.IsControl)))
            throw new ArgumentException("Invalid excluded application.", nameof(Tracker));
    }

    internal AppSettings Snapshot()
    {
        Validate();
        return this with
        {
            Tracker = Tracker with
            {
                ExcludedApplications = Tracker.ExcludedApplications.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            }
        };
    }
}
