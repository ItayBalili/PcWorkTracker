using System.IO;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using WorkTimeTracker.Core.Labels;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.App;

internal sealed record ApplicationChoice(string Key, string Name, ImageSource? Icon, IReadOnlyList<string> SearchTerms)
{
    public string DisplayName => $"\u2068{Name}\u2069";
    public bool Matches(string query) => string.IsNullOrWhiteSpace(query) ||
        Name.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
        SearchTerms.Any(term => term.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase));
}

internal static class ApplicationCatalog
{
    public static IReadOnlyList<ApplicationChoice> Load(string path, AppSettings settings)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT a.ProcessName, a.FriendlyName, a.ExecutablePath, a.PathIsNull, w.Title FROM Windows w JOIN Apps a ON a.Id = w.AppId";
        using var reader = command.ExecuteReader();
        var windows = new List<WindowInfo>();
        while (reader.Read())
            windows.Add(new WindowInfo("", reader.GetString(0), reader.GetString(1),
                reader.GetInt64(3) != 0 ? null : reader.GetString(2), reader.GetString(4)));
        return windows.GroupBy(DashboardReport.AppKey, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var first = group.First();
            var appearance = PackageAppearance.ForExecutable(first.ExecutablePath);
            var name = appearance.Name ?? (string.IsNullOrWhiteSpace(first.FriendlyName) ? first.ProcessName : first.FriendlyName);
            var terms = group.SelectMany(window =>
            {
                var normalized = TitleNormalizer.Normalize(window.Title, settings.TitleNormalizationRules);
                var label = LabelExtractor.Extract(window with { Title = normalized }, settings.LabelRules);
                return new[] { window.Title, normalized, label };
            }).Where(term => !string.IsNullOrWhiteSpace(term)).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
            var icon = appearance.Icon ?? DashboardReport.LoadIcon(first.ExecutablePath) ??
                windows.Where(window => string.Equals(window.ProcessName, first.ProcessName, StringComparison.OrdinalIgnoreCase))
                    .Select(window => PackageAppearance.ForExecutable(window.ExecutablePath).Icon).FirstOrDefault(candidate => candidate is not null);
            return new ApplicationChoice(group.Key, name, icon, terms);
        }).OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
