using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;

namespace WorkTimeTracker.App;

/// <summary>A side-effect-free editor. The host persists ResultSettings after ShowDialog returns true.</summary>
public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AppSettings original;
    private readonly string dataDirectory;

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
