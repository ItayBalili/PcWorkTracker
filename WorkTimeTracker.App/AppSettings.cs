using System.Collections.Frozen;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Core.Labels;

namespace WorkTimeTracker.App;

public enum ThemePreference { System, Light, Dark }
public enum AccentPreference { System, Blue, Green, Purple }

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
        ExcludedApplications = new[] { "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "LockApp", "WorkTimeTracker.App" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
    };
    public IReadOnlyList<LabelRule> LabelRules { get; init; } = LabelExtractor.DefaultRules;
    public bool StartWithWindows { get; init; }
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public AccentPreference Accent { get; init; } = AccentPreference.Blue;
    public bool HasShownTrayNotice { get; init; }
    public WindowPlacement? WindowPlacement { get; init; }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Tracker);
        Tracker.Validate();
        LabelExtractor.Validate(LabelRules);
        WindowPlacement?.Validate();
        if (!Enum.IsDefined(Theme))
            throw new ArgumentOutOfRangeException(nameof(Theme));
        if (!Enum.IsDefined(Accent))
            throw new ArgumentOutOfRangeException(nameof(Accent));
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
            },
            LabelRules = Array.AsReadOnly(LabelRules.ToArray())
        };
    }
}
