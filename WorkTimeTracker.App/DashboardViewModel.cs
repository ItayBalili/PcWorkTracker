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
using WorkTimeTracker.Data;

namespace WorkTimeTracker.App;

internal enum DashboardPeriod { Day, Week, Month }

internal sealed class DashboardViewModel : INotifyPropertyChanged
{
    private readonly string databasePath;
    private readonly Func<TrackerSettings> settings;
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private long revision;
    private DateTime date = DateTime.Today;
    private DashboardPeriod period;
    private string search = "";
    private string message = "";
    private bool loading;
    private DashboardReport? report;

    public DashboardViewModel(string databasePath, Func<TrackerSettings> settings)
    {
        this.databasePath = databasePath;
        this.settings = settings;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<DashboardApplication> Applications { get; } = [];
    public ObservableCollection<DashboardDay> Days { get; } = [];
    public ObservableCollection<DashboardTimelineItem> Timeline { get; } = [];
    public ObservableCollection<DashboardApplication> Chart { get; } = [];
    public string Search
    {
        get => search;
        set { if (search == value) return; search = value ?? ""; OnChanged(); ApplyFilter(); }
    }
    public string Message { get => message; set { message = value; OnChanged(); } }
    public bool Loading { get => loading; private set { loading = value; OnChanged(); } }
    public string PeriodLabel => $"{Range().From:dd/MM/yyyy} – {Range().To:dd/MM/yyyy}";
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
    public string TimelineStart => report?.Reports.Count > 0 ? report.Reports.Min(x => x.WorkDay.StartUtc).ToLocalTime().ToString("dd/MM HH:mm:ss") : "—";
    public string TimelineEnd => report?.Reports.Count > 0 ? report.Reports.Max(x => x.EffectiveEndUtc).ToLocalTime().ToString("dd/MM HH:mm:ss") : "—";
    public string Start => report?.Reports.Count > 0 ? report.Reports.Min(x => x.WorkDay.StartUtc).ToLocalTime().ToString("dd/MM HH:mm") : "—";
    public string End => report?.Reports.Count > 0
        ? report.Reports.Any(x => x.WorkDay.EndUtc is null) ? DashboardText.Get("Ongoing") : report.Reports.Max(x => x.EffectiveEndUtc).ToLocalTime().ToString("dd/MM HH:mm")
        : "—";
    public string Active => Duration(report?.Reports.Aggregate(TimeSpan.Zero, (a, x) => a + x.TotalActiveTime) ?? TimeSpan.Zero);
    public string Displayed => Duration(report?.Reports.Aggregate(TimeSpan.Zero, (a, x) => a + x.TotalDisplayedTime) ?? TimeSpan.Zero);
    public string Away => Duration(report?.Reports.Aggregate(TimeSpan.Zero, (a, x) => a + x.AwayTime) ?? TimeSpan.Zero);
    public string Span => Duration(report?.Reports.Aggregate(TimeSpan.Zero, (a, x) => a + x.TotalSpan) ?? TimeSpan.Zero);
    public string NotTracked => Duration(report?.Reports.Aggregate(TimeSpan.Zero, (a, x) => a + x.NotTrackedTime) ?? TimeSpan.Zero);
    public bool IsEmpty => !Loading && report is { Reports.Count: 0 };
    public bool NoResults => !Loading && report is { Reports.Count: > 0 } && Applications.Count == 0;
    public DashboardReport? Report => report;

    public void SetPeriod(DashboardPeriod value) { period = value; ClearReport(); OnChanged(nameof(IsDaily)); OnChanged(nameof(IsPeriod)); OnChanged(nameof(PeriodLabel)); _ = RefreshAsync(); }
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
            await loadGate.WaitAsync();
            DashboardReport result;
            try
            {
                if (current != Interlocked.Read(ref revision)) return;
                result = await Task.Run(() => DashboardReport.Load(databasePath, from, to, snapshotSettings));
            }
            finally { loadGate.Release(); }
            if (current != Interlocked.Read(ref revision)) return;
            report = result;
            Replace(Days, result.Days);
            Replace(Timeline, result.Timeline);
            Replace(Chart, result.Applications);
            ApplyFilter();
            NotifyReport();
            Message = "";
        }
        catch (Exception error)
        {
            var logged = DashboardLog.Write(error);
            if (current == Interlocked.Read(ref revision))
            {
                report = null;
                Replace(Days, Array.Empty<DashboardDay>());
                Replace(Timeline, Array.Empty<DashboardTimelineItem>());
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
        var day = date.Date;
        var start = period switch
        {
            DashboardPeriod.Week => day.AddDays(-(int)day.DayOfWeek),
            DashboardPeriod.Month => new DateTime(day.Year, day.Month, 1),
            _ => day
        };
        var end = period switch { DashboardPeriod.Week => start.AddDays(6), DashboardPeriod.Month => start.AddMonths(1).AddDays(-1), _ => start };
        return (DateOnly.FromDateTime(start), DateOnly.FromDateTime(end));
    }

    private void ApplyFilter()
    {
        var query = search.Trim();
        var filtered = (report?.Applications ?? []).Select(x => x.Filter(query)).Where(x => x is not null).Cast<DashboardApplication>();
        Replace(Applications, filtered);
        OnChanged(nameof(NoResults));
    }
    private void ClearReport()
    {
        report = null;
        Replace(Days, Array.Empty<DashboardDay>());
        Replace(Timeline, Array.Empty<DashboardTimelineItem>());
        Replace(Chart, Array.Empty<DashboardApplication>());
        ApplyFilter();
        NotifyReport();
    }
    private void NotifyReport()
    {
        foreach (var name in new[] { nameof(Start), nameof(End), nameof(Active), nameof(Displayed), nameof(Away), nameof(Span), nameof(NotTracked), nameof(Report), nameof(TimelineStart), nameof(TimelineEnd) }) OnChanged(name);
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    internal static string Duration(TimeSpan value) => $"{(long)value.TotalHours}:{value.Minutes:00}";
}

internal sealed record DashboardTitle(string Name, TimeSpan ActiveTime, TimeSpan DisplayedTime)
{
    public string Active => DashboardViewModel.Duration(ActiveTime);
    public string Displayed => DashboardViewModel.Duration(DisplayedTime);
}

internal sealed record DashboardApplication(string Key, string Name, string? Path, TimeSpan ActiveTime, TimeSpan DisplayedTime, IReadOnlyList<DashboardTitle> Titles, ImageSource? Icon, Brush Color, double BarWidth)
{
    public string Active => DashboardViewModel.Duration(ActiveTime);
    public string Displayed => DashboardViewModel.Duration(DisplayedTime);
    public string ActiveDetail => $"{Name} · {(long)ActiveTime.TotalHours}:{ActiveTime.Minutes:00}:{ActiveTime.Seconds:00}.{ActiveTime.Ticks % TimeSpan.TicksPerSecond:0000000}";
    public DashboardApplication? Filter(string query)
    {
        if (query.Length == 0 || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return this;
        var matching = Titles.Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        return matching.Length == 0 ? null : this with { Titles = matching };
    }
}

internal sealed record DashboardDay(DateOnly Date, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, TimeSpan ActiveTime, TimeSpan DisplayedTime, TimeSpan AwayTime, TimeSpan SpanTime)
{
    public string DateText => Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    public string Start => StartUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public string End => EndUtc is { } end ? end.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : DashboardText.Get("Ongoing");
    public string Active => DashboardViewModel.Duration(ActiveTime);
    public string Displayed => DashboardViewModel.Duration(DisplayedTime);
    public string Away => DashboardViewModel.Duration(AwayTime);
    public string Span => DashboardViewModel.Duration(SpanTime);
}

internal sealed record DashboardTimelineItem(string Label, DateTimeOffset Start, DateTimeOffset End, Brush Color)
{
    public string Interval => $"{Start.ToLocalTime():dd/MM HH:mm:ss} – {End.ToLocalTime():HH:mm:ss}";
    public string Duration => DashboardViewModel.Duration(End - Start);
    public string Description => $"{Label} · {Interval} · {Duration}";
    public double Left { get; init; }
    public double Width { get; init; }
}

internal sealed record DashboardReport(DateOnly From, DateOnly To, IReadOnlyList<DayReport> Reports, IReadOnlyList<DashboardApplication> Applications, IReadOnlyList<DashboardDay> Days, IReadOnlyList<DashboardTimelineItem> Timeline)
{
    public static DashboardReport Load(string path, DateOnly from, DateOnly to, TrackerSettings settings)
    {
        using var repository = new SqliteWorkTimeRepository(path);
        var now = DateTimeOffset.UtcNow;
        var raw = repository.ListWorkDays(from, to).Select(x => TrackingRawDataReader.GetRawData(path, x.Id)).ToArray();
        var reports = raw.Select(x => CalculationEngine.Calculate(x, settings, now)).ToArray();
        var days = reports.GroupBy(x => x.WorkDay.Date).OrderByDescending(x => x.Key)
            .Select(g => new DashboardDay(g.Key, g.Min(x => x.WorkDay.StartUtc), g.Any(x => x.WorkDay.EndUtc is null) ? null : g.Max(x => x.WorkDay.EndUtc), Sum(g.Select(x => x.TotalActiveTime)), Sum(g.Select(x => x.TotalDisplayedTime)), Sum(g.Select(x => x.AwayTime)), Sum(g.Select(x => x.TotalSpan)))).ToArray();
        var groups = reports.SelectMany(x => x.Windows).GroupBy(x => AppKey(x.Window), StringComparer.OrdinalIgnoreCase).ToArray();
        var max = groups.Select(g => Sum(g.Select(x => x.ActiveTime)).TotalSeconds).DefaultIfEmpty(0).Max();
        var apps = groups.Select(g =>
        {
            var first = g.First().Window;
            var name = string.IsNullOrWhiteSpace(first.FriendlyName) ? first.ProcessName : first.FriendlyName;
            if (string.IsNullOrWhiteSpace(name)) name = DashboardText.Get("NoApplication");
            var active = Sum(g.Select(x => x.ActiveTime));
            var titles = g.GroupBy(x => string.IsNullOrWhiteSpace(x.Window.Title) ? DashboardText.Get("NoTitle") : x.Window.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => new DashboardTitle(t.Key, Sum(t.Select(x => x.ActiveTime)), Sum(t.Select(x => x.DisplayedTime))))
                .OrderByDescending(x => x.ActiveTime).ThenBy(x => x.Name).ToArray();
            return new DashboardApplication(g.Key, name, first.ExecutablePath, active, Sum(g.Select(x => x.DisplayedTime)), titles, LoadIcon(first.ExecutablePath), AppColor(g.Key), max > 0 ? 260 * active.TotalSeconds / max : 0);
        }).OrderByDescending(x => x.ActiveTime).ThenBy(x => x.Name).ToArray();
        var timelineItems = raw.SelectMany(x => BuildTimeline(x, settings, now)).ToArray();
        var axisStart = reports.Length > 0 ? reports.Min(x => x.WorkDay.StartUtc) : now;
        var axisEnd = reports.Length > 0 ? reports.Max(x => x.EffectiveEndUtc) : now;
        var axisSeconds = (axisEnd - axisStart).TotalSeconds;
        var timeline = timelineItems.Select(x => x with
        {
            Left = axisSeconds > 0 ? 960 * (x.Start - axisStart).TotalSeconds / axisSeconds : 0,
            Width = axisSeconds > 0 ? 960 * (x.End - x.Start).TotalSeconds / axisSeconds : 0
        }).ToArray();
        return new DashboardReport(from, to, reports, apps, days, timeline);
    }

    private static string AppKey(WindowInfo w) => (w.ExecutablePath ?? "") + "|" + w.ProcessName;
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

    private static ImageSource? LoadIcon(string? path)
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

    private static IEnumerable<DashboardTimelineItem> BuildTimeline(WorkDayRawData raw, TrackerSettings settings, DateTimeOffset now)
    {
        var end = raw.WorkDay.EndUtc ?? now;
        var cursor = raw.TrackingStartedUtc is { } tracked && tracked > raw.WorkDay.StartUtc ? tracked : raw.WorkDay.StartUtc;
        if (raw.TrackingStartedUtc is null) yield break;
        var windows = raw.Windows.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var foreground = raw.VisibilitySegments.Where(x => x.IsForeground).OrderBy(x => x.StartUtc).ToArray();
        var inputs = raw.Interactions.Where(x => x.Kind != InteractionKind.None).OrderBy(x => x.SecondUtc).ToArray();
        var events = raw.SessionEvents.OrderBy(x => x.TimeUtc).ToArray();
        var globalInputs = raw.GlobalInputs.OrderBy(x => x.ObservedUtc).ToArray();
        var tracking = TrackingAvailability.GetNotTrackedIntervals(raw, end);
        var i = 0; var s = 0; var e = 0; var locked = false; var suspended = false; var loggedOff = false;
        var t = 0; var g = 0;
        var recordingSince = cursor;
        DateTimeOffset? lastInput = null, runStart = null;
        string? runKey = null, label = null;
        Brush? color = null;
        while (cursor < end)
        {
            while (t < tracking.Count && tracking[t].EndUtc <= cursor) t++;
            if (t < tracking.Count && tracking[t].StartUtc <= cursor)
            {
                if (runKey is not null && runStart is not null) yield return new DashboardTimelineItem(label!, runStart.Value, cursor, color!);
                runKey = null;
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
            while (s < foreground.Length && foreground[s].EndUtc <= cursor) s++;
            var away = locked || suspended || loggedOff || lastInput is null || cursor - lastInput.Value >= settings.AwayTimeout;
            WindowInfo? window = null;
            if (!away && lastInput is not null && cursor - lastInput.Value < settings.ActiveTimeout &&
                s < foreground.Length && foreground[s].StartUtc <= cursor && foreground[s].EndUtc > cursor)
                windows.TryGetValue(foreground[s].WindowId, out window);
            var key = away ? "\0away" : window is null ? null : AppKey(window);
            if (key != runKey)
            {
                if (runKey is not null && runStart is not null) yield return new DashboardTimelineItem(label!, runStart.Value, cursor, color!);
                runKey = key; runStart = cursor;
                label = away ? DashboardText.Get("Away") : string.IsNullOrWhiteSpace(window?.FriendlyName) ? window?.ProcessName : window.FriendlyName;
                if (key is not null && string.IsNullOrWhiteSpace(label)) label = DashboardText.Get("NoApplication");
                color = key is null ? null : away ? SystemColorsBrush() : AppColor(key);
            }
            var duration = end - cursor < TimeSpan.FromSeconds(1) ? end - cursor : TimeSpan.FromSeconds(1);
            if (t < tracking.Count && tracking[t].StartUtc - cursor < duration) duration = tracking[t].StartUtc - cursor;
            if (e < events.Length && events[e].TimeUtc - cursor < duration) duration = events[e].TimeUtc - cursor;
            if (i < inputs.Length && inputs[i].SecondUtc - cursor < duration) duration = inputs[i].SecondUtc - cursor;
            if (g < globalInputs.Length && globalInputs[g].ObservedUtc - cursor < duration) duration = globalInputs[g].ObservedUtc - cursor;
            if (lastInput is { } last)
            {
                var age = cursor - last;
                if (age < settings.AwayTimeout && settings.AwayTimeout - age < duration) duration = settings.AwayTimeout - age;
                if (age < settings.ActiveTimeout && settings.ActiveTimeout - age < duration) duration = settings.ActiveTimeout - age;
            }
            cursor += duration;
        }
        if (runKey is not null && runStart is not null) yield return new DashboardTimelineItem(label!, runStart.Value, end, color!);
    }

    private static Brush SystemColorsBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(128, 128, 128));
        brush.Freeze();
        return brush;
    }
}
