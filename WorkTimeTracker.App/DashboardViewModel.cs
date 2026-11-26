using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ImageSource = System.Windows.Media.ImageSource;
using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Core.Labels;
using WorkTimeTracker.Data;

namespace WorkTimeTracker.App;

internal enum DashboardPeriod { Day, Week, Month, Custom }
internal enum DashboardGrouping { AppLabelTitle, Label, App }

internal sealed class DashboardViewModel : INotifyPropertyChanged
{
    private readonly string databasePath;
    private readonly Func<AppSettings> settings;
    private readonly Func<TrackingService>? tracker;
    private readonly Dictionary<long, IncrementalCalculationEngine> calculations = new();
    private DashboardGrouping grouping;
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private long revision;
    private DateTime date = DateTime.Today;
    private DashboardPeriod period;
    private DateTime customFrom = DateTime.Today;
    private DateTime customTo = DateTime.Today;
    private string search = "";
    private string message = "";
    private bool loading;
    private bool showZeroApplications;
    private double timelineWidth = 960;
    private DashboardReport? report;

    public DashboardViewModel(string databasePath, Func<AppSettings> settings, Func<TrackingService>? tracker = null)
    {
        this.databasePath = databasePath;
        this.settings = settings;
        this.tracker = tracker;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ReportApplying;
    public event Action? ReportApplied;
    public ObservableCollection<DashboardApplication> Applications { get; } = [];
    public ObservableCollection<DashboardDay> Days { get; } = [];
    public ObservableCollection<DashboardSession> DailySessions { get; } = [];
    public ObservableCollection<DashboardTimelineItem> Timeline { get; } = [];
    public ObservableCollection<DashboardTimelineTick> TimelineTicks { get; } = [];
    public ObservableCollection<DashboardLegendItem> TimelineLegend { get; } = [];
    public double TimelineWidth => timelineWidth;
    public void ResizeTimeline(double width)
    {
        if (!double.IsFinite(width) || width < 1 || Math.Abs(width - timelineWidth) < 1) return;
        timelineWidth = width;
        if (report is not null) ApplyTimelineGeometry(report);
        OnChanged(nameof(TimelineWidth));
    }
    public ObservableCollection<DashboardApplication> Chart { get; } = [];
    public DashboardGrouping Grouping
    {
        get => grouping;
        set { if (grouping == value) return; grouping = value; _ = RefreshAsync(); OnChanged(); }
    }
    public bool ShowZeroApplications
    {
        get => showZeroApplications;
        set { if (showZeroApplications == value) return; showZeroApplications = value; ApplyFilter(); OnChanged(); }
    }
    public string WorkDays => (report?.Reports.Select(x => x.WorkDay.Date).Distinct().Count() ?? 0).ToString(CultureInfo.InvariantCulture);
    public string Search
    {
        get => search;
        set { if (search == value) return; search = value ?? ""; OnChanged(); ApplyFilter(); }
    }
    public string Message { get => message; set { message = value; OnChanged(); } }
    public bool Loading { get => loading; private set { loading = value; OnChanged(); } }
    public string PeriodLabel
    {
        get
        {
            var culture = UiCulture.Current;
            var (from, to) = Range();
            return period switch
            {
                DashboardPeriod.Day => date.ToString("D", culture),
                DashboardPeriod.Week => string.Format(culture, DashboardText.Get("WeekHeading"),
                    from.ToDateTime(TimeOnly.MinValue).ToString("d MMM", culture),
                    to.ToDateTime(TimeOnly.MinValue).ToString("d MMM yyyy", culture)),
                DashboardPeriod.Custom => $"{from.ToDateTime(TimeOnly.MinValue).ToString("d", culture)} – {to.ToDateTime(TimeOnly.MinValue).ToString("d", culture)}",
                _ => date.ToString("MMMM yyyy", culture)
            };
        }
    }
    public DateTime SelectedDate
    {
        get => date;
        set
        {
            if (date.Date == value.Date) return;
            date = value.Date;
            ClearReport(); OnChanged(); OnChanged(nameof(PeriodLabel)); _ = RefreshAsync();
        }
    }
    public bool IsDaily => period == DashboardPeriod.Day;
    public bool IsPeriod => !IsDaily;
    public bool IsCustom => period == DashboardPeriod.Custom;
    public string TimelineStart => report?.Reports.Count > 0 ? report.Reports.Min(x => x.WorkDay.StartUtc).ToLocalTime().ToString("HH:mm") : "\u2014";
    public string TimelineEnd => report?.Reports.Count > 0 ? report.Reports.Max(x => x.EffectiveEndUtc).ToLocalTime().ToString("HH:mm") : "\u2014";
    public string Start => report?.Reports.Count > 0 ? report.Reports.Min(x => x.WorkDay.StartUtc).ToLocalTime().ToString("dd/MM HH:mm") : "\u2014";
    public string End => report?.Reports.Count > 0
        ? report.Reports.Any(x => x.WorkDay.EndUtc is null) ? $"{DashboardText.Get("Ongoing")} · {report.Reports.Max(x => x.EffectiveEndUtc).ToLocalTime():HH:mm:ss}" : report.Reports.Max(x => x.EffectiveEndUtc).ToLocalTime().ToString("dd/MM HH:mm")
        : "\u2014";
    public string Active => Duration(report?.Reports.Aggregate(TimeSpan.Zero, (a, x) => a + x.TotalActiveTime) ?? TimeSpan.Zero);
    public string Displayed => Duration(report?.Reports.Aggregate(TimeSpan.Zero, (a, x) => a + x.TotalDisplayedTime) ?? TimeSpan.Zero);
    public string Away => Duration(report?.Reports.Aggregate(TimeSpan.Zero, (a, x) => a + x.AwayTime) ?? TimeSpan.Zero);
    public string Span => Duration(report?.Reports.Aggregate(TimeSpan.Zero, (a, x) => a + x.TotalSpan) ?? TimeSpan.Zero);
    public string NotTracked => Duration(report?.Reports.Aggregate(TimeSpan.Zero, (a, x) => a + x.NotTrackedTime) ?? TimeSpan.Zero);
    public string PoweredOff => Duration(report?.Timeline.Where(x => x.Kind == TimelineKind.PoweredOff).Aggregate(TimeSpan.Zero, (a, x) => a + (x.End - x.Start)) ?? TimeSpan.Zero);
    public bool IsEmpty => !Loading && report is { Reports.Count: 0 };
    public bool HasReportData => report is { Reports.Count: > 0 };
    public bool HasLiveDay => report?.Reports.Any(x => x.WorkDay.EndUtc is null && x.WorkDay.Date == DateOnly.FromDateTime(DateTime.Today)) == true;
    public bool IsLivePeriod
    {
        get { var (from, to) = Range(); var today = DateOnly.FromDateTime(DateTime.Today); return from <= today && today <= to; }
    }
    public bool HasDailyData => IsDaily && HasReportData;
    public bool HasPeriodData => IsPeriod && HasReportData;
    public bool IsManuallyEdited => IsDaily && report?.ReportDays.Any(x => x.Edit is not null) == true;
    public string OriginalTimes => string.Join("\n", (report?.ReportDays ?? [])
        .Where(x => x.Edit is not null)
        .Select(x => $"{x.Original.WorkDay.StartUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss} – " +
            (x.Original.WorkDay.EndUtc is { } end ? end.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") : DashboardText.Get("Ongoing"))));
    public bool NoResults => !Loading && report is { Reports.Count: > 0 } && Applications.Count == 0;
    public DashboardReport? Report => report;

    public void SetPeriod(DashboardPeriod value) { period = value; ClearReport(); OnChanged(nameof(IsDaily)); OnChanged(nameof(IsPeriod)); OnChanged(nameof(IsCustom)); OnChanged(nameof(PeriodLabel)); _ = RefreshAsync(); }
    public void SetCustomRange(DateTime from, DateTime to)
    {
        if (from.Date > to.Date) throw new ArgumentException("The start date must not be after the end date.");
        customFrom = from.Date;
        customTo = to.Date;
        if (IsCustom) { ClearReport(); OnChanged(nameof(PeriodLabel)); _ = RefreshAsync(); }
    }
    public void Navigate(int delta)
    {
        date = period switch { DashboardPeriod.Day => date.AddDays(delta), DashboardPeriod.Week => date.AddDays(delta * 7), _ => date.AddMonths(delta) };
        ClearReport(); OnChanged(nameof(PeriodLabel)); OnChanged(nameof(SelectedDate));
        _ = RefreshAsync();
    }
    public void Today() { date = DateTime.Today; ClearReport(); OnChanged(nameof(PeriodLabel)); OnChanged(nameof(SelectedDate)); _ = RefreshAsync(); }
    public void CancelPending() => Interlocked.Increment(ref revision);

    public async Task RefreshAsync()
    {
        var current = Interlocked.Increment(ref revision);
        var (from, to) = Range();
        Loading = true;
        OnChanged(nameof(IsEmpty)); OnChanged(nameof(NoResults));
        try
        {
            var snapshotSettings = settings();
            var selectedGrouping = grouping;
            await loadGate.WaitAsync();
            DashboardReport result;
            try
            {
                if (current != Interlocked.Read(ref revision)) return;
                WorkDayRawData? live = null;
                long? liveId = null;
                if (tracker is not null && IsLivePeriod)
                {
                    var service = tracker();
                    var status = service.Status;
                    if (status.WorkDayId is { } id && status.WorkDay is { EndUtc: null } day &&
                        day.Date >= from && day.Date <= to && status.State is TrackingState.Tracking or TrackingState.Paused or TrackingState.Unavailable)
                    {
                        live = await service.GetSnapshotAsync(id);
                        liveId = id;
                    }
                }
                result = await Task.Run(() => DashboardReport.LoadLive(databasePath, from, to, snapshotSettings, selectedGrouping, liveId, live, calculations));
            }
            finally { loadGate.Release(); }
            if (current != Interlocked.Read(ref revision)) return;
            ReportApplying?.Invoke();
            report = result;
            Replace(Days, result.Days);
            Replace(DailySessions, result.Reports.OrderBy(x => x.WorkDay.StartUtc).Select(DashboardSession.FromReport));
            Replace(Timeline, result.Timeline);
            Replace(TimelineTicks, result.TimelineTicks);
            Replace(TimelineLegend, result.TimelineLegend);
            ApplyTimelineGeometry(result);
            Replace(Chart, result.Applications);
            ApplyFilter();
            NotifyReport();
            ReportApplied?.Invoke();
            Message = "";
        }
        catch (Exception error)
        {
            var logged = DashboardLog.Write(error);
            if (current == Interlocked.Read(ref revision))
            {
                report = null;
                Replace(Days, Array.Empty<DashboardDay>());
                Replace(DailySessions, Array.Empty<DashboardSession>());
                Replace(Timeline, Array.Empty<DashboardTimelineItem>());
                Replace(TimelineTicks, Array.Empty<DashboardTimelineTick>());
                Replace(TimelineLegend, Array.Empty<DashboardLegendItem>());
                Replace(Chart, Array.Empty<DashboardApplication>());
                ApplyFilter();
                NotifyReport();
                Message = DashboardText.Get(logged ? "Error" : "LogUnavailable");
            }
        }
        finally
        {
            if (current == Interlocked.Read(ref revision)) { Loading = false; OnChanged(nameof(IsEmpty)); OnChanged(nameof(NoResults)); }
        }
    }

    private (DateOnly From, DateOnly To) Range()
    {
        if (IsCustom) return (DateOnly.FromDateTime(customFrom), DateOnly.FromDateTime(customTo));
        var day = date.Date;
        var start = period switch
        {
            DashboardPeriod.Week => day.AddDays(-((7 + (int)day.DayOfWeek - (int)UiCulture.FirstDay(settings().WeekStart)) % 7)),
            DashboardPeriod.Month => new DateTime(day.Year, day.Month, 1),
            _ => day
        };
        var end = period switch { DashboardPeriod.Week => start.AddDays(6), DashboardPeriod.Month => start.AddMonths(1).AddDays(-1), _ => start };
        return (DateOnly.FromDateTime(start), DateOnly.FromDateTime(end));
    }

    private void ApplyTimelineGeometry(DashboardReport value)
    {
        var seconds = (value.AxisEnd - value.AxisStart).TotalSeconds;
        if (seconds <= 0) return;
        Replace(Timeline, value.Timeline.Select(item => item with
        {
            Left = timelineWidth * (UiCulture.IsRightToLeft
                ? (value.AxisEnd - item.End).TotalSeconds : (item.Start - value.AxisStart).TotalSeconds) / seconds,
            Width = timelineWidth * (item.End - item.Start).TotalSeconds / seconds
        }));
        Replace(TimelineTicks, value.TimelineTicks.Select(tick => tick with
        { Left = timelineWidth * (UiCulture.IsRightToLeft
            ? (value.AxisEnd - tick.Time).TotalSeconds : (tick.Time - value.AxisStart).TotalSeconds) / seconds }));
    }

    private void ApplyFilter()
    {
        var query = search.Trim();
        var filtered = (report?.Applications ?? []).Where(x => ShowZeroApplications || x.DisplayedTime >= TimeSpan.FromMinutes(1) || x.ActiveTime >= TimeSpan.FromMinutes(1)).Select(x => x.Filter(query, ShowZeroApplications)).Where(x => x is not null).Cast<DashboardApplication>().ToArray();
        Replace(Applications, filtered);
        Replace(Chart, filtered);
        OnChanged(nameof(NoResults));
    }
    private void ClearReport()
    {
        report = null;
        Replace(Days, Array.Empty<DashboardDay>());
        Replace(DailySessions, Array.Empty<DashboardSession>());
        Replace(Timeline, Array.Empty<DashboardTimelineItem>());
        Replace(TimelineTicks, Array.Empty<DashboardTimelineTick>());
        Replace(TimelineLegend, Array.Empty<DashboardLegendItem>());
        Replace(Chart, Array.Empty<DashboardApplication>());
        ApplyFilter();
        NotifyReport();
    }
    private void NotifyReport()
    {
        foreach (var name in new[] { nameof(Start), nameof(End), nameof(Active), nameof(Displayed), nameof(Away), nameof(Span), nameof(NotTracked), nameof(PoweredOff), nameof(WorkDays), nameof(Report), nameof(TimelineStart), nameof(TimelineEnd), nameof(HasReportData), nameof(HasLiveDay), nameof(HasDailyData), nameof(HasPeriodData), nameof(IsManuallyEdited), nameof(OriginalTimes) }) OnChanged(name);
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    internal static string Duration(TimeSpan value) => $"{(long)value.TotalHours}:{value.Minutes:00}";
}

internal sealed record DashboardTitle(string Name, TimeSpan ActiveTime, TimeSpan DisplayedTime, string Label = "")
{
    public string DisplayName => $"\u2068{Name}\u2069";
    public string Active => DashboardViewModel.Duration(ActiveTime);
    public string Displayed => DashboardViewModel.Duration(DisplayedTime);
}

internal sealed record DashboardLabel(string Name, TimeSpan ActiveTime, TimeSpan DisplayedTime, IReadOnlyList<DashboardTitle> Titles)
{
    public string DisplayName => $"\u2068{Name}\u2069";
    public string Active => DashboardViewModel.Duration(ActiveTime);
    public string Displayed => DashboardViewModel.Duration(DisplayedTime);
}

internal sealed record DashboardApplication(string Key, string Name, string? Path, TimeSpan ActiveTime, TimeSpan DisplayedTime, IReadOnlyList<DashboardTitle> Titles, ImageSource? Icon, Brush Color, double BarWidth)
{
    public string DisplayName => $"\u2068{Name}\u2069";
    public string Active => DashboardViewModel.Duration(ActiveTime);
    public string Displayed => DashboardViewModel.Duration(DisplayedTime);
    public IReadOnlyList<DashboardLabel> Labels { get; init; } = [];
    public bool ShowLabelGroups { get; init; }
    public bool ShowTitleRows { get; init; } = true;
    public bool CanOpenApplication { get; init; } = true;
    public bool IsForeground { get; init; }
    public string ActiveDetail => $"{Name} \u00B7 {(long)ActiveTime.TotalHours}:{ActiveTime.Minutes:00}:{ActiveTime.Seconds:00}.{ActiveTime.Ticks % TimeSpan.TicksPerSecond:0000000}";
    public DashboardApplication? Filter(string query, bool showZero = true)
    {
        static bool HasTime(TimeSpan active, TimeSpan displayed) =>
            active >= TimeSpan.FromMinutes(1) || displayed >= TimeSpan.FromMinutes(1);
        var titles = Titles.Where(title => showZero || HasTime(title.ActiveTime, title.DisplayedTime)).ToArray();
        var groups = Labels.Where(label => showZero || HasTime(label.ActiveTime, label.DisplayedTime))
            .Select(label => label with { Titles = label.Titles.Where(title => showZero || HasTime(title.ActiveTime, title.DisplayedTime)).ToArray() }).ToArray();
        if (query.Length == 0 || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            return this with { Titles = titles, Labels = groups };
        var matching = titles.Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || x.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var labels = groups.Select(label => label with
        { Titles = label.Titles.Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray() })
            .Where(label => label.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || label.Titles.Count > 0).ToArray();
        return matching.Length == 0 && labels.Length == 0 ? null : this with { Titles = matching, Labels = labels };
    }
}

internal sealed record DashboardDay(DateOnly Date, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, TimeSpan ActiveTime, TimeSpan DisplayedTime, TimeSpan AwayTime, TimeSpan SpanTime, bool HasData = true, bool IsTotal = false)
{
    public IReadOnlyList<DashboardSession> Sessions { get; init; } = [];
    public bool HasSessions => Sessions.Count > 0;
    public string DateText => IsTotal ? DashboardText.Get("Total") : $"{Date.ToString("ddd", UiCulture.Current)} {Date.ToString("d", UiCulture.Current)}";
    public bool IsWeekend => !IsTotal && (UiCulture.IsRightToLeft
        ? Date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday
        : Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    public string Start => StartUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "\u2014";
    public string End => !HasData || IsTotal ? "\u2014" : EndUtc is { } end ? end.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : DashboardText.Get("Ongoing");
    public string Active => DashboardViewModel.Duration(ActiveTime);
    public string Displayed => DashboardViewModel.Duration(DisplayedTime);
    public string Away => DashboardViewModel.Duration(AwayTime);
    public string Span => DashboardViewModel.Duration(SpanTime);
}

internal sealed record DashboardSession(DateTimeOffset StartUtc, DateTimeOffset? EndUtc, TimeSpan ActiveTime, TimeSpan DisplayedTime, TimeSpan AwayTime, TimeSpan SpanTime, TimeSpan NotTrackedTime, TimeSpan PoweredOffTime)
{
    public static DashboardSession FromReport(DayReport report) => new(report.WorkDay.StartUtc, report.WorkDay.EndUtc, report.TotalActiveTime, report.TotalDisplayedTime, report.AwayTime, report.TotalSpan, report.NotTrackedTime, report.PoweredOffTime);
    public string Start => StartUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public string End => EndUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? DashboardText.Get("Ongoing");
    public string Span => DashboardViewModel.Duration(SpanTime);
    public string Active => DashboardViewModel.Duration(ActiveTime);
    public string Displayed => DashboardViewModel.Duration(DisplayedTime);
    public string Away => DashboardViewModel.Duration(AwayTime);
}

internal enum TimelineKind { Application, Away, Untracked, PoweredOff }
internal sealed record DashboardTimelineItem(string Label, DateTimeOffset Start, DateTimeOffset End, Brush Color)
{
    public TimelineKind Kind { get; init; }
    public bool IsAway => Kind == TimelineKind.Away;
    public bool IsUntracked => Kind == TimelineKind.Untracked;
    public bool IsPoweredOff => Kind == TimelineKind.PoweredOff;
    public string Interval => $"{Start.ToLocalTime():HH:mm}\u2013{End.ToLocalTime():HH:mm}";
    public string Duration => DashboardViewModel.Duration(End - Start);
    public string Description => $"{Label} \u00B7 {Interval} \u00B7 {Duration}";
    public double Left { get; init; }
    public double Width { get; init; }
}

internal sealed record DashboardTimelineTick(DateTimeOffset Time, string Label) { public double Left { get; init; } }
internal sealed record DashboardLegendItem(string Label, Brush Color)
{
    public bool IsAway { get; init; }
    public bool IsUntracked { get; init; }
    public bool IsPoweredOff { get; init; }
}

internal sealed record ReportDay(StoredWorkDay Original, WorkDayEdit? Edit);

internal sealed record DashboardReport(DateOnly From, DateOnly To, IReadOnlyList<DayReport> Reports, IReadOnlyList<DashboardApplication> Applications, IReadOnlyList<DashboardDay> Days, IReadOnlyList<DashboardTimelineItem> Timeline, DashboardGrouping Grouping, DateTimeOffset AxisStart, DateTimeOffset AxisEnd, IReadOnlyList<DashboardTimelineTick> TimelineTicks, IReadOnlyList<DashboardLegendItem> TimelineLegend, IReadOnlyList<ReportDay> ReportDays)
{
    public static DashboardReport Load(string path, DateOnly from, DateOnly to, AppSettings settings, DashboardGrouping grouping = DashboardGrouping.AppLabelTitle) =>
        LoadLive(path, from, to, settings, grouping, null, null, null);

    internal static DashboardReport LoadLive(string path, DateOnly from, DateOnly to, AppSettings settings, DashboardGrouping grouping,
        long? liveId, WorkDayRawData? live, Dictionary<long, IncrementalCalculationEngine>? calculations)
    {
        using var repository = new SqliteWorkTimeRepository(path);
        var now = DateTimeOffset.UtcNow;
        var reportDays = repository.ListWorkDays(from, to).Select(x => new ReportDay(x, repository.GetWorkDayEdit(x.Id))).ToArray();
        var raw = reportDays.Select(item =>
        {
            var data = item.Original.Id == liveId && live is not null ? live : TrackingRawDataReader.GetRawData(path, item.Original.Id);
            return item.Edit is { } edit
                ? data with { WorkDay = data.WorkDay with { StartUtc = edit.StartUtc, EndUtc = edit.EndUtc ?? data.WorkDay.EndUtc } }
                : data;
        }).ToArray();
        var reports = raw.Select((x, index) =>
        {
            if (calculations is null) return CalculationEngine.Calculate(x, settings.Tracker, now);
            var id = reportDays[index].Original.Id;
            if (!calculations.TryGetValue(id, out var calculation))
                calculations.Add(id, calculation = new IncrementalCalculationEngine());
            return calculation.Calculate(x, settings.Tracker, now);
        }).ToArray();
        var dayRows = reports.GroupBy(x => x.WorkDay.Date).ToDictionary(g => g.Key, g => new DashboardDay(g.Key,
            g.Min(x => (DateTimeOffset?)x.WorkDay.StartUtc), g.Any(x => x.WorkDay.EndUtc is null) ? null : g.Max(x => x.WorkDay.EndUtc),
            Sum(g.Select(x => x.TotalActiveTime)), Sum(g.Select(x => x.TotalDisplayedTime)), Sum(g.Select(x => x.AwayTime)), Sum(g.Select(x => x.TotalSpan)))
            { Sessions = g.OrderBy(x => x.WorkDay.StartUtc).Select(DashboardSession.FromReport).ToArray() });
        var allDays = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(offset => from.AddDays(offset))
            .Select(day => dayRows.TryGetValue(day, out var item) ? item : new DashboardDay(day, null, null, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, false)).ToList();
        allDays.Add(new DashboardDay(to, null, null, Sum(reports.Select(x => x.TotalActiveTime)), Sum(reports.Select(x => x.TotalDisplayedTime)), Sum(reports.Select(x => x.AwayTime)), Sum(reports.Select(x => x.TotalSpan)), true, true));
        var days = allDays.ToArray();
        var entries = reports.SelectMany(x => x.Windows).Select(metric =>
        {
            var key = AppKey(metric.Window);
            var name = PackageAppearance.ForExecutable(metric.Window.ExecutablePath).Name
                ?? (string.IsNullOrWhiteSpace(metric.Window.FriendlyName) ? metric.Window.ProcessName : metric.Window.FriendlyName);
            if (string.IsNullOrWhiteSpace(name)) name = DashboardText.Get("NoApplication");
            var normalized = TitleNormalizer.Normalize(metric.Window.Title, settings.TitleNormalizationRules);
            var title = string.IsNullOrWhiteSpace(normalized) ? DashboardText.Get("NoTitle") : normalized;
            var label = LabelExtractor.Extract(metric.Window with { Title = normalized }, settings.LabelRules);
            if (string.IsNullOrWhiteSpace(label)) label = title;
            return (Metric: metric, Key: key, AppName: name, Title: title, Label: label);
        }).ToArray();
        var groupingGroups = grouping == DashboardGrouping.Label
            ? entries.GroupBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).Select(g => (Key: g.Key, Name: g.Key, Entries: g.ToArray())).ToArray()
            : entries.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(g => (Key: g.Key, Name: grouping == DashboardGrouping.App ? g.First().AppName : g.First().AppName, Entries: g.ToArray())).ToArray();
        var max = groupingGroups.Select(g => Sum(g.Entries.Select(x => x.Metric.ActiveTime)).TotalSeconds).DefaultIfEmpty(0).Max();
        ImageSource? ApplicationIcon(WindowInfo window) =>
            PackageAppearance.ForExecutable(window.ExecutablePath).Icon ?? LoadIcon(window.ExecutablePath) ??
            entries.Where(entry => string.Equals(entry.Metric.Window.ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase))
                .Select(entry => PackageAppearance.ForExecutable(entry.Metric.Window.ExecutablePath).Icon)
                .FirstOrDefault(icon => icon is not null);
        var foregroundId = live?.VisibilitySegments
            .Where(x => x.IsForeground && x.EndUtc <= now && now - x.EndUtc <= settings.Tracker.SampleInterval)
            .OrderByDescending(x => x.EndUtc).FirstOrDefault()?.WindowId;
        var apps = groupingGroups.Select(group =>
        {
            var first = group.Entries[0].Metric.Window;
            var labels = grouping == DashboardGrouping.AppLabelTitle
                ? group.Entries.GroupBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).Select(label =>
                {
                    var titlesForLabel = label.GroupBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                        .Select(title => new DashboardTitle(title.Key, Sum(title.Select(x => x.Metric.ActiveTime)),
                            Sum(title.Select(x => x.Metric.DisplayedTime)), label.Key))
                        .OrderByDescending(title => title.ActiveTime).ThenBy(title => title.Name).ToArray();
                    return new DashboardLabel(label.Key, Sum(label.Select(x => x.Metric.ActiveTime)),
                        Sum(label.Select(x => x.Metric.DisplayedTime)), titlesForLabel);
                }).OrderBy(label => label.Name).ToArray()
                : Array.Empty<DashboardLabel>();
            var titles = grouping == DashboardGrouping.App
                ? Array.Empty<DashboardTitle>()
                : group.Entries.GroupBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                    .Select(title => new DashboardTitle(title.Key, Sum(title.Select(x => x.Metric.ActiveTime)),
                        Sum(title.Select(x => x.Metric.DisplayedTime)), grouping == DashboardGrouping.Label ? group.Name : title.First().Label))
                    .OrderByDescending(title => title.ActiveTime).ThenBy(title => title.Name).ToArray();
            var active = Sum(group.Entries.Select(x => x.Metric.ActiveTime));
            return new DashboardApplication(group.Key, group.Name, first.ExecutablePath, active,
                Sum(group.Entries.Select(x => x.Metric.DisplayedTime)), titles, ApplicationIcon(first), AppColor(group.Key),
                max > 0 ? 260 * active.TotalSeconds / max : 0)
            { Labels = labels, ShowLabelGroups = grouping == DashboardGrouping.AppLabelTitle, ShowTitleRows = grouping != DashboardGrouping.AppLabelTitle, CanOpenApplication = grouping != DashboardGrouping.Label,
                IsForeground = foregroundId is not null && group.Entries.Any(x => x.Metric.Window.Id == foregroundId) };
        }).OrderByDescending(x => x.ActiveTime).ThenBy(x => x.Name).ToArray();
        var axisStart = reports.Length > 0 ? reports.Min(x => x.WorkDay.StartUtc) : now;
        var axisEnd = reports.Length > 0 ? reports.Max(x => x.EffectiveEndUtc) : now;
        var interSessionOff = TrackingLifecycleStore.ReadPowerOffGaps(path, reportDays.Select(x => x.Original.Id))
            .Where(off => reports.Any(day => day.WorkDay.EndUtc is { } end && end <= off.StartUtc) &&
                reports.Any(day => day.WorkDay.StartUtc >= off.EndUtc) && off.StartUtc >= axisStart && off.EndUtc <= axisEnd)
            .Select(off => new DashboardTimelineItem(DashboardText.Get("PoweredOff"), off.StartUtc, off.EndUtc, SystemColorsBrush()) { Kind = TimelineKind.PoweredOff });
        var timelineItems = raw.SelectMany(x => BuildTimeline(x, settings.Tracker, now, settings.LabelRules, settings.TitleNormalizationRules))
            .Concat(interSessionOff).OrderBy(x => x.Start).ToArray();
        var axisSeconds = (axisEnd - axisStart).TotalSeconds;
        var timeline = timelineItems.Select(item => item with
        {
            Left = axisSeconds > 0 ? 960 * (axisEnd - item.End).TotalSeconds / axisSeconds : 0,
            Width = axisSeconds > 0 ? 960 * (item.End - item.Start).TotalSeconds / axisSeconds : 0
        }).ToArray();
        var ticks = new List<DashboardTimelineTick>();
        if (axisSeconds > 0)
        {
            var tick = new DateTimeOffset(axisStart.UtcDateTime.Date.AddHours(axisStart.UtcDateTime.Hour + 1), TimeSpan.Zero);
            while (tick < axisEnd && ticks.Count < 48)
            {
                ticks.Add(new DashboardTimelineTick(tick, tick.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)));
                tick = tick.AddHours(1);
            }
        }
        var legend = timeline.GroupBy(item => item.Kind switch
            {
                TimelineKind.Away => "\0away",
                TimelineKind.Untracked => "\0untracked",
                TimelineKind.PoweredOff => "\0poweredoff",
                _ => item.Label.Split('\u00B7')[0].Trim()
            }, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key switch
            {
                "\0away" => new DashboardLegendItem(DashboardText.Get("Away"), SystemColorsBrush()) { IsAway = true },
                "\0untracked" => new DashboardLegendItem(DashboardText.Get("NotTracked"), SystemColorsBrush()) { IsUntracked = true },
                "\0poweredoff" => new DashboardLegendItem(DashboardText.Get("PoweredOff"), SystemColorsBrush()) { IsPoweredOff = true },
                _ => new DashboardLegendItem(group.First().Label.Split('\u00B7')[0].Trim(), group.First().Color)
            })
            .ToArray();
        return new DashboardReport(from, to, reports, apps, days, timeline, grouping, axisStart, axisEnd, ticks, legend, reportDays);
    }

    internal static string AppKey(WindowInfo w)
    {
        var path = w.ExecutablePath ?? "";
        var marker = path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            var packageStart = marker + @"\WindowsApps\".Length;
            var packageEnd = path.IndexOf('\\', packageStart);
            if (packageEnd > packageStart)
            {
                var parts = path[packageStart..packageEnd].Split('_');
                if (parts.Length == 5 && Version.TryParse(parts[1], out _) && parts[4].Length > 0)
                    path = path[..packageStart] + parts[0] + "_" + parts[3] + "_" + parts[4] + path[packageEnd..];
            }
        }
        return path + "|" + w.ProcessName;
    }
    private static TimeSpan Sum(IEnumerable<TimeSpan> values) => values.Aggregate(TimeSpan.Zero, (a, b) => a + b);

    private static Brush AppColor(string key)
    {
        uint hash = 2166136261;
        foreach (var c in key.ToUpperInvariant()) hash = (hash ^ c) * 16777619;
        var hue = hash % 360 / 60.0;
        var x = 1 - Math.Abs(hue % 2 - 1);
        var (r, g, b) = hue switch { < 1 => (1.0, x, 0.0), < 2 => (x, 1.0, 0.0), < 3 => (0.0, 1.0, x), < 4 => (0.0, x, 1.0), < 5 => (x, 0.0, 1.0), _ => (1.0, 0.0, x) };
        var brush = new SolidColorBrush(Color.FromRgb((byte)(70 + r * 140), (byte)(70 + g * 140), (byte)(70 + b * 140)));
        brush.Freeze();
        return brush;
    }

    internal static ImageSource? LoadIcon(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var root = Path.GetPathRoot(path);
            if (root is null || new DriveInfo(root).DriveType != DriveType.Fixed || !File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException) { DashboardLog.Write(ex); return null; }
    }

    private static IEnumerable<DashboardTimelineItem> BuildTimeline(WorkDayRawData raw, TrackerSettings settings, DateTimeOffset now,
        IReadOnlyList<LabelRule> labelRules, IReadOnlyList<TitleNormalizationRule>? titleRules = null)
    {
        var end = raw.WorkDay.EndUtc ?? now;
        var cursor = raw.WorkDay.StartUtc;
        var windows = raw.Windows.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var foreground = raw.VisibilitySegments.Where(x => x.IsForeground).OrderBy(x => x.StartUtc).ToArray();
        var inputs = raw.Interactions.Where(x => x.Kind != InteractionKind.None).OrderBy(x => x.SecondUtc).ToArray();
        var events = raw.SessionEvents.OrderBy(x => x.TimeUtc).ToArray();
        var globalInputs = raw.GlobalInputs.OrderBy(x => x.ObservedUtc).ToArray();
        var tracking = TrackingAvailability.GetNotTrackedIntervals(raw, end);
        var poweredOff = TrackingAvailability.GetPoweredOffIntervals(raw, end);
        var activity = InputActivity.Create(raw, end);
        var i = 0; var s = 0; var e = 0; var locked = false; var suspended = false; var loggedOff = false;
        var t = 0; var o = 0; var g = 0; var activityIndex = 0;
        var recordingSince = cursor;
        DateTimeOffset? lastInput = null, runStart = null;
        string? runKey = null, label = null;
        Brush? color = null;
        while (cursor < end)
        {
            while (o < poweredOff.Count && poweredOff[o].EndUtc <= cursor) o++;
            if (o < poweredOff.Count && poweredOff[o].StartUtc <= cursor)
            {
                if (runKey is not null && runStart is not null) yield return new DashboardTimelineItem(label!, runStart.Value, cursor, color!) { Kind = runKey == "\0away" ? TimelineKind.Away : TimelineKind.Application };
                runKey = null;
                yield return new DashboardTimelineItem(DashboardText.Get("PoweredOff"), cursor, poweredOff[o].EndUtc, SystemColorsBrush()) { Kind = TimelineKind.PoweredOff };
                cursor = poweredOff[o].EndUtc;
                recordingSince = cursor;
                lastInput = null;
                continue;
            }
            while (t < tracking.Count && tracking[t].EndUtc <= cursor) t++;
            if (t < tracking.Count && tracking[t].StartUtc <= cursor)
            {
                if (runKey is not null && runStart is not null) yield return new DashboardTimelineItem(label!, runStart.Value, cursor, color!) { Kind = runKey == "\0away" ? TimelineKind.Away : TimelineKind.Application };
                runKey = null;
                yield return new DashboardTimelineItem(DashboardText.Get("NotTracked"), cursor, tracking[t].EndUtc, SystemColorsBrush()) { Kind = TimelineKind.Untracked };
                cursor = tracking[t].EndUtc;
                recordingSince = cursor;
                lastInput = null;
                continue;
            }
            while (e < events.Length && events[e].TimeUtc <= cursor)
            {
                switch (events[e++].Kind)
                {
                    case SessionEventKind.Lock: locked = true; break;
                    case SessionEventKind.Unlock: locked = false; break;
                    case SessionEventKind.Suspend: suspended = true; break;
                    case SessionEventKind.Resume: suspended = false; break;
                    case SessionEventKind.Logoff:
                    case SessionEventKind.Shutdown: loggedOff = true; break;
                    case SessionEventKind.Logon: loggedOff = false; break;
                    case SessionEventKind.Boot: locked = suspended = loggedOff = false; break;
                }
            }
            while (i < inputs.Length && inputs[i].SecondUtc <= cursor)
            {
                var input = inputs[i++];
                if (input.SecondUtc >= recordingSince) lastInput = input.SecondUtc;
            }
            while (g < globalInputs.Length && globalInputs[g].ObservedUtc <= cursor)
            {
                var input = globalInputs[g++];
                if (input.ObservedUtc >= recordingSince && (lastInput is null || input.LastInputUtc > lastInput))
                    lastInput = input.LastInputUtc;
            }
            while (activityIndex < activity.Inputs.Count && activity.Inputs[activityIndex] <= cursor)
            {
                var inputTime = activity.Inputs[activityIndex++];
                if (inputTime >= recordingSince && (lastInput is null || inputTime > lastInput))
                    lastInput = inputTime;
            }
            while (s < foreground.Length && foreground[s].EndUtc <= cursor) s++;
            var presenceEnd = lastInput is { } knownInput ? activity.End(knownInput, settings.AwayTimeout) : cursor;
            var activeEnd = lastInput is { } activeInput ? activity.End(activeInput, settings.ActiveTimeout) : cursor;
            var away = locked || suspended || loggedOff || lastInput is null || cursor >= presenceEnd;
            WindowInfo? window = null;
            if (!away && lastInput is not null && cursor < activeEnd &&
                s < foreground.Length && foreground[s].StartUtc <= cursor && foreground[s].EndUtc > cursor)
                windows.TryGetValue(foreground[s].WindowId, out window);
            var key = away ? "\0away" : window is null ? null : AppKey(window) + "|" + window.Id;
            if (key != runKey)
            {
                if (runKey is not null && runStart is not null) yield return new DashboardTimelineItem(label!, runStart.Value, cursor, color!) { Kind = runKey == "\0away" ? TimelineKind.Away : TimelineKind.Application };
                runKey = key; runStart = cursor;
                var normalized = window is null ? "" : TitleNormalizer.Normalize(window.Title, titleRules ?? TitleNormalizer.DefaultRules);
                label = away ? DashboardText.Get("Away") : window is null ? null : $"\u2068{PackageAppearance.ForExecutable(window.ExecutablePath).Name ?? (string.IsNullOrWhiteSpace(window.FriendlyName) ? window.ProcessName : window.FriendlyName)}\u2069 \u00B7 \u2068{LabelExtractor.Extract(window with { Title = normalized }, labelRules)}\u2069 \u00B7 \u2068{normalized}\u2069";
                if (key is not null && string.IsNullOrWhiteSpace(label)) label = DashboardText.Get("NoApplication");
                color = key is null ? null : away ? SystemColorsBrush() : AppColor(AppKey(window!));
            }
            var duration = end - cursor;
            if (t < tracking.Count && tracking[t].StartUtc - cursor < duration) duration = tracking[t].StartUtc - cursor;
            if (o < poweredOff.Count && poweredOff[o].StartUtc - cursor < duration) duration = poweredOff[o].StartUtc - cursor;
            if (e < events.Length && events[e].TimeUtc - cursor < duration) duration = events[e].TimeUtc - cursor;
            if (i < inputs.Length && inputs[i].SecondUtc - cursor < duration) duration = inputs[i].SecondUtc - cursor;
            if (g < globalInputs.Length && globalInputs[g].ObservedUtc - cursor < duration) duration = globalInputs[g].ObservedUtc - cursor;
            if (activityIndex < activity.Inputs.Count && activity.Inputs[activityIndex] - cursor < duration) duration = activity.Inputs[activityIndex] - cursor;
            if (presenceEnd > cursor && presenceEnd - cursor < duration) duration = presenceEnd - cursor;
            if (activeEnd > cursor && activeEnd - cursor < duration) duration = activeEnd - cursor;
            if (s < foreground.Length)
            {
                var boundary = foreground[s].StartUtc > cursor ? foreground[s].StartUtc : foreground[s].EndUtc;
                if (boundary > cursor && boundary - cursor < duration) duration = boundary - cursor;
            }
            cursor += duration;
        }
        if (runKey is not null && runStart is not null) yield return new DashboardTimelineItem(label!, runStart.Value, end, color!) { Kind = runKey == "\0away" ? TimelineKind.Away : TimelineKind.Application };
    }

    private static Brush SystemColorsBrush() => System.Windows.SystemColors.ControlBrush;
}
