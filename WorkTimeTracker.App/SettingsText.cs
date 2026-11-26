using System.Globalization;
using System.Resources;

namespace WorkTimeTracker.App;

public static class SettingsText
{
    private static readonly ResourceManager Resources = new(
        "WorkTimeTracker.App.SettingsStrings", typeof(SettingsText).Assembly);
    public static string Get(string key) => Resources.GetString(key, UiCulture.Current)
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
    public static string TitleNormalization => Get(nameof(TitleNormalization));
    public static string TitleNormalizationTest => Get(nameof(TitleNormalizationTest));
    public static string MeasurementSection => Get(nameof(MeasurementSection));
    public static string WorkDaySection => Get(nameof(WorkDaySection));
    public static string DataSection => Get(nameof(DataSection));
    public static string AppearanceSection => Get(nameof(AppearanceSection));
    public static string MinimumVisibleHint => Get(nameof(MinimumVisibleHint));
    public static string InteractionHint => Get(nameof(InteractionHint));
    public static string ActiveHint => Get(nameof(ActiveHint));
    public static string AwayHint => Get(nameof(AwayHint));
    public static string SampleHint => Get(nameof(SampleHint));
    public static string AutoEndHint => Get(nameof(AutoEndHint));
    public static string RetentionHint => Get(nameof(RetentionHint));
    public static string MinutesUnit => Get(nameof(MinutesUnit));
    public static string SecondsUnit => Get(nameof(SecondsUnit));
    public static string HoursUnit => Get(nameof(HoursUnit));
    public static string DaysUnit => Get(nameof(DaysUnit));
    public static string Accent => Get(nameof(Accent));
    public static string AccentSystem => Get(nameof(AccentSystem));
    public static string AccentBlue => Get(nameof(AccentBlue));
    public static string AccentGreen => Get(nameof(AccentGreen));
    public static string AccentPurple => Get(nameof(AccentPurple));
    public static string Language => Get(nameof(Language));
    public static string LanguageSystem => Get(nameof(LanguageSystem));
    public static string LanguageHebrew => Get(nameof(LanguageHebrew));
    public static string LanguageEnglish => Get(nameof(LanguageEnglish));
    public static string WeekStart => Get(nameof(WeekStart));
    public static string WeekStartCulture => Get(nameof(WeekStartCulture));
    public static string WeekStartSunday => Get(nameof(WeekStartSunday));
    public static string WeekStartMonday => Get(nameof(WeekStartMonday));
}
