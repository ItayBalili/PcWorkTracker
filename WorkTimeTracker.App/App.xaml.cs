using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Resources;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WorkTimeTracker.Core.Calculation;
using WorkTimeTracker.Core.Models;
using WorkTimeTracker.Data;
using Wpf.Ui.Appearance;
using Forms = System.Windows.Forms;

namespace WorkTimeTracker.App;

public partial class App : System.Windows.Application
{
    private static readonly ResourceManager Text = new("WorkTimeTracker.App.HostStrings", typeof(App).Assembly);
    private readonly string dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkTimeTracker");
    private readonly SemaphoreSlim operations = new(1, 1);
    private Mutex? instance;
    private EventWaitHandle? activate;
    private EventWaitHandle? stoppingActivation;
    private Task? activationTask;
    private Forms.NotifyIcon? tray;
    private bool trayMenuOpen;
    private string trayTooltip = "WorkTimeTracker";
    private Forms.ToolStripMenuItem? dayMenu;
    private Forms.ToolStripMenuItem? pauseMenu;
    private Icon? trayIcon;
    private DispatcherTimer? tooltipTimer;
    private DashboardWindow? dashboard;
    private SettingsStore? settingsStore;
    private AppSettings? settings;
    private TrackingService? tracker;
    private bool exiting;
    private bool dashboardMaximized;
    private bool uiResourcesLoaded;

    private static string T(string key) => Text.GetString(key)
        ?? throw new InvalidOperationException($"Missing resource: {key}");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WorkTimeTracker.Activate.v1");
        instance = new Mutex(true, @"Local\WorkTimeTracker.Instance.v1", out var first);
        if (!first)
        {
            activate.Set();
            Shutdown();
            return;
        }

        try
        {
            Directory.CreateDirectory(dataDirectory);
            settingsStore = new SettingsStore(dataDirectory);
            var firstRun = !File.Exists(settingsStore.SettingsPath);
            settings = settingsStore.Load();
            if (firstRun)
            {
                var enable = System.Windows.MessageBox.Show(T("AutostartPrompt"), "WorkTimeTracker",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes,
                    System.Windows.MessageBoxOptions.RightAlign | System.Windows.MessageBoxOptions.RtlReading) == MessageBoxResult.Yes;
                if (enable) settingsStore.SetAutostart(true);
                settings = settings with { StartWithWindows = enable };
                settingsStore.Save(settings);
            }
            CreateTray();
            stoppingActivation = new EventWaitHandle(false, EventResetMode.ManualReset);
            activationTask = Task.Run(WatchActivation);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SessionEnding += OnSessionEnding;
            await StartTrackerAsync();
            tooltipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            tooltipTimer.Tick += async (_, _) => { UpdateTray(); await RefreshTooltipAsync(); };
            tooltipTimer.Start();
            await RefreshTooltipAsync();
            if (!e.Args.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase)))
                ShowDashboard();
        }
        catch (Exception ex)
        {
            ReportError(ex);
            exiting = true;
            Shutdown(1);
        }
    }

    private async Task StartTrackerAsync()
    {
        tracker = new TrackingService(new TrackingOptions { Settings = settings!.Tracker });
        tracker.StateChanged += OnTrackerStateChanged;
        tracker.Error += OnTrackerError;
        await tracker.StartAsync();
        UpdateTray();
    }

    private void WatchActivation()
    {
        while (WaitHandle.WaitAny([activate!, stoppingActivation!]) == 0)
            Dispatcher.BeginInvoke(ShowDashboard);
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip { RightToLeft = Forms.RightToLeft.Yes };
        menu.Items.Add(T("Open"), null, (_, _) => ShowDashboard());
        dayMenu = new Forms.ToolStripMenuItem();
        dayMenu.Click += async (_, _) => await RunActionAsync(async () =>
        {
            if (tracker!.Status.State == TrackingState.DayEnded) await tracker.StartDayAsync();
            else await tracker.EndDayAsync();
        });
        menu.Items.Add(dayMenu);
        pauseMenu = new Forms.ToolStripMenuItem();
        pauseMenu.Click += async (_, _) => await RunActionAsync(async () =>
        {
            if (tracker!.Status.State == TrackingState.Paused) await tracker.ResumeAsync();
            else await tracker.PauseAsync();
        });
        menu.Items.Add(pauseMenu);
        menu.Items.Add(T("Settings"), null, async (_, _) => await OpenSettingsAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(T("Exit"), null, async (_, _) => await ExitAsync());
        tray = new Forms.NotifyIcon { ContextMenuStrip = menu, Text = "WorkTimeTracker" };
        menu.Opening += (_, _) => { trayMenuOpen = true; if (tray is not null) tray.Text = string.Empty; };
        menu.Closed += (_, _) => { trayMenuOpen = false; if (tray is not null) tray.Text = trayTooltip; };
        tray.DoubleClick += (_, _) => ShowDashboard();
        UpdateTray();
        tray.Visible = true;
    }

    private void ShowDashboard()
    {
        if (exiting || tracker == null) return;
        EnsureUiResources();
        if (dashboard == null)
        {
            dashboard = new DashboardWindow(() => tracker!, () => settings!,
                Path.Combine(dataDirectory, "tracking.sqlite3"));
            RestorePlacement(dashboard);
            dashboard.SettingsRequested += async (_, _) => await OpenSettingsAsync();
            dashboard.Closing += (_, args) =>
            {
                if (exiting) return;
                args.Cancel = true;
                HideDashboard();
            };
            dashboard.StateChanged += (_, _) =>
            {
                if (dashboard.WindowState == WindowState.Minimized) HideDashboard();
                else dashboardMaximized = dashboard.WindowState == WindowState.Maximized;
            };
        }
        dashboard.ShowInTaskbar = true;
        if (!dashboard.IsVisible) dashboard.Show();
        if (dashboard.WindowState == WindowState.Minimized)
            dashboard.WindowState = dashboardMaximized ? WindowState.Maximized : WindowState.Normal;
        dashboard.Activate();
        dashboard.Topmost = true;
        dashboard.Topmost = false;
        dashboard.Focus();
    }

    private void HideDashboard()
    {
        if (dashboard == null) return;
        dashboard.ShowInTaskbar = false;
        SavePlacement();
        dashboard.Hide();
        if (!settings!.HasShownTrayNotice)
        {
            try
            {
                var updated = settings with { HasShownTrayNotice = true };
                settingsStore!.Save(updated);
                settings = updated;
                tray!.ShowBalloonTip(3000, "WorkTimeTracker", T("Hidden"), Forms.ToolTipIcon.Info);
            }
            catch (Exception ex) { ReportError(ex); }
        }
    }

    private async Task OpenSettingsAsync()
    {
        if (settings == null || exiting) return;
        try
        {
            EnsureUiResources();
            var dialog = new SettingsWindow(settings, dataDirectory);
            if (dashboard?.IsVisible == true) dialog.Owner = dashboard;
            if (dialog.ShowDialog() != true || dialog.ResultSettings is not { } updated) return;
            updated = updated with
            {
                HasShownTrayNotice = settings.HasShownTrayNotice,
                WindowPlacement = settings.WindowPlacement
            };
            await operations.WaitAsync();
            try
            {
                var previous = settings;
                var autostartChanged = previous.StartWithWindows != updated.StartWithWindows;
                settingsStore!.Save(updated);
                try
                {
                    if (autostartChanged) settingsStore.SetAutostart(updated.StartWithWindows);
                }
                catch (Exception error)
                {
                    try { settingsStore.Save(previous); }
                    catch (Exception rollback) { throw new AggregateException(error, rollback); }
                    throw;
                }
                settings = updated;
                ApplyTheme();
                if (!SameTrackerSettings(previous.Tracker, updated.Tracker))
                {
                    if (tracker != null) await tracker.DisposeAsync();
                    await StartTrackerAsync();
                }
            }
            finally { operations.Release(); }
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private void RestorePlacement(Window window)
    {
        if (settings?.WindowPlacement is not { } placement) return;
        var saved = new System.Windows.Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        var desktop = new System.Windows.Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = System.Windows.Rect.Intersect(saved, desktop);
        if (visible.IsEmpty || visible.Width < 100 || visible.Height < 100) return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = placement.Left;
        window.Top = placement.Top;
        window.Width = Math.Max(window.MinWidth, placement.Width);
        window.Height = Math.Max(window.MinHeight, placement.Height);
        dashboardMaximized = placement.IsMaximized;
        if (dashboardMaximized) window.WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (dashboard == null || settings == null || settingsStore == null) return;
        var bounds = dashboard.WindowState == WindowState.Normal
            ? new System.Windows.Rect(dashboard.Left, dashboard.Top, dashboard.ActualWidth, dashboard.ActualHeight)
            : dashboard.RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
            bounds.Width <= 0 || bounds.Height <= 0) return;
        try
        {
            var updated = settings with
            {
                WindowPlacement = new WindowPlacement
                {
                    Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
                    IsMaximized = dashboardMaximized
                }
            };
            settingsStore.Save(updated);
            settings = updated;
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private static bool SameTrackerSettings(TrackerSettings left, TrackerSettings right) =>
        left.MinimumVisiblePercent == right.MinimumVisiblePercent &&
        left.InteractionWindow == right.InteractionWindow &&
        left.ActiveTimeout == right.ActiveTimeout &&
        left.AwayTimeout == right.AwayTimeout &&
        left.AutoEndAfter == right.AutoEndAfter &&
        left.SampleInterval == right.SampleInterval &&
        left.RetentionPeriod == right.RetentionPeriod &&
        left.ExcludedApplications.SetEquals(right.ExcludedApplications);

    private async Task RunActionAsync(Func<Task> action)
    {
        try
        {
            await operations.WaitAsync();
            try { if (!exiting) await action(); }
            finally { operations.Release(); }
            UpdateTray();
            await RefreshTooltipAsync();
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task ExitAsync()
    {
        if (exiting) return;
        SavePlacement();
        exiting = true;
        await operations.WaitAsync();
        try
        {
            if (tracker != null) await tracker.DisposeAsync();
            Shutdown();
        }
        catch (Exception ex)
        {
            exiting = false;
            ReportError(ex);
        }
        finally { operations.Release(); }
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        try { tracker?.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { LogError(ex); }
    }

    private void OnTrackerStateChanged(TrackingStatus _) => Dispatcher.BeginInvoke(UpdateTray);
    private void OnTrackerError(Exception error) => Dispatcher.BeginInvoke(() => ReportError(error));
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => { if (uiResourcesLoaded) ApplyTheme(); UpdateTray(); });

    private void EnsureUiResources()
    {
        if (uiResourcesLoaded) return;
        var resources = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/WorkTimeTracker.App;component/UiResources.xaml")
        };
        var dictionaries = resources.MergedDictionaries.ToArray();
        resources.MergedDictionaries.Clear();
        foreach (var dictionary in dictionaries) Resources.MergedDictionaries.Add(dictionary);
        try
        {
            ApplyTheme();
            uiResourcesLoaded = true;
        }
        catch
        {
            foreach (var dictionary in dictionaries) Resources.MergedDictionaries.Remove(dictionary);
            throw;
        }
    }

    private void ApplyTheme()
    {
        if (settings == null) return;
        if (settings.Theme == ThemePreference.System) ApplicationThemeManager.ApplySystemTheme();
        else ApplicationThemeManager.Apply(settings.Theme == ThemePreference.Dark
            ? ApplicationTheme.Dark : ApplicationTheme.Light);
        if (settings.Accent == AccentPreference.System)
            ApplicationAccentColorManager.ApplySystemAccent();
        else
        {
            var color = settings.Accent switch
            {
                AccentPreference.Blue => System.Windows.Media.Color.FromRgb(0, 103, 192),
                AccentPreference.Green => System.Windows.Media.Color.FromRgb(16, 124, 65),
                AccentPreference.Purple => System.Windows.Media.Color.FromRgb(107, 70, 193),
                _ => throw new InvalidOperationException("Unknown accent preference.")
            };
            ApplicationAccentColorManager.Apply(color, ApplicationThemeManager.GetAppTheme(), false);
        }
    }

    private void UpdateTray()
    {
        if (tray == null) return;
        var state = tracker?.Status.State;
        dayMenu!.Text = T(state == TrackingState.DayEnded ? "StartDay" : "EndDay");
        pauseMenu!.Text = T(state == TrackingState.Paused ? "Resume" : "Pause");
        var old = trayIcon;
        var trayState = state;
        if (state == TrackingState.Tracking && (tracker?.LastSampleUtc is not { } last ||
            (DateTimeOffset.UtcNow - last).TotalSeconds > (settings?.Tracker.SampleInterval.TotalSeconds ?? 1) * 2 + 5))
            trayState = TrackingState.Faulted;
        trayIcon = CreateTrayIcon(trayState);
        tray.Icon = trayIcon;
        old?.Dispose();
    }

    private async Task RefreshTooltipAsync()
    {
        if (tray == null || tracker?.Status.WorkDayId is not { } id || settings == null) return;
        try
        {
            var result = await Task.Run(() =>
            {
                using var repository = new SqliteWorkTimeRepository(Path.Combine(dataDirectory, "tracking.sqlite3"));
                var raw = repository.GetRawData(id);
                var report = CalculationEngine.Calculate(raw, settings.Tracker, DateTimeOffset.UtcNow);
                return (raw.WorkDay.StartUtc.ToLocalTime(), report.TotalActiveTime);
            });
            trayTooltip = $"WorkTimeTracker\n{string.Format(T("StartedAt"), result.Item1.ToString("HH:mm"))}\n" +
                string.Format(T("ActiveSoFar"), $"{(int)result.Item2.TotalHours}:{result.Item2.Minutes:00}");
            if (tray != null && !trayMenuOpen) tray.Text = trayTooltip;
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private static Icon CreateTrayIcon(TrackingState? state)
    {
        var lightTaskbar = (int?)Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 1) != 0;
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = lightTaskbar ? Color.Black : Color.White;
        using var pen = new Pen(color, 2.5f);
        graphics.DrawEllipse(pen, 4, 4, 24, 24);
        if (state == TrackingState.Paused)
        {
            graphics.DrawLine(pen, 13, 10, 13, 22);
            graphics.DrawLine(pen, 19, 10, 19, 22);
        }
        else
        {
            graphics.DrawLine(pen, 16, 8, 16, 16);
            graphics.DrawLine(pen, 16, 16, 22, 19);
        }
        if (state is not (TrackingState.Tracking or TrackingState.Paused))
        {
            using var brush = new SolidBrush(Color.Red);
            graphics.FillEllipse(brush, 21, 21, 10, 10);
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    private void ReportError(Exception error)
    {
        LogError(error);
        System.Windows.MessageBox.Show(T("Error"), "WorkTimeTracker",
            MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK,
            System.Windows.MessageBoxOptions.RightAlign | System.Windows.MessageBoxOptions.RtlReading);
    }

    private void LogError(Exception error)
    {
        Directory.CreateDirectory(dataDirectory);
        File.AppendAllText(Path.Combine(dataDirectory, "worktimetracker.log"),
            $"{DateTimeOffset.UtcNow:O} {error}{Environment.NewLine}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tooltipTimer?.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        stoppingActivation?.Set();
        activationTask?.GetAwaiter().GetResult();
        tray?.Dispose();
        trayIcon?.Dispose();
        if (tracker != null)
        {
            try { tracker.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { LogError(ex); }
        }
        stoppingActivation?.Dispose();
        activate?.Dispose();
        if (instance != null)
        {
            try { instance.ReleaseMutex(); }
            catch (ApplicationException) { }
            instance.Dispose();
        }
        base.OnExit(e);
    }
}
