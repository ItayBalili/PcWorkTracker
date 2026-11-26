using WorkTimeTracker.Core.Labels;
using WorkTimeTracker.Core.Models;

namespace WorkTimeTracker.App;

internal sealed record ApplicationUsageTitle(string Title, TimeSpan Active, TimeSpan Displayed)
{
    public string DisplayTitle => $"\u2068{Title}\u2069";
    public string ActiveText => DashboardViewModel.Duration(Active);
    public string DisplayedText => DashboardViewModel.Duration(Displayed);
}
internal sealed record ApplicationUsageDay(DateOnly Date, TimeSpan Active, TimeSpan Displayed)
{
    public string DateText => Date.ToString("d", UiCulture.Current);
    public string ActiveText => DashboardViewModel.Duration(Active);
    public double Width { get; init; }
}
internal sealed record ApplicationUsageDocument(string Label, TimeSpan Active, TimeSpan Displayed,
    int DaysUsed, DateTimeOffset? LastUsed, IReadOnlyList<ApplicationUsageTitle> Titles,
    IReadOnlyList<ApplicationUsageDay> Days)
{
    public double Percent { get; init; }
    public string DisplayLabel => $"\u2068{Label}\u2069";
    public string ActiveText => DashboardViewModel.Duration(Active);
    public string DisplayedText => DashboardViewModel.Duration(Displayed);
    public string PercentText => Percent.ToString("0.#", UiCulture.Current) + "%";
    public string LastUsedText => LastUsed?.ToLocalTime().ToString("g", UiCulture.Current) ?? "\u2014";
}

internal sealed record ApplicationUsageReport(TimeSpan Active, TimeSpan Displayed, int DaysUsed,
    DateTimeOffset? FirstUsed, DateTimeOffset? LastUsed, IReadOnlyList<ApplicationUsageDocument> Documents,
    IReadOnlyList<ApplicationUsageDay> Days)
{
    private sealed record Entry(DateOnly Date, string Label, string Title, TimeSpan Active, TimeSpan Displayed,
        DateTimeOffset? First, DateTimeOffset? Last);

    public static ApplicationUsageReport Create(DashboardReport report, AppSettings settings, string appKey)
    {
        var entries = new List<Entry>();
        for (var index = 0; index < report.Reports.Count; index++)
        {
            var day = report.Reports[index];
            foreach (var metric in day.Windows.Where(x =>
                string.Equals(DashboardReport.AppKey(x.Window), appKey, StringComparison.OrdinalIgnoreCase) &&
                (x.ActiveTime > TimeSpan.Zero || x.DisplayedTime > TimeSpan.Zero)))
            {
                var normalized = TitleNormalizer.Normalize(metric.Window.Title, settings.TitleNormalizationRules);
                var title = string.IsNullOrWhiteSpace(normalized) ? DashboardText.Get("NoTitle") : normalized;
                var label = LabelExtractor.Extract(metric.Window with { Title = normalized }, settings.LabelRules);
                if (string.IsNullOrWhiteSpace(label)) label = title;
                entries.Add(new(day.WorkDay.Date, label, metric.Window.Title, metric.ActiveTime, metric.DisplayedTime, metric.FirstUsedUtc, metric.LastUsedUtc));
            }
        }
        static TimeSpan Sum(IEnumerable<TimeSpan> values) => values.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        static ApplicationUsageDay[] Days(IEnumerable<Entry> values) => values.GroupBy(x => x.Date).OrderBy(x => x.Key)
            .Select(g => new ApplicationUsageDay(g.Key, Sum(g.Select(x => x.Active)), Sum(g.Select(x => x.Displayed)))).ToArray();
        var active = Sum(entries.Select(x => x.Active));
        var documents = entries.GroupBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).Select(g =>
        {
            var documentActive = Sum(g.Select(x => x.Active));
            return new ApplicationUsageDocument(g.Key, documentActive, Sum(g.Select(x => x.Displayed)),
                g.Select(x => x.Date).Distinct().Count(), g.Max(x => x.Last),
                g.GroupBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                    .Select(t => new ApplicationUsageTitle(t.Key, Sum(t.Select(x => x.Active)), Sum(t.Select(x => x.Displayed)))).ToArray(),
                Days(g))
            { Percent = active == TimeSpan.Zero ? 0 : documentActive.TotalSeconds / active.TotalSeconds * 100 };
        }).OrderByDescending(x => x.Active).ThenBy(x => x.Label).ToArray();
        return new(active, Sum(entries.Select(x => x.Displayed)), entries.Select(x => x.Date).Distinct().Count(),
            entries.Min(x => x.First), entries.Max(x => x.Last), documents, Days(entries));
    }
}
