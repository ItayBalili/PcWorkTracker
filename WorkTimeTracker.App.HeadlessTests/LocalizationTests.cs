using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace WorkTimeTracker.App.HeadlessTests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("DashboardStrings")]
    [InlineData("SettingsStrings")]
    [InlineData("HostStrings")]
    public void EnglishAndHebrewResourcesHaveIdenticalKeys(string name)
    {
        var manager = new ResourceManager("WorkTimeTracker.App." + name, typeof(TrackingService).Assembly);
        var hebrew = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var english = manager.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, false)!;
        static string[] Keys(ResourceSet set) => set.Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Keys(hebrew), Keys(english));
    }

    [Theory]
    [InlineData(LanguagePreference.System, "he-IL", "he-IL")]
    [InlineData(LanguagePreference.System, "fr-FR", "en-GB")]
    [InlineData(LanguagePreference.Hebrew, "en-US", "he-IL")]
    [InlineData(LanguagePreference.English, "he-IL", "en-GB")]
    public void LanguageSelectionUsesWindowsUiLanguage(LanguagePreference preference, string system, string expected)
    {
        var type = typeof(TrackingService).Assembly.GetType("WorkTimeTracker.App.UiCulture", true)!;
        var select = type.GetMethod("Select", BindingFlags.Static | BindingFlags.NonPublic)!;
        var culture = (CultureInfo)select.Invoke(null, [preference, CultureInfo.GetCultureInfo(system)])!;
        Assert.Equal(expected, culture.Name);
    }

    [Theory]
    [InlineData("he-IL", WeekStartPreference.Culture, "2026-11-22")]
    [InlineData("en-GB", WeekStartPreference.Culture, "2026-11-23")]
    [InlineData("en-GB", WeekStartPreference.Sunday, "2026-11-22")]
    [InlineData("he-IL", WeekStartPreference.Monday, "2026-11-23")]
    public void WeeklyRangeUsesCultureOrOverride(string cultureName, WeekStartPreference start, string expected)
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var type = typeof(TrackingService).Assembly.GetType("WorkTimeTracker.App.DashboardViewModel", true)!;
            var model = Activator.CreateInstance(type, "unused.sqlite3", (Func<AppSettings>)(() => new AppSettings { WeekStart = start }), null)!;
            type.GetField("date", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, new DateTime(2026, 11, 25));
            var period = type.GetField("period", BindingFlags.Instance | BindingFlags.NonPublic)!;
            period.SetValue(model, Enum.Parse(period.FieldType, "Week"));
            var range = ((DateOnly From, DateOnly To))type.GetMethod("Range", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null)!;
            Assert.Equal(DateOnly.ParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture), range.From);
            Assert.Equal(range.From.AddDays(6), range.To);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }
}
