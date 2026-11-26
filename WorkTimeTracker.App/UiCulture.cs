using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;

namespace WorkTimeTracker.App;

internal static class UiCulture
{
    private static CultureInfo? selected;
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    internal static CultureInfo Select(LanguagePreference preference, CultureInfo windowsLanguage) =>
        CultureInfo.GetCultureInfo(preference switch
        {
            LanguagePreference.Hebrew => "he-IL",
            LanguagePreference.English => "en-GB",
            LanguagePreference.System => windowsLanguage.TwoLetterISOLanguageName == "he" ? "he-IL" : "en-GB",
            _ => throw new ArgumentOutOfRangeException(nameof(preference))
        });

    internal static void Apply(LanguagePreference preference)
    {
        var culture = Select(preference, CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()));
        selected = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    internal static CultureInfo Current => selected ?? CultureInfo.CurrentCulture;
    internal static bool IsRightToLeft => Current.TextInfo.IsRightToLeft;
    internal static System.Windows.FlowDirection Direction => IsRightToLeft
        ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;
    internal static XmlLanguage Language => XmlLanguage.GetLanguage(Current.IetfLanguageTag);

    internal static DayOfWeek FirstDay(WeekStartPreference preference) => preference switch
    {
        WeekStartPreference.Culture => Current.DateTimeFormat.FirstDayOfWeek,
        WeekStartPreference.Sunday => DayOfWeek.Sunday,
        WeekStartPreference.Monday => DayOfWeek.Monday,
        _ => throw new ArgumentOutOfRangeException(nameof(preference))
    };
}
