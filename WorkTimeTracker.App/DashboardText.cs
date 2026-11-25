using System.Resources;
using System.Windows.Markup;

namespace WorkTimeTracker.App;

public sealed class DashboardString : MarkupExtension
{
    public DashboardString(string key) => Key = key;
    public string Key { get; }
    public override object ProvideValue(IServiceProvider serviceProvider) => DashboardText.Get(Key);
}

internal static class DashboardText
{
    private static readonly ResourceManager Resources = new("WorkTimeTracker.App.DashboardStrings", typeof(DashboardText).Assembly);
    public static string Get(string key) => Resources.GetString(key) ?? key;
}
