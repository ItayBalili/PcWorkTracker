using System.Collections.Frozen;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Core.Labels;

namespace WorkTimeTracker.App;

/// <summary>
/// Stores settings only. Save does not edit the registry: the host must call SetAutostart
/// explicitly and handle failures before accepting the new settings.
/// </summary>
public sealed class SettingsStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private const int CurrentVersion = 2;
    private static readonly string[] DefaultExcludedApplications =
        ["ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "LockApp", "WorkTimeTracker.App"];
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "WorkTimeTracker";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<ThemePreference>(allowIntegerValues: false), new JsonStringEnumConverter<AccentPreference>(allowIntegerValues: false),
            new JsonStringEnumConverter<LanguagePreference>(allowIntegerValues: false), new JsonStringEnumConverter<WeekStartPreference>(allowIntegerValues: false) }
    };

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkTimeTracker");

    public SettingsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Only a missing file produces defaults. Corruption and access errors are surfaced.</summary>
    public AppSettings Load()
    {
        FileStream stream;
        try { stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return new AppSettings(); }
        catch (DirectoryNotFoundException) { return new AppSettings(); }

        using (stream)
        {
            try
            {
                if (stream.Length > MaximumFileBytes)
                    throw new InvalidDataException("Settings file is too large.");
                var payload = JsonSerializer.Deserialize<SettingsPayload>(stream, JsonOptions)
                    ?? throw new JsonException("Settings cannot be null.");
                return payload.ToSettings().Snapshot();
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or OverflowException)
            {
                throw new InvalidDataException($"Invalid settings file: {SettingsPath}", exception);
            }
        }
    }

    /// <summary>Flushes a validated immutable snapshot, then atomically replaces the previous file.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.Snapshot();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(SettingsPayload.FromSettings(snapshot), JsonOptions);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("Settings file is too large.");
        Directory.CreateDirectory(DataDirectory);
        var pendingPath = Path.Combine(DataDirectory, $".settings-{Guid.NewGuid():N}.pending");
        try
        {
            using (var stream = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(SettingsPath))
                File.Replace(pendingPath, SettingsPath, destinationBackupFileName: null);
            else
                File.Move(pendingPath, SettingsPath);
        }
        finally
        {
            // Cleanup must not hide the original write/replace failure.
            try { File.Delete(pendingPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Registers the current app executable with --minimized in HKCU Run.
    /// Pass the deployed executable path when called from a launcher/test host.
    /// Registry and path failures propagate; this does not save JSON.
    /// </summary>
    public void SetAutostart(bool enabled, string? executablePath = null)
    {
        if (enabled)
        {
            var path = Path.GetFullPath(executablePath ?? Environment.ProcessPath
                ?? throw new InvalidOperationException("The application executable path is unavailable."));
            if (path.Contains('"') || !File.Exists(path) ||
                !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A deployed application executable is required.", nameof(executablePath));
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new IOException("Cannot open the current-user Run registry key.");
            key.SetValue(RunValue, $"\"{path}\" --minimized", RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    private sealed record SettingsPayload
    {
        public required int Version { get; init; }
        public required TrackerPayload Tracker { get; init; }
        public required bool StartWithWindows { get; init; }
        public required ThemePreference Theme { get; init; }
        public AccentPreference Accent { get; init; } = AccentPreference.Blue;
        public LanguagePreference Language { get; init; } = LanguagePreference.System;
        public WeekStartPreference WeekStart { get; init; } = WeekStartPreference.Culture;
        public required bool HasShownTrayNotice { get; init; }
        public WindowPlacement? WindowPlacement { get; init; }
        public LabelRule[]? LabelRules { get; init; } = LabelExtractor.DefaultRules.ToArray();
        public TitleNormalizationRule[]? TitleNormalizationRules { get; init; } = TitleNormalizer.DefaultRules.ToArray();

        public AppSettings ToSettings()
        {
            if (Version is not (1 or CurrentVersion))
                throw new JsonException("Unsupported settings version.");
            if (Tracker is null)
                throw new JsonException("Tracker settings are required.");
            var tracker = Tracker.ToSettings();
            // Version 1 predates the default shell exclusions; merge them in once, the user can remove them afterwards.
            if (Version == 1)
                tracker = tracker with
                {
                    ExcludedApplications = tracker.ExcludedApplications.Concat(DefaultExcludedApplications)
                        .ToFrozenSet(StringComparer.OrdinalIgnoreCase)
                };
            return new AppSettings
            {
                Tracker = tracker, LabelRules = LabelRules ?? throw new JsonException("Label rules cannot be null."),
                TitleNormalizationRules = TitleNormalizationRules ?? throw new JsonException("Title normalization rules cannot be null."),
                StartWithWindows = StartWithWindows,
                Theme = Theme, Accent = Accent, Language = Language, WeekStart = WeekStart, HasShownTrayNotice = HasShownTrayNotice,
                WindowPlacement = WindowPlacement
            };
        }

        public static SettingsPayload FromSettings(AppSettings settings) => new()
        {
            Version = CurrentVersion, Tracker = TrackerPayload.FromSettings(settings.Tracker),
            StartWithWindows = settings.StartWithWindows, Theme = settings.Theme, Accent = settings.Accent,
            Language = settings.Language, WeekStart = settings.WeekStart,
            HasShownTrayNotice = settings.HasShownTrayNotice, LabelRules = settings.LabelRules.ToArray(),
            TitleNormalizationRules = settings.TitleNormalizationRules.ToArray(),
            WindowPlacement = settings.WindowPlacement
        };
    }

    // An explicit array wire format avoids System.Text.Json's unsupported IReadOnlySet construction.
    private sealed record TrackerPayload
    {
        public required decimal MinimumVisiblePercent { get; init; }
        public required TimeSpan InteractionWindow { get; init; }
        public required TimeSpan ActiveTimeout { get; init; }
        public required TimeSpan AwayTimeout { get; init; }
        public required TimeSpan AutoEndAfter { get; init; }
        public required TimeSpan SampleInterval { get; init; }
        public string[]? ExcludedApplications { get; init; } = DefaultExcludedApplications.ToArray();
        public required TimeSpan? RetentionPeriod { get; init; }

        public TrackerSettings ToSettings()
        {
            if (ExcludedApplications is null || ExcludedApplications.Any(value => value is null))
                throw new JsonException("Excluded applications must be an array of strings.");
            return new TrackerSettings
            {
                MinimumVisiblePercent = MinimumVisiblePercent, InteractionWindow = InteractionWindow,
                ActiveTimeout = ActiveTimeout, AwayTimeout = AwayTimeout, AutoEndAfter = AutoEndAfter,
                SampleInterval = SampleInterval, RetentionPeriod = RetentionPeriod,
                ExcludedApplications = ExcludedApplications.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            };
        }

        public static TrackerPayload FromSettings(TrackerSettings settings) => new()
        {
            MinimumVisiblePercent = settings.MinimumVisiblePercent, InteractionWindow = settings.InteractionWindow,
            ActiveTimeout = settings.ActiveTimeout, AwayTimeout = settings.AwayTimeout,
            AutoEndAfter = settings.AutoEndAfter, SampleInterval = settings.SampleInterval,
            RetentionPeriod = settings.RetentionPeriod,
            ExcludedApplications = settings.ExcludedApplications.Order(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }
}
