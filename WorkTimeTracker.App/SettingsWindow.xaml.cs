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

    public SettingsWindow(AppSettings current, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        original = current.Snapshot();
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        InitializeComponent();
        MinimumVisibleInput.Text = original.Tracker.MinimumVisiblePercent.ToString(CultureInfo.CurrentCulture);
        InteractionInput.Text = Format(original.Tracker.InteractionWindow.TotalMinutes);
        ActiveInput.Text = Format(original.Tracker.ActiveTimeout.TotalSeconds);
        AwayInput.Text = Format(original.Tracker.AwayTimeout.TotalMinutes);
        AutoEndInput.Text = Format(original.Tracker.AutoEndAfter.TotalHours);
        SampleInput.Text = Format(original.Tracker.SampleInterval.TotalSeconds);
        RetentionInput.Text = Format(original.Tracker.RetentionPeriod?.TotalDays ?? 0);
        ExclusionsInput.Text = string.Join(Environment.NewLine,
            original.Tracker.ExcludedApplications.Order(StringComparer.OrdinalIgnoreCase));
        AutostartInput.IsChecked = original.StartWithWindows;
        ThemeInput.SelectedIndex = (int)original.Theme;
        labelRules = new ObservableCollection<EditableLabelRule>(original.LabelRules.Select(rule => new EditableLabelRule(rule)));
        LabelRulesGrid.ItemsSource = labelRules;
        LoadRecentTitles();
    }

    public AppSettings? ResultSettings { get; private set; }

    private static string Format(double value) => value.ToString("R", CultureInfo.CurrentCulture);

    private static decimal Number(string value) =>
        decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint |
            NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.CurrentCulture);

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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var retention = Duration(RetentionInput.Text, TimeSpan.TicksPerDay, allowZero: true);
            var candidate = original with
            {
                Tracker = original.Tracker with
                {
                    MinimumVisiblePercent = Number(MinimumVisibleInput.Text),
                    InteractionWindow = Duration(InteractionInput.Text, TimeSpan.TicksPerMinute),
                    ActiveTimeout = Duration(ActiveInput.Text, TimeSpan.TicksPerSecond),
                    AwayTimeout = Duration(AwayInput.Text, TimeSpan.TicksPerMinute),
                    AutoEndAfter = Duration(AutoEndInput.Text, TimeSpan.TicksPerHour),
                    SampleInterval = Duration(SampleInput.Text, TimeSpan.TicksPerSecond),
                    RetentionPeriod = retention == TimeSpan.Zero ? null : retention,
                    ExcludedApplications = ExclusionsInput.Text.Split(new[] { "\r\n", "\n", "\r" },
                            StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim())
                        .Where(value => value.Length > 0).ToFrozenSet(StringComparer.OrdinalIgnoreCase)
                },
                LabelRules = labelRules.Select(rule => rule.ToRule()).ToArray(),
                StartWithWindows = AutostartInput.IsChecked == true,
                Theme = (ThemePreference)ThemeInput.SelectedIndex
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
            RecentTitlesInput.ItemsSource = titles;
            if (titles.Length > 0) RecentTitlesInput.SelectedIndex = 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or DbException)
        {
            RuleTestResult.Text = SettingsText.Get("NoRecentTitles");
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
        if (RecentTitlesInput.SelectedItem is not WindowInfo window)
        {
            RuleTestResult.Text = SettingsText.Get("NoRecentTitles");
            return;
        }
        try { RuleTestResult.Text = LabelExtractor.Extract(window, labelRules.Select(rule => rule.ToRule()).ToArray()); }
        catch (ArgumentException) { ShowError("InvalidSettings"); }
    }

    private sealed class EditableLabelRule(LabelRule rule)
    {
        public string Application { get; set; } = rule.Application;
        public string Pattern { get; set; } = rule.Pattern;
        public bool Enabled { get; set; } = rule.Enabled;
        public LabelRule ToRule() => new(Application, Pattern, Enabled);
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
