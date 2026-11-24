using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
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
    private readonly Func<TrackingService> tracker;
    private TrackingService? subscribedTracker;
    private bool dayActionPending;
    private string? sortProperty;
    private ListSortDirection sortDirection = ListSortDirection.Descending;

    public DashboardWindow(Func<TrackingService> tracker, Func<AppSettings> settings, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.tracker = tracker;
        viewModel = new DashboardViewModel(databasePath, settings);
        DataContext = viewModel;
        InitializeComponent();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += Timer_Tick;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        IsVisibleChanged += Window_VisibilityChanged;
        Closed += Window_Closed;
        PreviewKeyDown += Window_PreviewKeyDown;
    }

    public event EventHandler? SettingsRequested;

    private void Window_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) { UpdateTracker(); timer.Start(); _ = viewModel.RefreshAsync(); }
        else { timer.Stop(); viewModel.CancelPending(); UnsubscribeTracker(); }
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        timer.Stop();
        timer.Tick -= Timer_Tick;
        UnsubscribeTracker();
        viewModel.CancelPending();
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        IsVisibleChanged -= Window_VisibilityChanged;
        PreviewKeyDown -= Window_PreviewKeyDown;
        Closed -= Window_Closed;
    }
    private async void Timer_Tick(object? sender, EventArgs e) { UpdateTracker(); await viewModel.RefreshAsync(); }
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
    private void ApplicationRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement element) Animate(element);
    }
    private static void AnimateHover(object sender, double opacity)
    {
        if (SystemParameters.ClientAreaAnimation && sender is UIElement element)
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(200)));
    }
    private void Row_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => AnimateHover(sender, 0.96);
    private void Row_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => AnimateHover(sender, 1);
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Control { Tag: string property }) return;
        sortDirection = sortProperty == property && sortDirection == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        sortProperty = property;
        var directionMark = sortDirection == ListSortDirection.Ascending ? " ?" : " ?";
        SortApplication.Content = DashboardText.Get("Application") + (property == "Name" ? directionMark : "");
        SortActive.Content = DashboardText.Get("Active") + (property == "ActiveTime" ? directionMark : "");
        SortDisplayed.Content = DashboardText.Get("Displayed") + (property == "DisplayedTime" ? directionMark : "");
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
