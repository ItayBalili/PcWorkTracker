using System.Collections.Frozen;
using System.ComponentModel;
using System.Collections.ObjectModel;
using WorkTimeTracker.Core.Labels;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Data.Common;
using System.Windows;

namespace WorkTimeTracker.App;

/// <summary>A side-effect-free editor. The host persists ResultSettings after ShowDialog returns true.</summary>
public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AppSettings original;
    private readonly string dataDirectory;
    private readonly ObservableCollection<EditableLabelRule> labelRules;
    private readonly ObservableCollection<EditableTitleRule> titleRules;
    private bool recentTitlesFailed;
    private sealed record RecentTitle(WindowInfo Window)
    {
        public string DisplayTitle => $"\u2068{Window.Title}\u2069";
    }

    public SettingsWindow(AppSettings current, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        original = current.Snapshot();
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        InitializeComponent();
        FlowDirection = UiCulture.Direction;
        Language = UiCulture.Language;
        MinimumVisibleInput.Value = (double)original.Tracker.MinimumVisiblePercent;
        InteractionInput.Value = original.Tracker.InteractionWindow.TotalMinutes;
        ActiveInput.Value = original.Tracker.ActiveTimeout.TotalSeconds;
        AwayInput.Value = original.Tracker.AwayTimeout.TotalMinutes;
        AutoEndInput.Value = original.Tracker.AutoEndAfter.TotalHours;
        SampleInput.Value = original.Tracker.SampleInterval.TotalSeconds;
        RetentionInput.Value = original.Tracker.RetentionPeriod?.TotalDays ?? 0;
        ExclusionsInput.Text = string.Join(Environment.NewLine,
            original.Tracker.ExcludedApplications.Order(StringComparer.OrdinalIgnoreCase));
        AutostartInput.IsChecked = original.StartWithWindows;
        ThemeInput.SelectedIndex = (int)original.Theme;
        AccentInput.SelectedIndex = (int)original.Accent;
        LanguageInput.SelectedIndex = (int)original.Language;
        WeekStartInput.SelectedIndex = (int)original.WeekStart;
        labelRules = new ObservableCollection<EditableLabelRule>(original.LabelRules.Select(rule => new EditableLabelRule(rule)));
        LabelRulesGrid.ItemsSource = labelRules;
        titleRules = new ObservableCollection<EditableTitleRule>(original.TitleNormalizationRules.Select(rule => new EditableTitleRule(rule)));
        TitleRulesGrid.ItemsSource = titleRules;
        LoadRecentTitles();
    }

    public AppSettings? ResultSettings { get; private set; }
    public string AppVersion => string.Format(UiCulture.Current, SettingsText.Get("VersionFormat"),
        (typeof(SettingsWindow).Assembly.GetName().Version ?? throw new InvalidOperationException("Application version is unavailable.")).ToString(3));

    private static string NumberText(double? value) =>
        (value ?? throw new FormatException()).ToString("R", UiCulture.Current);

    private static decimal Number(string value) =>
        decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint |
            NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowExponent, UiCulture.Current);

    private static TimeSpan Duration(string text, long ticksPerUnit, bool allowZero = false)
    {
        var value = Number(text);
        if (value < 0 || (!allowZero && value == 0))
            throw new ArgumentOutOfRangeException(nameof(text));
        var ticks = checked((long)(value * ticksPerUnit));
        if (ticks == 0 && value > 0)
            throw new ArgumentOutOfRangeException(nameof(text));
        return TimeSpan.FromTicks(ticks);
    }

    private static TimeSpan Duration(double? value, TimeSpan original, long ticksPerUnit, bool allowZero = false)
    {
        var originalValue = ticksPerUnit switch
        {
            TimeSpan.TicksPerSecond => original.TotalSeconds,
            TimeSpan.TicksPerMinute => original.TotalMinutes,
            TimeSpan.TicksPerHour => original.TotalHours,
            TimeSpan.TicksPerDay => original.TotalDays,
            _ => throw new ArgumentOutOfRangeException(nameof(ticksPerUnit))
        };
        return value == originalValue ? original : Duration(NumberText(value), ticksPerUnit, allowZero);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LabelRulesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            LabelRulesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            TitleRulesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            TitleRulesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            var retention = Duration(RetentionInput.Value, original.Tracker.RetentionPeriod ?? TimeSpan.Zero, TimeSpan.TicksPerDay, allowZero: true);
            var candidate = original with
            {
                Tracker = original.Tracker with
                {
                    MinimumVisiblePercent = MinimumVisibleInput.Value == (double)original.Tracker.MinimumVisiblePercent
                        ? original.Tracker.MinimumVisiblePercent : Number(NumberText(MinimumVisibleInput.Value)),
                    InteractionWindow = Duration(InteractionInput.Value, original.Tracker.InteractionWindow, TimeSpan.TicksPerMinute),
                    ActiveTimeout = Duration(ActiveInput.Value, original.Tracker.ActiveTimeout, TimeSpan.TicksPerSecond),
                    AwayTimeout = Duration(AwayInput.Value, original.Tracker.AwayTimeout, TimeSpan.TicksPerMinute),
                    AutoEndAfter = Duration(AutoEndInput.Value, original.Tracker.AutoEndAfter, TimeSpan.TicksPerHour),
                    SampleInterval = Duration(SampleInput.Value, original.Tracker.SampleInterval, TimeSpan.TicksPerSecond),
                    RetentionPeriod = retention == TimeSpan.Zero ? null : retention,
                    ExcludedApplications = ExclusionsInput.Text.Split(new[] { "\r\n", "\n", "\r" },
                            StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim())
                        .Where(value => value.Length > 0).ToFrozenSet(StringComparer.OrdinalIgnoreCase)
                },
                LabelRules = labelRules.Select(rule => rule.ToRule()).ToArray(),
                TitleNormalizationRules = titleRules.Select(rule => rule.ToRule()).ToArray(),
                StartWithWindows = AutostartInput.IsChecked == true,
                Theme = (ThemePreference)ThemeInput.SelectedIndex,
                Accent = (AccentPreference)AccentInput.SelectedIndex,
                Language = (LanguagePreference)LanguageInput.SelectedIndex,
                WeekStart = (WeekStartPreference)WeekStartInput.SelectedIndex
            };
            candidate.Validate();
            ResultSettings = candidate;
            DialogResult = true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            ShowError("InvalidSettings");
        }
    }

    private void LoadRecentTitles()
    {
        var path = Path.Combine(dataDirectory, "tracking.sqlite3");
        if (!File.Exists(path)) return;
        try
        {
            using var repository = new SqliteWorkTimeRepository(path);
            var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-30));
            var to = DateOnly.FromDateTime(DateTime.Today);
            var titles = repository.ListWorkDays(from, to).OrderByDescending(day => day.WorkDay.Date)
                .SelectMany(day => TrackingRawDataReader.GetRawData(path, day.Id).Windows)
                .Where(window => !string.IsNullOrWhiteSpace(window.Title))
                .GroupBy(window => (window.ProcessName, window.Title), StringTupleComparer.Instance)
                .Select(group => group.First()).Take(100).ToArray();
            RecentTitlesInput.ItemsSource = titles.Select(window => new RecentTitle(window)).ToArray();
            if (titles.Length > 0) RecentTitlesInput.SelectedIndex = 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or DbException)
        {
            DashboardLog.Write(exception);
            recentTitlesFailed = true;
            RuleTestResult.Text = SettingsText.Get("RecentTitlesError");
        }
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        labelRules.Add(new EditableLabelRule(new LabelRule(string.Empty, "(?<label>.*)")));
        LabelRulesGrid.SelectedIndex = labelRules.Count - 1;
        LabelRulesGrid.ScrollIntoView(LabelRulesGrid.SelectedItem);
        LabelRulesGrid.BeginEdit();
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (LabelRulesGrid.SelectedItem is EditableLabelRule selected) labelRules.Remove(selected);
    }

    private void TestRule_Click(object sender, RoutedEventArgs e)
    {
        LabelRulesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        LabelRulesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        if (RecentTitlesInput.SelectedItem is not RecentTitle recent)
        {
            RuleTestResult.Text = SettingsText.Get(recentTitlesFailed ? "RecentTitlesError" : "NoRecentTitles");
            return;
        }
        try
        {
            var normalization = titleRules.Select(rule => rule.ToRule()).ToArray();
            TitleNormalizer.Validate(normalization);
            var window = recent.Window;
            var normalized = window with { Title = TitleNormalizer.Normalize(window.Title, normalization) };
            RuleTestResult.Text = LabelExtractor.TryExtract(normalized, labelRules.Select(rule => rule.ToRule()).ToArray(), out var label)
                ? string.Format(SettingsText.Get("RuleMatched"), $"\u2068{label}\u2069")
                : SettingsText.Get("RuleNoMatch");
        }
        catch (ArgumentException) { ShowError("InvalidSettings"); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { ShowError("InvalidSettings"); }
    }

    private sealed class EditableLabelRule(LabelRule rule)
    {
        public string Application { get; set; } = rule.Application;
        public string Pattern { get; set; } = rule.Pattern;
        public bool Enabled { get; set; } = rule.Enabled;
        public LabelRule ToRule() => new(Application, Pattern, Enabled);
    }

    private void AddTitleRule_Click(object sender, RoutedEventArgs e)
    {
        titleRules.Add(new EditableTitleRule(new TitleNormalizationRule("")));
        TitleRulesGrid.SelectedIndex = titleRules.Count - 1;
        TitleRulesGrid.ScrollIntoView(TitleRulesGrid.SelectedItem);
        TitleRulesGrid.BeginEdit();
    }

    private void RemoveTitleRule_Click(object sender, RoutedEventArgs e)
    {
        if (TitleRulesGrid.SelectedItem is EditableTitleRule selected) titleRules.Remove(selected);
    }

    private void TestTitleRule_Click(object sender, RoutedEventArgs e)
    {
        TitleRulesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        TitleRulesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        if (RecentTitlesInput.SelectedItem is not RecentTitle recent)
        {
            TitleRuleTestResult.Text = SettingsText.Get(recentTitlesFailed ? "RecentTitlesError" : "NoRecentTitles");
            return;
        }
        try
        {
            var rules = titleRules.Select(rule => rule.ToRule()).ToArray();
            TitleNormalizer.Validate(rules);
            TitleRuleTestResult.Text = string.Format(SettingsText.Get("TitleNormalizationResult"), $"\u2068{TitleNormalizer.Normalize(recent.Window.Title, rules)}\u2069");
        }
        catch (ArgumentException) { ShowError("InvalidSettings"); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { ShowError("InvalidSettings"); }
    }

    private sealed class EditableTitleRule(TitleNormalizationRule rule)
    {
        public string Pattern { get; set; } = rule.Pattern;
        public bool Enabled { get; set; } = rule.Enabled;
        public TitleNormalizationRule ToRule() => new(Pattern, Enabled);
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string ProcessName, string Title)>
    {
        public static StringTupleComparer Instance { get; } = new();
        public bool Equals((string ProcessName, string Title) x, (string ProcessName, string Title) y) =>
            string.Equals(x.ProcessName, y.ProcessName, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Title, y.Title, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string ProcessName, string Title) value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.ProcessName), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Title));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            Process.Start(new ProcessStartInfo(dataDirectory) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           Win32Exception or InvalidOperationException)
        {
            ShowError("FolderError");
        }
    }

    private void ShowError(string key)
    {
        ErrorText.Text = SettingsText.Get(key);
        ErrorText.Visibility = Visibility.Visible;
    }
}
