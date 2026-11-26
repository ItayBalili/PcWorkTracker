using System.IO;
using System.Text.Json.Nodes;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.App.HeadlessTests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(
        "artifacts", "settings-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void MissingFileReturnsDefaultsWithoutCreatingAnything()
    {
        var store = new SettingsStore(directory);
        var defaults = store.Load();
        Assert.Equal(new TrackerSettings().SampleInterval, defaults.Tracker.SampleInterval);
        Assert.Contains("ShellExperienceHost", defaults.Tracker.ExcludedApplications);
        Assert.Contains("WorkTimeTracker.App", defaults.Tracker.ExcludedApplications);
        Assert.Equal(AccentPreference.Blue, defaults.Accent);
        Assert.Equal(LanguagePreference.System, defaults.Language);
        Assert.Equal(WeekStartPreference.Culture, defaults.WeekStart);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void RoundTripPreservesAllFieldsAndProducesImmutableCaseInsensitiveExclusions()
    {
        var store = new SettingsStore(directory);
        var settings = new AppSettings
        {
            StartWithWindows = true, Theme = ThemePreference.Dark, Accent = AccentPreference.Purple, HasShownTrayNotice = true,
            Language = LanguagePreference.English, WeekStart = WeekStartPreference.Sunday,
            Tracker = new TrackerSettings
            {
                MinimumVisiblePercent = 42.5m,
                InteractionWindow = TimeSpan.FromMinutes(8),
                ActiveTimeout = TimeSpan.FromSeconds(45),
                AwayTimeout = TimeSpan.FromMinutes(7),
                AutoEndAfter = TimeSpan.FromHours(6),
                SampleInterval = TimeSpan.FromMilliseconds(1500),
                RetentionPeriod = TimeSpan.FromDays(90),
                ExcludedApplications = new HashSet<string> { "Editor.EXE", @"C:\Private\private.exe" }
            }
        };
        store.Save(settings);
        var loaded = store.Load();
        Assert.True(loaded.StartWithWindows && loaded.HasShownTrayNotice);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
        Assert.Equal(AccentPreference.Purple, loaded.Accent);
        Assert.Equal(LanguagePreference.English, loaded.Language);
        Assert.Equal(WeekStartPreference.Sunday, loaded.WeekStart);
        Assert.Equal(settings.Tracker with { ExcludedApplications = loaded.Tracker.ExcludedApplications }, loaded.Tracker);
        Assert.True(loaded.Tracker.ExcludedApplications.Contains("editor.exe"));
        Assert.Equal(2, loaded.Tracker.ExcludedApplications.Count);
        Assert.False(loaded.Tracker.ExcludedApplications is HashSet<string>);
    }

    [Fact]
    public void MissingPersistedExclusionAndLabelRulesUseEditableDefaults()
    {
        var store = new SettingsStore(directory);
        store.Save(new AppSettings { Tracker = new TrackerSettings { ExcludedApplications = new HashSet<string>() } });
        var document = JsonNode.Parse(File.ReadAllText(store.SettingsPath))!;
        document["Tracker"]!.AsObject().Remove("ExcludedApplications");
        document.AsObject().Remove("LabelRules");
        document.AsObject().Remove("Accent");
        File.WriteAllText(store.SettingsPath, document.ToJsonString());
        var loaded = store.Load();
        Assert.Contains("ShellExperienceHost", loaded.Tracker.ExcludedApplications);
        Assert.Contains("WorkTimeTracker.App", loaded.Tracker.ExcludedApplications);
        Assert.Contains(loaded.LabelRules, rule => rule.Application == "devenv");
        Assert.Equal(AccentPreference.Blue, loaded.Accent);
    }

    [Fact]
    public void Version1FilesGainDefaultShellExclusionsOnceAndKeepUserEntries()
    {
        var store = new SettingsStore(directory);
        store.Save(new AppSettings { Tracker = new TrackerSettings { ExcludedApplications = new HashSet<string> { "private" } } });
        var document = JsonNode.Parse(File.ReadAllText(store.SettingsPath))!;
        document["Version"] = 1;
        File.WriteAllText(store.SettingsPath, document.ToJsonString());
        var migrated = store.Load();
        Assert.Contains("private", migrated.Tracker.ExcludedApplications);
        Assert.Contains("SearchHost", migrated.Tracker.ExcludedApplications);

        // After saving as the current version, a user's removal of a default is preserved.
        store.Save(migrated with { Tracker = migrated.Tracker with { ExcludedApplications = new HashSet<string> { "private" } } });
        Assert.Equal(["private"], store.Load().Tracker.ExcludedApplications);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    public void CorruptOrIncompleteFilesAreNotReplacedByDefaults(string json)
    {
        var store = new SettingsStore(directory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(store.SettingsPath, json);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Equal(json, File.ReadAllText(store.SettingsPath));
    }

    [Theory]
    [InlineData("negative-interval")]
    [InlineData("null-exclusions")]
    [InlineData("null-exclusion")]
    [InlineData("invalid-theme")]
    [InlineData("future-version")]
    public void InvalidPersistedValuesAreRejected(string scenario)
    {
        var store = new SettingsStore(directory);
        store.Save(new AppSettings());
        var document = JsonNode.Parse(File.ReadAllText(store.SettingsPath))!;
        switch (scenario)
        {
            case "negative-interval": document["Tracker"]!["SampleInterval"] = "-00:00:01"; break;
            case "null-exclusions": document["Tracker"]!["ExcludedApplications"] = null; break;
            case "null-exclusion": document["Tracker"]!["ExcludedApplications"] = new JsonArray((JsonNode?)null); break;
            case "invalid-theme": document["Theme"] = "Sepia"; break;
            case "future-version": document["Version"] = 3; break;
        }
        File.WriteAllText(store.SettingsPath, document.ToJsonString());
        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void InvalidSavePreservesPreviousFileAndAtomicReplacementRemovesPendingFiles()
    {
        var store = new SettingsStore(directory);
        store.Save(new AppSettings());
        var previous = File.ReadAllText(store.SettingsPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(new AppSettings
        {
            Tracker = new TrackerSettings { MinimumVisiblePercent = 101 }
        }));
        Assert.Equal(previous, File.ReadAllText(store.SettingsPath));
        store.Save(new AppSettings { Theme = ThemePreference.Light });
        Assert.Equal(ThemePreference.Light, store.Load().Theme);
        Assert.Null(store.Load().Tracker.RetentionPeriod);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void ReplacementFailurePreservesExistingFileAndCleansPendingWrite()
    {
        var store = new SettingsStore(directory);
        store.Save(new AppSettings());
        using (var locked = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => store.Save(new AppSettings { Theme = ThemePreference.Dark }));
        Assert.Equal(ThemePreference.System, store.Load().Theme);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void InvalidAutostartPathFailsBeforeAnyRegistryWrite()
    {
        var store = new SettingsStore(directory);
        Assert.Throws<ArgumentException>(() => store.SetAutostart(true, Path.Combine(directory, "missing.exe")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowPlacementRoundTripsRestoredBoundsAndMaximizedState(bool maximized)
    {
        var store = new SettingsStore(directory);
        var placement = new WindowPlacement
        {
            Left = -1600.5, Top = -120, Width = 1280.25, Height = 800, IsMaximized = maximized
        };
        store.Save(new AppSettings { WindowPlacement = placement });
        Assert.Equal(placement, store.Load().WindowPlacement);
    }

    [Fact]
    public void OlderSettingsWithoutWindowPlacementRemainCompatible()
    {
        var store = new SettingsStore(directory);
        store.Save(new AppSettings { Theme = ThemePreference.Dark });
        var document = JsonNode.Parse(File.ReadAllText(store.SettingsPath))!.AsObject();
        document.Remove("WindowPlacement");
        File.WriteAllText(store.SettingsPath, document.ToJsonString());
        var loaded = store.Load();
        Assert.Null(loaded.WindowPlacement);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
    }

    [Theory]
    [InlineData(double.NaN, 0, 1000, 800)]
    [InlineData(0, double.PositiveInfinity, 1000, 800)]
    [InlineData(0, 0, double.NegativeInfinity, 800)]
    [InlineData(0, 0, 1000, double.NaN)]
    [InlineData(0, 0, 0, 800)]
    [InlineData(0, 0, 1000, -1)]
    [InlineData(double.MaxValue, 0, double.MaxValue, 800)]
    public void InvalidWindowPlacementCannotReplaceValidSettings(double left, double top, double width, double height)
    {
        var store = new SettingsStore(directory);
        store.Save(new AppSettings { Theme = ThemePreference.Light });
        var previous = File.ReadAllText(store.SettingsPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(new AppSettings
        {
            WindowPlacement = new WindowPlacement
            {
                Left = left, Top = top, Width = width, Height = height, IsMaximized = false
            }
        }));
        Assert.Equal(previous, File.ReadAllText(store.SettingsPath));
    }

    [Theory]
    [InlineData("""{"Left":0,"Top":0,"Width":0,"Height":800,"IsMaximized":false}""")]
    [InlineData("""{"Left":0,"Top":0,"Width":1000,"Height":-1,"IsMaximized":true}""")]
    [InlineData("""{"Left":0,"Top":0,"Width":"NaN","Height":800,"IsMaximized":false}""")]
    [InlineData("""{"Left":1e999,"Top":0,"Width":1000,"Height":800,"IsMaximized":false}""")]
    [InlineData("""{"Width":1000,"Height":800,"IsMaximized":false}""")]
    public void InvalidPersistedPlacementIsReportedAsCorruptSettings(string placementJson)
    {
        var store = new SettingsStore(directory);
        store.Save(new AppSettings());
        var document = JsonNode.Parse(File.ReadAllText(store.SettingsPath))!;
        document["WindowPlacement"] = JsonNode.Parse(placementJson);
        File.WriteAllText(store.SettingsPath, document.ToJsonString());
        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
