using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using WorkTimeTracker.Core.Models;
using Wpf.Ui.Controls;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace WorkTimeTracker.App;

public partial class DashboardWindow : FluentWindow
{
    private readonly DashboardViewModel viewModel;
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer statusTimer;
    private readonly Func<TrackingService> tracker;
    private readonly Func<AppSettings> settings;
    private readonly string databasePath;
    private TrackingService? subscribedTracker;
    private bool dayActionPending;
    private readonly Dictionary<string, bool> expandedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string App, string Label), bool> expandedLabels = new();
    private readonly Dictionary<DateOnly, bool> expandedDays = new();
    private double refreshScrollOffset;
    private string? sortProperty = "ActiveTime";
    private ListSortDirection sortDirection = ListSortDirection.Descending;

    public DashboardWindow(Func<TrackingService> tracker, Func<AppSettings> settings, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.tracker = tracker;
        this.settings = settings;
        this.databasePath = databasePath;
        viewModel = new DashboardViewModel(databasePath, settings, tracker);
        DataContext = viewModel;
        InitializeComponent();
        FlowDirection = UiCulture.Direction;
        Language = UiCulture.Language;
        DatePickerControl.FirstDayOfWeek = UiCulture.FirstDay(settings().WeekStart);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += Timer_Tick;
        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statusTimer.Tick += StatusTimer_Tick;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.ReportApplying += ReportApplying;
        viewModel.ReportApplied += ReportApplied;
        IsVisibleChanged += Window_VisibilityChanged;
        Closed += Window_Closed;
        PreviewKeyDown += Window_PreviewKeyDown;
    }

    public event EventHandler? SettingsRequested;

    private void Window_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) { UpdateTracker(); timer.Start(); statusTimer.Start(); _ = viewModel.RefreshAsync(); }
        else { timer.Stop(); statusTimer.Stop(); viewModel.CancelPending(); UnsubscribeTracker(); }
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        timer.Stop();
        statusTimer.Stop();
        timer.Tick -= Timer_Tick;
        statusTimer.Tick -= StatusTimer_Tick;
        UnsubscribeTracker();
        viewModel.CancelPending();
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        viewModel.ReportApplying -= ReportApplying;
        viewModel.ReportApplied -= ReportApplied;
        IsVisibleChanged -= Window_VisibilityChanged;
        PreviewKeyDown -= Window_PreviewKeyDown;
        Closed -= Window_Closed;
    }
    private async void Timer_Tick(object? sender, EventArgs e) { UpdateTracker(); if (viewModel.IsLivePeriod && !viewModel.Loading) await viewModel.RefreshAsync(); }
    private void StatusTimer_Tick(object? sender, EventArgs e) { if (subscribedTracker is not null) UpdateStatus(subscribedTracker); }
    private void UpdateTracker()
    {
        if (!IsVisible) { UnsubscribeTracker(); return; }
        try
        {
            var current = tracker();
            if (!ReferenceEquals(current, subscribedTracker))
            {
                UnsubscribeTracker();
                subscribedTracker = current;
                current.StateChanged += Tracker_StateChanged;
            }
            UpdateDayAction(current.Status);
            UpdateStatus(current);
        }
        catch (Exception error) { DayAction.IsEnabled = false; ShowError(error); }
    }
    private void UnsubscribeTracker()
    {
        if (subscribedTracker is not null) subscribedTracker.StateChanged -= Tracker_StateChanged;
        subscribedTracker = null;
    }
    private void Tracker_StateChanged(TrackingStatus status)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || subscribedTracker is null) return;
            UpdateDayAction(subscribedTracker.Status);
            UpdateStatus(subscribedTracker);
            _ = viewModel.RefreshAsync();
        });
    }
    private void UpdateDayAction(TrackingStatus status)
    {
        var ended = status.State == TrackingState.DayEnded;
        DayAction.Content = DashboardText.Get(ended ? "StartDay" : "EndDay");
        DayAction.IsEnabled = !dayActionPending && (ended || ((status.State is TrackingState.Tracking or TrackingState.Paused or TrackingState.Unavailable) && status.WorkDay is { EndUtc: null }));
    }
    private async void DayAction_Click(object sender, RoutedEventArgs e)
    {
        if (dayActionPending) return;
        try
        {
            dayActionPending = true;
            DayAction.IsEnabled = false;
            var current = tracker();
            var status = current.Status;
            if (status.State == TrackingState.DayEnded) await current.StartDayAsync();
            else if ((status.State is TrackingState.Tracking or TrackingState.Paused or TrackingState.Unavailable) && status.WorkDay is { EndUtc: null })
                await current.EndDayAsync();
            await viewModel.RefreshAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { dayActionPending = false; UpdateTracker(); }
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.Message) && !string.IsNullOrEmpty(viewModel.Message))
            ShowMessage(viewModel.Message, ControlAppearance.Danger);
        if (e.PropertyName is nameof(DashboardViewModel.IsEmpty) or nameof(DashboardViewModel.SelectedDate))
            UpdatePreviousDayBanner();
    }
    private void UpdateStatus(TrackingService current)
    {
        var state = current.Status.State;
        var last = current.LastSampleUtc;
        var maximumAgeSeconds = settings().Tracker.SampleInterval.TotalSeconds * 2 + 5;
        var active = state == TrackingState.Tracking && last is not null && (DateTimeOffset.UtcNow - last.Value).TotalSeconds <= maximumAgeSeconds;
        var paused = state == TrackingState.Paused;
        TrackingStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            active ? "SystemFillColorSuccessBrush" : paused ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush");
        TrackingStatusText.Text = active
            ? string.Format(DashboardText.Get("StatusActive"), Math.Max(0, (int)(DateTimeOffset.UtcNow - last!.Value).TotalSeconds))
            : DashboardText.Get(paused ? "StatusPaused" : "StatusInactive");
        UpdatePreviousDayBanner();
    }

    private void UpdatePreviousDayBanner()
    {
        var open = subscribedTracker?.Status.WorkDay;
        var show = viewModel.IsEmpty && open is { EndUtc: null } &&
            open.Date < DateOnly.FromDateTime(viewModel.SelectedDate);
        PreviousDayBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) PreviousDayText.Text = string.Format(DashboardText.Get("PreviousDayOpen"), open!.Date.ToString("dd/MM"));
    }

    private void GoToOpenDay_Click(object sender, RoutedEventArgs e)
    {
        if (subscribedTracker?.Status.WorkDay is { EndUtc: null } day) viewModel.SelectedDate = day.Date.ToDateTime(TimeOnly.MinValue);
    }
    private async void RestartDay_Click(object sender, RoutedEventArgs e)
    {
        try { await tracker().EndAndStartDayAsync(); viewModel.Today(); }
        catch (Exception error) { ShowError(error); }
    }
    private void ShowMessage(string text, ControlAppearance appearance)
    {
        var snackbar = new Snackbar(SnackPresenter) { Content = text, Appearance = appearance, Timeout = TimeSpan.FromSeconds(5), IsCloseButtonEnabled = true };
        snackbar.Show();
    }
    private void ShowError(Exception error) =>
        ShowMessage(DashboardText.Get(DashboardLog.Write(error) ? "Error" : "LogUnavailable"), ControlAppearance.Danger);
    private void ShowExportSuccess(string fileName)
    {
        var text = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new Run(DashboardText.Get("ExportSuccess") + " "));
        var link = new Hyperlink(new Run(DashboardText.Get("OpenFolder")));
        link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        link.Click += (_, _) =>
        {
            try
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(fileName)) ?? throw new IOException("Export directory is unavailable.");
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add(folder);
                Process.Start(start);
            }
            catch (Exception error) { ShowError(error); }
        };
        text.Inlines.Add(link);
        new Snackbar(SnackPresenter) { Content = text, Appearance = ControlAppearance.Success, Timeout = TimeSpan.FromSeconds(15), IsCloseButtonEnabled = true }.Show();
    }
    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element && viewModel is not null)
            viewModel.ResizeTimeline(element.ActualWidth);
    }

    private void PeriodTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (viewModel is null || !IsVisible || !ReferenceEquals(e.Source, PeriodTabs)) return;
        viewModel.SetPeriod((DashboardPeriod)Math.Clamp(PeriodTabs.SelectedIndex, 0, 2));
        AnimatePage();
    }
    private void Grouping_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (viewModel is null || !IsVisible || !ReferenceEquals(e.Source, GroupingInput)) return;
        viewModel.Grouping = (DashboardGrouping)Math.Clamp(GroupingInput.SelectedIndex, 0, 2);
    }
    private void Previous_Click(object sender, RoutedEventArgs e) { viewModel.Navigate(-1); AnimatePage(); }
    private void Next_Click(object sender, RoutedEventArgs e) { viewModel.Navigate(1); AnimatePage(); }
    private void Today_Click(object sender, RoutedEventArgs e) { viewModel.Today(); AnimatePage(); }
    private void OverviewNavigation_Click(object sender, RoutedEventArgs e) => DashboardScroll.ScrollToTop();
    private void DatePicker_Changed(object sender, SelectionChangedEventArgs e) { if (IsVisible) AnimatePage(); }
    private static void Animate(UIElement element)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop });
    }
    private void AnimatePage()
    {
        if (DailySection is null || PeriodSection is null || TableSection is null) return;
        Animate(viewModel.IsDaily ? DailySection : PeriodSection);
        Animate(TableSection);
    }
    private void Row_Loading(object sender, DataGridRowEventArgs e) => Animate(e.Row);
    private void DaySessions_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DashboardDay day } element &&
            ItemsControl.ContainerFromElement(DaysGrid, element) is DataGridRow row)
            row.DetailsVisibility = expandedDays.TryGetValue(day.Date, out var expanded) && expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DaySessions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DashboardDay day } element &&
            ItemsControl.ContainerFromElement(DaysGrid, element) is DataGridRow row)
        {
            var expanded = row.DetailsVisibility != Visibility.Visible;
            expandedDays[day.Date] = expanded;
            row.DetailsVisibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private void ApplicationRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement element) Animate(element);
    }
    private void ReportApplying() => refreshScrollOffset = DashboardScroll.VerticalOffset;
    private void ReportApplied()
    {
        var offset = refreshScrollOffset;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (IsVisible) DashboardScroll.ScrollToVerticalOffset(offset);
        });
    }

    private void AppExpansion_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: DashboardApplication app } toggle)
            toggle.IsChecked = expandedApps.TryGetValue(app.Key, out var expanded) && expanded;
    }
    private void AppExpansion_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: DashboardApplication app } toggle)
            expandedApps[app.Key] = toggle.IsChecked == true;
    }
    private static (string App, string Label)? LabelKey(ToggleButton toggle)
    {
        if (toggle.DataContext is not DashboardLabel label) return null;
        for (DependencyObject? item = VisualTreeHelper.GetParent(toggle); item is not null; item = VisualTreeHelper.GetParent(item))
            if (item is FrameworkElement { DataContext: DashboardApplication app }) return (app.Key, label.Name);
        return null;
    }
    private void LabelExpansion_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle && LabelKey(toggle) is { } key)
            toggle.IsChecked = expandedLabels.TryGetValue(key, out var expanded) && expanded;
    }
    private void LabelExpansion_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle && LabelKey(toggle) is { } key)
            expandedLabels[key] = toggle.IsChecked == true;
    }
    private static void AnimateHover(object sender, double opacity)
    {
        if (SystemParameters.ClientAreaAnimation && sender is UIElement element)
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(200)));
    }
    private void Row_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => AnimateHover(sender, 0.96);
    private void Row_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => AnimateHover(sender, 1);
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private async void EditDay_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.Report is not { ReportDays.Count: > 0 } report) return;
        try
        {
            var dialog = new EditDayWindow(databasePath, report.ReportDays) { Owner = this };
            if (dialog.ShowDialog() == true) await viewModel.RefreshAsync();
        }
        catch (Exception error) { ShowError(error); }
    }
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Control { Tag: string property }) return;
        sortDirection = sortProperty == property && sortDirection == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        sortProperty = property;
        var symbol = sortDirection == ListSortDirection.Ascending ? SymbolRegular.ArrowUp16 : SymbolRegular.ArrowDown16;
        SortApplicationIcon.Symbol = SortActiveIcon.Symbol = SortDisplayedIcon.Symbol = symbol;
        SortApplicationIcon.Visibility = property == "Name" ? Visibility.Visible : Visibility.Collapsed;
        SortActiveIcon.Visibility = property == "ActiveTime" ? Visibility.Visible : Visibility.Collapsed;
        SortDisplayedIcon.Visibility = property == "DisplayedTime" ? Visibility.Visible : Visibility.Collapsed;
        var view = CollectionViewSource.GetDefaultView(viewModel.Applications);
        using (view.DeferRefresh()) { view.SortDescriptions.Clear(); view.SortDescriptions.Add(new SortDescription(property, sortDirection)); }
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E) { Export_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            if (e.Key == Key.Left) { viewModel.Navigate(-1); AnimatePage(); e.Handled = true; }
            else if (e.Key == Key.Right) { viewModel.Navigate(1); AnimatePage(); e.Handled = true; }
        }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var report = viewModel.Report;
        if (report is null || viewModel.Loading) return;
        report = ReportExport.VisibleGroups(report, CollectionViewSource.GetDefaultView(viewModel.Applications).Cast<DashboardApplication>());
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = DashboardText.Get("Export"),
                Filter = DashboardText.Get("Filter"),
                FileName = DashboardText.Get("ExportName"),
                DefaultExt = ".xlsx",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;
            var root = Path.GetPathRoot(dialog.FileName);
            if (dialog.FileName.StartsWith(@"\\", StringComparison.Ordinal) || root is null || new DriveInfo(root).DriveType == DriveType.Network)
            {
                ShowMessage(DashboardText.Get("LocalOnly"), ControlAppearance.Danger);
                return;
            }
            await Task.Run(() => ReportExport.Write(dialog.FileName, report));
            ShowExportSuccess(dialog.FileName);
        }
        catch (Exception error) { ShowError(error); }
    }
}
