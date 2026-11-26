using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WorkTimeTracker.Data;
using Wpf.Ui.Controls;

namespace WorkTimeTracker.App;

public partial class EditDayWindow : FluentWindow
{
    private const string Format = "dd/MM/yyyy HH:mm:ss";
    private readonly string databasePath;
    private sealed record Choice(ReportDay Day, string Display);

    internal EditDayWindow(string databasePath, IReadOnlyList<ReportDay> days)
    {
        this.databasePath = databasePath;
        InitializeComponent();
        DaySelection.ItemsSource = days.Select(day => new Choice(day,
            day.Original.WorkDay.StartUtc.ToLocalTime().ToString(Format, CultureInfo.InvariantCulture))).ToArray();
        DaySelection.SelectedIndex = 0;
    }

    private void DaySelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DaySelection.SelectedItem is not Choice choice) return;
        var day = choice.Day;
        StartInput.Text = (day.Edit?.StartUtc ?? day.Original.WorkDay.StartUtc).ToLocalTime().ToString(Format, CultureInfo.InvariantCulture);
        var end = day.Edit?.EndUtc ?? day.Original.WorkDay.EndUtc;
        EndInput.Text = end?.ToLocalTime().ToString(Format, CultureInfo.InvariantCulture) ?? "";
        EndInput.IsEnabled = day.Original.WorkDay.EndUtc is not null;
        OpenHint.Visibility = EndInput.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private static bool TryParseLocal(string text, out DateTimeOffset value)
    {
        value = default;
        if (!DateTime.TryParseExact(text.Trim(), Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
            TimeZoneInfo.Local.IsInvalidTime(time) || TimeZoneInfo.Local.IsAmbiguousTime(time)) return false;
        value = new DateTimeOffset(time, TimeZoneInfo.Local.GetUtcOffset(time)).ToUniversalTime();
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = DashboardText.Get("EditDayInvalid");
        if (DaySelection.SelectedItem is not Choice choice || !TryParseLocal(StartInput.Text, out var start) ||
            EndInput.IsEnabled && !TryParseLocal(EndInput.Text, out _))
        {
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DateTimeOffset? end = EndInput.IsEnabled && TryParseLocal(EndInput.Text, out var parsedEnd) ? parsedEnd : null;
        var existingStart = choice.Day.Edit?.StartUtc ?? choice.Day.Original.WorkDay.StartUtc;
        var existingEnd = choice.Day.Edit?.EndUtc ?? choice.Day.Original.WorkDay.EndUtc;
        if (StartInput.Text.Trim() == existingStart.ToLocalTime().ToString(Format, CultureInfo.InvariantCulture)) start = existingStart;
        if (existingEnd is { } originalEnd && EndInput.Text.Trim() == originalEnd.ToLocalTime().ToString(Format, CultureInfo.InvariantCulture)) end = originalEnd;
        try
        {
            using var repository = new SqliteWorkTimeRepository(databasePath);
            repository.SaveWorkDayEdit(new WorkDayEdit(choice.Day.Original.Id, start, end));
            DialogResult = true;
        }
        catch (ArgumentException) { ErrorText.Visibility = Visibility.Visible; }
        catch (Exception error)
        {
            ErrorText.Text = DashboardText.Get(DashboardLog.Write(error) ? "Error" : "LogUnavailable");
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
