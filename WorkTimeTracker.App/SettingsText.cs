using System.Globalization;
using System.Resources;

namespace WorkTimeTracker.App;

public static class SettingsText
{
    private static readonly ResourceManager Resources = new(
        "WorkTimeTracker.App.SettingsStrings", typeof(SettingsText).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException(key);
    public static string Title => Get(nameof(Title));
    public static string MinimumVisible => Get(nameof(MinimumVisible));
    public static string InteractionMinutes => Get(nameof(InteractionMinutes));
    public static string ActiveSeconds => Get(nameof(ActiveSeconds));
    public static string AwayMinutes => Get(nameof(AwayMinutes));
    public static string AutoEndHours => Get(nameof(AutoEndHours));
    public static string SampleSeconds => Get(nameof(SampleSeconds));
    public static string RetentionDays => Get(nameof(RetentionDays));
    public static string Exclusions => Get(nameof(Exclusions));
    public static string ExclusionsHint => Get(nameof(ExclusionsHint));
    public static string StartWithWindows => Get(nameof(StartWithWindows));
    public static string Theme => Get(nameof(Theme));
    public static string ThemeSystem => Get(nameof(ThemeSystem));
    public static string ThemeLight => Get(nameof(ThemeLight));
    public static string ThemeDark => Get(nameof(ThemeDark));
    public static string OpenFolder => Get(nameof(OpenFolder));
    public static string Save => Get(nameof(Save));
    public static string Cancel => Get(nameof(Cancel));
    public static string LabelRules => Get(nameof(LabelRules));
    public static string RuleEnabled => Get(nameof(RuleEnabled));
    public static string RuleApplication => Get(nameof(RuleApplication));
    public static string RulePattern => Get(nameof(RulePattern));
    public static string RuleAdd => Get(nameof(RuleAdd));
    public static string RuleRemove => Get(nameof(RuleRemove));
    public static string RuleTest => Get(nameof(RuleTest));
    public static string RuleTry => Get(nameof(RuleTry));
}
