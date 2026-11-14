using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using UpdateExpress.Models;
using UpdateExpress.Services;
namespace UpdateExpress;
public partial class MainWindow : Window
{
    public ObservableCollection<UpdateEntry> Entries { get; } = new();
    private ReminderSchedule _schedule = new();
    private readonly ICollectionView _entriesView;
    private string _activeTab = "software";
    private bool _showingHistory;
    public ObservableCollection<AppUpdate> AppEntries { get; } = new();
    public bool AutomaticAppsEnabled => AutoApplications?.IsChecked == true;
    private readonly ObservableCollection<AppUpdate> _appEntries;
    private string _rawWinget = "";
    private DateTimeOffset? _appliedWingetCacheAt;
    private UpdatePolicy _policy = UpdatePolicy.Load();
    private DateTime? _lastSuccessfulScan;
    private readonly ObservableCollection<UpdateEntry> _pendingSnapshot = new();
    private List<UpdateEntry> _historySnapshot = new();
    private DateTime? _pendingSnapshotAt;
    private readonly DispatcherTimer _pendingReconcileTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private int _reconcileRunning;
    private DateTime _lastReconcileAttempt = DateTime.MinValue;
    private bool VisibleInTab(object item)
    {
        if (item is not UpdateEntry e) return false;
        if (_activeTab == "history") return true;
        return _activeTab switch
        {
            "software" => e.Category != "Driver / firmware",
            "drivers" => e.Category == "Driver / firmware",
            _ => false
        };
    }
    public bool CanSelectDisplayedEntry => _activeTab == "history" || _activeTab == "drivers" || _activeTab == "software";
    private bool _busy;
    private bool _scanButtonSearching;
    private bool _historyActionsEnabled = true;
    private bool _healthCheckRunning;
    private bool _diagnosticsRunning;
    private FixItWindow? _fixItWindow;
    private readonly DispatcherTimer _fixItIconTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _showingClickItIcon;
    private bool _fixItIconAnimating;
    private static bool IsManuallyInstallableDriver(UpdateEntry e) =>
        e.Category == "Driver / firmware" &&
        !string.IsNullOrWhiteSpace(e.UpdateId) &&
        !string.IsNullOrWhiteSpace(e.DriverHardwareId);

    private void UpdateSelectionControls()
    {
        var visible = Entries.Where(e => VisibleInTab(e)).ToList();
        var eligible = visible.Where(e => !_showingHistory && (_activeTab == "drivers" ? IsManuallyInstallableDriver(e) : e.SafeDefault) && !string.IsNullOrWhiteSpace(e.UpdateId)).ToList();
        var selected = eligible.Count(e => e.IsSelected);
        SelectAllUpdates.IsEnabled = !_busy && (_activeTab == "apps" ? _appEntries.Count > 0 : eligible.Count > 0);
        SelectAllUpdates.IsChecked = _activeTab == "apps" ? _appEntries.Count > 0 && _appEntries.All(a => a.IsSelected) : eligible.Count > 0 && selected == eligible.Count;
        int appCount = _appEntries.Count(a => a.IsSelected);
        SelectedCountText.Text = _schedule.Language == "no" ? $"{(_activeTab == "apps" ? appCount : selected)} valgt" : $"{(_activeTab == "apps" ? appCount : selected)} selected";
        InstallButton.IsEnabled = !_busy && !_showingHistory && (_pendingSnapshot.Any(e => e.IsSelected && !string.IsNullOrWhiteSpace(e.UpdateId)) || appCount > 0);
    }
    private void UpdateCheckbox_Click(object sender, RoutedEventArgs e) => UpdateSelectionControls();
    private void SelectAllUpdates_Click(object sender, RoutedEventArgs e)
    {
        bool value = SelectAllUpdates.IsChecked == true;
        if (_activeTab == "apps") foreach (var app in _appEntries) app.IsSelected = value;
        else foreach (var item in Entries.Where(e => VisibleInTab(e) && (!_showingHistory && (_activeTab == "drivers" ? IsManuallyInstallableDriver(e) : e.SafeDefault) && !string.IsNullOrWhiteSpace(e.UpdateId)))) item.IsSelected = value;
        UpdatesGrid.Items.Refresh(); AppsGrid.Items.Refresh();
        UpdateSelectionControls();
    }
    private void SetTab(string tab)
    {
        _activeTab = tab;
        _showingHistory = tab == "history";
        // Entries is the visible table only. Keep pending and history in separate snapshots.
        // Never reuse history rows when returning to Windows Updates / Drivers.
        Entries.Clear();
        if (_showingHistory)
        {
            // Keep raw Date on the same objects (EventKey / hide / Fixed). Format only DateDisplay for the grid.
            foreach (var entry in _historySnapshot)
            {
                entry.DateDisplay = _schedule.FormatDateValue(entry.Date);
                Entries.Add(entry);
            }
        }
        else
        {
            foreach (var entry in _pendingSnapshot)
            {
                entry.DateDisplay = string.IsNullOrWhiteSpace(entry.Date) ? "" : _schedule.FormatDateValue(entry.Date);
                Entries.Add(entry);
            }
        }
        _entriesView.Refresh();
        // All tabs share the same visible column order. Empty values remain blank.
        SoftwareTab.FontWeight = DriversTab.FontWeight = HistoryTab.FontWeight = AppsTab.FontWeight = FontWeights.Normal;
        PaintResultTab(SoftwareTab, tab == "software");
        PaintResultTab(DriversTab, tab == "drivers");
        PaintResultTab(AppsTab, tab == "apps");
        PaintResultTab(HistoryTab, tab == "history");
        WindowsUpdatesTable.Visibility = tab == "apps" ? Visibility.Collapsed : Visibility.Visible;
        AppsActions.Visibility = tab == "apps" ? Visibility.Visible : Visibility.Collapsed;
        AppsToolbar.Visibility = tab == "apps" ? Visibility.Visible : Visibility.Collapsed;
        SelectAllUpdates.Visibility = Visibility.Visible;
        SelectedCountText.Visibility = Visibility.Visible;
        HistoryPdfButton.Visibility = tab == "history" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesGrid.Items.Refresh();
        UpdateSelectionControls();
        if (_languageReady) ApplyLanguage();
        RefreshFeatureUpdateButton();
    }
    private static void PaintResultTab(Button button, bool selected)
    {
        if (selected)
        {
            button.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1B4F86"));
            button.Foreground = System.Windows.Media.Brushes.White;
            button.BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#163E6B"));
            return;
        }
        button.ClearValue(BackgroundProperty);
        button.ClearValue(ForegroundProperty);
        button.ClearValue(BorderBrushProperty);
    }
    public static readonly System.Windows.DependencyProperty InstallButtonActiveProperty =
        System.Windows.DependencyProperty.Register(nameof(InstallButtonActive), typeof(bool), typeof(MainWindow), new System.Windows.PropertyMetadata(false));
    public bool InstallButtonActive
    {
        get => (bool)GetValue(InstallButtonActiveProperty);
        set => SetValue(InstallButtonActiveProperty, value);
    }
    private void SoftwareTab_Click(object sender, RoutedEventArgs e)
    {
        SetTab("software");
        HidePendingAlreadyInHistory(_historySnapshot);
    }
    private void DriverMode_Changed(object sender, RoutedEventArgs e)
    {
        // The mode changes the WUA query, not just the UI. A fresh scan is required.
        if (!IsLoaded || StatusText is null) return;
        StatusText.Text = "Driver mode changed. Click Check for Updates to refresh WUA results.";
    }
    private async void PmeControl_Click(object sender, RoutedEventArgs e)
    {
        const string service = "PME.Agent.PmeService";
        try
        {
            var state = await Task.Run(() => Services.PmeServiceControl.ReadState(service));
            if (!state.Exists)
            {
                MessageBox.Show(this, "N-able PME service not detected.", "PME Control", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var question = $"PME service: {state.State}\nStartup: {state.StartMode}\n\n" +
                "YES: Stop PME and disable service startup.\nNO: Restore Automatic startup and start PME.\nCANCEL: Make no changes.\n\n" +
                "This is a LOCAL temporary change. N-able RMM policy may override it. " +
                "Windows Update may start downloading or installing updates after PME is stopped. " +
                "This action does NOT change Windows Update registry policies.";
            var answer = MessageBox.Show(this, question, "N-able PME Control (administrator required)", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.Cancel) return;
            PmeControlButton.IsEnabled = false;
            try
            {
                // Elevation is requested by Windows if the app is not already elevated.
                await Task.Run(() => Services.PmeServiceControl.SetEnabled(service, answer == MessageBoxResult.No));
                var after = await Task.Run(() => Services.PmeServiceControl.ReadState(service));
                MessageBox.Show(this, $"PME state: {after.State}\nStartup: {after.StartMode}\n\nCheck RMM policy before relying on this setting.", "PME Control", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally { PmeControlButton.IsEnabled = true; }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "PME Control error", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void DriversTab_Click(object sender, RoutedEventArgs e)
    {
        SetTab("drivers");
        HidePendingAlreadyInHistory(_historySnapshot);
    }
    private void AppsTab_Click(object sender, RoutedEventArgs e) { SetTab("apps"); UpdateAppSelection(); }
    private void UpdateAppSelection()
    {
        int count = _appEntries.Count(a => a.IsSelected);

        UpdateSelectionControls();
    }
    private void AppCheckbox_Click(object sender, RoutedEventArgs e) => UpdateAppSelection();
    private void AutoApplications_Click(object sender, RoutedEventArgs e) => AppsGrid.Items.Refresh();
    private void AppAutoCheckbox_Click(object sender, RoutedEventArgs e)
    {
        // Only persist after Save Policy; checkbox changes never start installation.
        AppsGrid.CommitEdit();
    }
    private void LoadAppPreferences(IEnumerable<AppUpdate> updates)
    {
        var ids = _policy.AutomaticApplicationIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in updates)
        {
            app.AutoUpdate = ids.Contains(app.Id);
            _appEntries.Add(app);
        }
    }

    private void ApplyAppScanResult(IReadOnlyList<AppUpdate> updates, string? raw, DateTimeOffset? cacheWrittenAt = null)
    {
        if (raw is not null) _rawWinget = raw;
        _appEntries.Clear();
        LoadAppPreferences(updates);
        if (cacheWrittenAt is DateTimeOffset at)
            _appliedWingetCacheAt = at;
        else
            _appliedWingetCacheAt = DateTimeOffset.Now;
        RefreshHealthSummary();
        UpdateAppSelection();
    }

    /// <summary>Pull tray/automation WinGet snapshot into the Applications grid when newer than last apply.</summary>
    private void TrySyncAppsFromWingetCache()
    {
        if (_busy) return;
        if (!WingetAvailableCache.TryRead(out var writtenAt, out var updates, out var raw)) return;
        if (_appliedWingetCacheAt is DateTimeOffset applied && writtenAt <= applied) return;
        ApplyAppScanResult(updates, raw, writtenAt);
        if (updates.Count == 0)
            CategoryPromptState.Clear("applications");
    }

    private void ReconcileApplicationsPromptWithScan(IReadOnlyList<AppUpdate> remaining)
    {
        try
        {
            var prompt = CategoryPromptState.Load("applications");
            if (prompt.Updates.Count == 0)
            {
                if (remaining.Count == 0) CategoryPromptState.Clear("applications");
                return;
            }

            var still = prompt.Updates
                .Where(u => remaining.Any(r => r.Id.Equals(u.UpdateId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (still.Count == 0)
            {
                CategoryPromptState.Clear("applications");
                return;
            }

            if (still.Count != prompt.Updates.Count)
            {
                prompt.Updates = still;
                prompt.Save();
            }
        }
        catch { /* prompt reconcile is best-effort */ }
    }

    private async void AppsScan_Click(object sender, RoutedEventArgs e)
    {
        ScanProgressText.Text = "Scanning applications with WinGet…";
        ScanProgressText.Text = "Scanning applications (WinGet)…";
        ScanProgressText.Visibility = Visibility.Visible;
        AppsScanButton.IsEnabled = false;
        try
        {
            var result = await WingetReader.ScanAsync();
            WingetAvailableCache.Write(result.Updates, result.Raw);
            ApplyAppScanResult(result.Updates, result.Raw);
            ReconcileApplicationsPromptWithScan(result.Updates);
            HealthSummary.Text = CategorySummary() + $"\nHistory: {_historySnapshot.Count}  |  Run Check Status for detailed history.";
            StatusText.Text = $"WinGet: {result.Updates.Count} available updates.";
        }
        catch (Exception ex) { MessageBox.Show(this, "WinGet error: " + ex.Message, "WinGet", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { AppsScanButton.IsEnabled = true; UpdateAppSelection(); ScanProgressText.Visibility = Visibility.Collapsed; }
    }
    private async void AppsInstall_Click(object sender, RoutedEventArgs e)
    {
        var selected = _appEntries.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0) return;
        var names = string.Join("\n", selected.Select(a => $"• {a.Name} ({a.Id}): {a.Installed} → {a.Available}"));
        if (AppDialog.Show(this, $"Install {selected.Count} selected application updates?\n\n{names}\n\nSave your work and close affected applications. Remote support software may disconnect. No automatic restart will be initiated by Update Express.", "Confirm WinGet Installation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true; AppsScanButton.IsEnabled = false; UpdateAppSelection();
        var logs = new List<string>();
        var activityRunId = Guid.NewGuid().ToString("N");
        ActivityLog.TryWrite("WinGetUpgrade", "Started", selected.Select(a => new { a.Name, a.Id, a.Installed, a.Available }).ToArray(), runId: activityRunId);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateExpress");
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "winget-install-history.jsonl");
        try
        {
            foreach (var app in selected)
            {
                ScanProgressText.Text = $"Installing {app.Name}…";
                int code; string output;
                try
                {
                    using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(20));
                    (code, output) = await WingetReader.UpgradeAsync(app.Id, timeout.Token);
                }
                catch (Exception ex) { code = -1; output = ex.Message; }
                var record = new { Timestamp = DateTimeOffset.Now, app.Name, app.Id, app.Installed, app.Available, ExitCode = code, Output = output };
                File.AppendAllText(logPath, JsonSerializer.Serialize(record) + Environment.NewLine, Encoding.UTF8);
                ActivityLog.TryWrite("WinGetUpgrade", code == 0 ? "Command completed - verification pending" : "Failed", new { app.Name, app.Id, app.Installed, app.Available, ExitCode = code, Output = output }, source: "WinGet", runId: activityRunId);
                logs.Add($"{app.Name}: {(code == 0 ? "Command completed" : $"Failed (exit {code})")}");
            }

            ScanProgressText.Text = "Refreshing WinGet results…";
            try
            {
                var scan = await WingetReader.ScanAsync();
                WingetAvailableCache.Write(scan.Updates, scan.Raw);
                ApplyAppScanResult(scan.Updates, scan.Raw);
                ReconcileApplicationsPromptWithScan(scan.Updates);
                ActivityLog.TryWrite("WinGetUpgrade", "Completed", new { Count = selected.Count, Remaining = scan.Updates.Count }, source: "WinGet", runId: activityRunId);
                MessageBox.Show(this, string.Join("\n", logs) + $"\n\nAvailable after refresh: {scan.Updates.Count}.\nLog: " + logPath, "WinGet Installation Results", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ActivityLog.TryWrite("WinGetUpgrade", "Completed - rescan failed", new { Count = selected.Count, Error = ex.Message }, source: "WinGet", runId: activityRunId);
                MessageBox.Show(this, string.Join("\n", logs) + "\n\nPost-install WinGet refresh failed: " + ex.Message + "\nLog: " + logPath, "WinGet Installation Results", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { _busy = false; AppsScanButton.IsEnabled = true; UpdateAppSelection(); }
    }
    private void AppsRaw_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_rawWinget)) { MessageBox.Show(this, "Run an application scan first."); return; }
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"UpdateExpress-winget-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, _rawWinget, new UTF8Encoding(true));
        MessageBox.Show(this, "Report saved: " + path);
    }
    private void SavePolicy_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DefenderIntervalMinutes.Text.Trim(), out int defenderMinutes) || defenderMinutes < 15)
        {
            MessageBox.Show(this, "Defender interval must be at least 15 minutes.", "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(CriticalIntervalMinutes.Text.Trim(), out int criticalMinutes) || criticalMinutes < 60)
        {
            MessageBox.Show(this, "Critical interval must be at least 60 minutes.", "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(GeneralIntervalMinutes.Text.Trim(), out int generalMinutes) || generalMinutes < 1440)
        {
            MessageBox.Show(this, "General interval must be at least 1440 minutes (1 day).", "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TimeSpan.TryParseExact(CriticalPreferredTime.Text.Trim(), @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out _) ||
            !TimeSpan.TryParseExact(GeneralPreferredTime.Text.Trim(), @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            MessageBox.Show(this, "Preferred times must be HH:mm, for example 12:00.", "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(ActiveHoursStart.Text.Trim(), out int activeStart) || activeStart < 0 || activeStart > 23 ||
            !int.TryParse(ActiveHoursEnd.Text.Trim(), out int activeEnd) || activeEnd < 0 || activeEnd > 23)
        {
            MessageBox.Show(this, "Active hours must be integers from 0 to 23.", "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(RebootCountdownSeconds.Text.Trim(), out int countdown) || countdown < 60 || countdown > 3600)
        {
            MessageBox.Show(this, "Reboot countdown must be between 60 and 3600 seconds.", "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _policy.WindowsAutomation.Enabled = WindowsAutomationEnabled.IsChecked == true;
        _policy.WindowsAutomation.Defender.Enabled = DefenderAutoEnabled.IsChecked == true;
        _policy.WindowsAutomation.Defender.IntervalMinutes = defenderMinutes;
        _policy.WindowsAutomation.Defender.Mode = "autoInstall";
        _policy.WindowsAutomation.Critical.Enabled = CriticalAutoEnabled.IsChecked == true;
        _policy.WindowsAutomation.Critical.IntervalMinutes = criticalMinutes;
        _policy.WindowsAutomation.Critical.PreferredInstallTime = CriticalPreferredTime.Text.Trim();
        _policy.WindowsAutomation.Critical.Mode = "downloadAndPrompt";
        _policy.WindowsAutomation.General.Enabled = GeneralAutoEnabled.IsChecked == true;
        _policy.WindowsAutomation.General.IntervalMinutes = generalMinutes;
        _policy.WindowsAutomation.General.PreferredInstallTime = GeneralPreferredTime.Text.Trim();
        _policy.WindowsAutomation.General.Mode = "downloadAndPrompt";
        _policy.WindowsAutomation.AutoReboot.Enabled = AutoRebootEnabled.IsChecked == true;
        _policy.WindowsAutomation.AutoReboot.Mode = AutoRebootOutsideOption.IsChecked == true ? "outsideActiveHours" : "confirm";
        _policy.WindowsAutomation.AutoReboot.ActiveHoursStart = activeStart;
        _policy.WindowsAutomation.AutoReboot.ActiveHoursEnd = activeEnd;
        _policy.WindowsAutomation.AutoReboot.CountdownSeconds = countdown;
        _policy.WindowsAutomation.NeverAutoReboot = !_policy.WindowsAutomation.AutoReboot.Enabled;

        _policy.AutomaticDrivers = AutoDrivers.IsChecked == true;
        _policy.AutomaticApplications = AutoApplications.IsChecked == true;
        _policy.AutomaticApplicationIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _appEntries)
        {
            if (app.AutoUpdate) _policy.AutomaticApplicationIds.Add(app.Id);
            else _policy.AutomaticApplicationIds.Remove(app.Id);
        }
        _policy.Save();
        RefreshAutomationStatusTexts();
        MessageBox.Show(this,
            "Windows automation policy saved.\n\n" +
            "Defender (K1) can auto-install via the background agent.\n" +
            "Critical (K2) and General (K3) download in the background and ask before install.\n" +
            "Drivers: " + (_policy.AutomaticDrivers ? "automatic (download + ask; firmware manual)" : "manual only") + ".\n" +
            "Applications: " + (_policy.AutomaticApplications ? "automatic (WinGet ask before install)" : "manual only") + ".\n" +
            "Optional auto-reboot: " + (_policy.WindowsAutomation.AutoReboot.Enabled
                ? (_policy.WindowsAutomation.AutoReboot.Mode == "outsideActiveHours"
                    ? "outside active hours"
                    : "ask for confirmation")
                : "off") + ".",
            "Update Express");
    }

    private void LoadAutomationPolicyIntoUi()
    {
        _policy.Normalize();
        var wa = _policy.WindowsAutomation;
        WindowsAutomationEnabled.IsChecked = wa.Enabled;
        DefenderAutoEnabled.IsChecked = wa.Defender.Enabled;
        DefenderIntervalMinutes.Text = wa.Defender.IntervalMinutes.ToString();
        CriticalAutoEnabled.IsChecked = wa.Critical.Enabled;
        CriticalIntervalMinutes.Text = wa.Critical.IntervalMinutes.ToString();
        CriticalPreferredTime.Text = string.IsNullOrWhiteSpace(wa.Critical.PreferredInstallTime) ? AutomationSchedule.DefaultPreferredTime : wa.Critical.PreferredInstallTime;
        GeneralAutoEnabled.IsChecked = wa.General.Enabled;
        GeneralIntervalMinutes.Text = wa.General.IntervalMinutes.ToString();
        GeneralPreferredTime.Text = string.IsNullOrWhiteSpace(wa.General.PreferredInstallTime) ? AutomationSchedule.DefaultPreferredTime : wa.General.PreferredInstallTime;
        AutoRebootEnabled.IsChecked = wa.AutoReboot.Enabled;
        if (string.Equals(wa.AutoReboot.Mode, "outsideActiveHours", StringComparison.OrdinalIgnoreCase))
            AutoRebootOutsideOption.IsChecked = true;
        else
            AutoRebootConfirmOption.IsChecked = true;
        ActiveHoursStart.Text = wa.AutoReboot.ActiveHoursStart.ToString();
        ActiveHoursEnd.Text = wa.AutoReboot.ActiveHoursEnd.ToString();
        RebootCountdownSeconds.Text = wa.AutoReboot.CountdownSeconds.ToString();
        AutoDrivers.IsChecked = _policy.AutomaticDrivers;
        AutoApplications.IsChecked = _policy.AutomaticApplications;
        RefreshAutomationStatusTexts();
    }

    private void RefreshAutomationStatusTexts()
    {
        var status = WindowsAutomationStatus.Load();
        string Format(CategoryAutomationStatus item, string pendingNote)
        {
            if (item.LastRun is null) return "Status: " + pendingNote;
            string next = item.NextDue?.ToString("dd.MM HH:mm") ?? "—";
            return $"Status: {item.LastResult} · last {item.LastRun:dd.MM HH:mm} · next {next}";
        }
        DefenderAutomationStatus.Text = Format(status.Defender, "waiting for agent");
        CriticalAutomationStatus.Text = Format(status.Critical, "waiting for agent");
        GeneralAutomationStatus.Text = Format(status.General, "waiting for agent");
        AutomationStatusText.Text =
            $"Windows automation: Defender={status.Defender.LastResult}" +
            $"  |  Critical={status.Critical.LastResult}" +
            $"  |  General={status.General.LastResult}" +
            $"  |  Drivers={status.Drivers.LastResult}" +
            $"  |  Apps={status.Applications.LastResult}";
        AutomationStatusText.ToolTip =
            $"Defender: {status.Defender.Detail}\nCritical: {status.Critical.Detail}\nGeneral: {status.General.Detail}\nDrivers: {status.Drivers.Detail}\nApplications: {status.Applications.Detail}";
    }
    private async void HistoryTab_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync(WindowsUpdateReader.HistoryForDisplay);
        if (_showingHistory) RefreshHealthSummary();
    }
    private async void Health_Click(object sender, RoutedEventArgs e) => await RunHealthCheckAsync();

    private async Task RunHealthCheckAsync()
    {
        if (_healthCheckRunning) return;
        _healthCheckRunning = true;
        SetFixItHealthEnabled(false);
        HealthSummary.Text = "Reading update history and pending updates…";
        try
        {
            // Separate STA calls: no COM objects cross thread boundaries.
            var history = await WindowsUpdateReader.RunStaAsync(WindowsUpdateReader.HistoryForDisplay);
            var pending = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(true, true));
            _historySnapshot = history;
            ReplacePendingSnapshot(pending);
            RefreshHealthSummary();
            StatusText.Text = "Status check complete. No installation performed.";
            HidePendingAlreadyInHistory(history);
        }
        catch (Exception ex) { HealthSummary.Text = "Status check failed: " + ex.Message; }
        finally
        {
            _healthCheckRunning = false;
            SetFixItHealthEnabled(true);
        }
    }
    private string CategorySummary()
    {
        int windows = _pendingSnapshot.Count(x => x.Category != "Driver / firmware" &&
            (!string.IsNullOrEmpty(x.UpdateId) || x.Source == FeatureUpdateOfferReader.Source));
        int drivers = _pendingSnapshot.Count(x => x.Category == "Driver / firmware" && !string.IsNullOrEmpty(x.UpdateId));
        return $"Windows: {windows} available  |  Drivers: {drivers} available  |  Applications: {_appEntries.Count} available";
    }

    private void RefreshHealthSummary()
    {
        if (_historySnapshot.Count == 0 && _pendingSnapshot.Count == 0)
        {
            HealthSummary.Text = CategorySummary() + "\nHistory not checked yet.";
            return;
        }
        var failures = _historySnapshot.Where(x => x.Status is "Mislykket" or "Avbrutt").ToList();
        var success = _historySnapshot.Where(x => x.Status == "Vellykket").ToList();
        var latestSuccess = success.Select(x => ReminderSchedule.TryParseDate(x.Date) ?? DateTime.MinValue).DefaultIfEmpty().Max();
        var pendingKbs = _pendingSnapshot.SelectMany(x => SplitKbs(x.KB)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int fixedCount = _historySnapshot.Count(x => x.IsHistoryFixed);
        int laterSuccess = 0, stillPending = 0, unresolved = 0;
        foreach (var failure in failures)
        {
            var keys = SplitKbs(failure.KB).ToArray();
            if (keys.Length == 0) { unresolved++; continue; }
            var failureDate = ReminderSchedule.TryParseDate(failure.Date) ?? DateTime.MinValue;
            if (keys.Any(k => pendingKbs.Contains(k))) { stillPending++; continue; }
            if (success.Any(x => (ReminderSchedule.TryParseDate(x.Date) ?? DateTime.MinValue) > failureDate && SplitKbs(x.KB).Intersect(keys, StringComparer.OrdinalIgnoreCase).Any())) laterSuccess++;
            else unresolved++;
        }
        HealthSummary.Text = CategorySummary() + $"\nHistory: {_historySnapshot.Count}  |  Failures: {failures.Count}  |  Fixed: {fixedCount}  |  Resolved KB: {laterSuccess}  |  Unresolved: {unresolved}";
        HealthSummary.ToolTip = "History keeps events saved before a Windows Update fix. Fixed means a later success for the same KB, or the update you fixed is no longer offered. Matching KB numbers do not prove the original error was repaired.";
    }

    private void ReplacePendingSnapshot(IEnumerable<UpdateEntry> items)
    {
        _pendingSnapshot.Clear();
        foreach (var item in items) _pendingSnapshot.Add(item);
        _pendingSnapshotAt = _pendingSnapshot.Count == 0 ? null : DateTime.Now;
        RefreshFeatureUpdateButton();
    }

    private void RefreshFeatureUpdateButton()
    {
        if (OpenWindowsUpdateButton is null) return;
        bool show = _activeTab == "software" && _pendingSnapshot.Any(entry => entry.Source == FeatureUpdateOfferReader.Source);
        OpenWindowsUpdateButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenWindowsUpdate_Click(object sender, RoutedEventArgs e)
    {
        try { FeatureUpdateOfferReader.OpenWindowsUpdate(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>
    /// Read History and drop pending rows it already records as successful.
    /// Skips while a scan or install owns the pending list.
    /// </summary>
    private async Task ReconcilePendingFromHistoryAsync()
    {
        if (_busy || _pendingSnapshot.Count == 0 || _pendingSnapshotAt is null) return;
        if (DateTime.Now - _lastReconcileAttempt < TimeSpan.FromSeconds(15)) return;
        if (_healthCheckRunning) return;
        if (Interlocked.CompareExchange(ref _reconcileRunning, 1, 0) != 0) return;
        _lastReconcileAttempt = DateTime.Now;
        try
        {
            var history = await WindowsUpdateReader.RunStaAsync(WindowsUpdateReader.HistoryForDisplay);
            if (_busy || _pendingSnapshotAt is null) return;
            _historySnapshot = history;
            HidePendingAlreadyInHistory(history);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Pending history reconcile failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _reconcileRunning, 0);
        }
    }

    private int HidePendingAlreadyInHistory(IReadOnlyList<UpdateEntry> history)
    {
        if (_pendingSnapshotAt is not DateTime taken || history.Count == 0) return 0;
        var hide = _pendingSnapshot.Where(item => PendingHistoryReconcile.ShouldHide(item, history, taken)).ToList();
        if (hide.Count == 0) return 0;
        foreach (var item in hide) _pendingSnapshot.Remove(item);
        // Entries can hold an older copy of the pending rows. Rebuild it from the snapshot
        // unless History is on screen, so the Windows Updates grid drops the installed item.
        if (!_showingHistory)
        {
            Entries.Clear();
            foreach (var entry in _pendingSnapshot) Entries.Add(entry);
            _entriesView.Refresh();
        }
        RefreshHealthSummary();
        UpdateSelectionControls();
        if (StatusText is null) return hide.Count;
        bool norwegian = _schedule.Language == "no";
        StatusText.Text = hide.Count == 1
            ? (norwegian
                ? "1 oppdatering er fjernet fra Windows-oppdateringer fordi den allerede er vellykket i Historikk."
                : "1 update was removed from Windows Updates because History already shows it as successful.")
            : (norwegian
                ? $"{hide.Count} oppdateringer er fjernet fra Windows-oppdateringer fordi de allerede er vellykket i Historikk."
                : $"{hide.Count} updates were removed from Windows Updates because History already shows them as successful.");
        return hide.Count;
    }

    private static IEnumerable<string> SplitKbs(string? value) =>
        System.Text.RegularExpressions.Regex.Matches(value ?? "", @"KB\d{5,9}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Value.ToUpperInvariant()).Distinct();
    private async void DefenderRefresh_Click(object sender, RoutedEventArgs e) { await RefreshDefenderAsync(); await RefreshFirewallAsync(); }
    private async System.Threading.Tasks.Task RefreshDefenderAsync()
    {
        DefenderRefreshButton.IsEnabled = false;
        DefenderLabel.Text = "Checking…";
        try
        {
            var status = await UpdateExpress.Services.DefenderStatusReader.ReadAsync();
            DefenderShield.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(status.Level switch
            {
                "green" => "#168A57", "orange" => "#EA941B", "red" => "#D13C36", _ => "#9099A6"
            }));
            DefenderLabel.Text = status.Label;
            DefenderChecked.Text = $"Checked {status.CheckedAt:HH:mm}";
            System.Windows.Controls.ToolTipService.SetToolTip(DefenderShield, status.Details);
            System.Windows.Controls.ToolTipService.SetToolTip(DefenderLabel, status.Details);
        }
        catch (Exception ex) { DefenderLabel.Text = "Unknown"; DefenderChecked.Text = ex.Message; }
        finally { DefenderRefreshButton.IsEnabled = true; }
    }
    private async Task RefreshFirewallAsync()
    {
        var profiles = await FirewallStatusReader.ReadAsync();
        void Lamp(System.Windows.Controls.Border border, System.Windows.Controls.TextBlock text, string name)
        {
            var state = profiles.TryGetValue(name, out var value) ? value : null;
            border.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(state switch { true => "#168A57", false => "#D13C36", _ => "#9099A6" }));
            text.Text = state switch { true => "ON", false => "OFF", _ => "?" };
            border.ToolTip = $"{name} firewall: {text.Text}. This is the configured profile status, not necessarily the currently active network.";
        }
        Lamp(PrivateLamp, PrivateLampText, "Private"); Lamp(PublicLamp, PublicLampText, "Public"); Lamp(DomainLamp, DomainLampText, "Domain");
    }
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _monitorUiTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _uptimeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _bootedAt = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
    private static readonly TimeSpan LongUptimeThreshold = TimeSpan.FromDays(7);
    private readonly DispatcherTimer _rebootBlinkTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    private bool _rebootPrompt;
    private bool _windowsRebootPending;

    private string RebootBannerText()
    {
        bool norwegian = _schedule.Language == "no";
        if (_rebootPrompt)
            return norwegian ? "⚠  Klikk her for å starte på nytt" : "⚠  Click here to reboot";
        return norwegian ? "⚠  Omstart nødvendig" : "⚠  Reboot needed";
    }

    private void RefreshRebootNeededBanner()
    {
        if (RebootNeededBanner is null) return;
        bool needed = RebootPendingReader.IsRebootPending();
        _windowsRebootPending = needed;
        RebootNeededBanner.Visibility = needed ? Visibility.Visible : Visibility.Collapsed;
        bool norwegian = _schedule.Language == "no";
        RebootNeededBanner.ToolTip = needed
            ? (norwegian
                ? "Windows melder at omstart venter (" + RebootPendingReader.Detail() + "). Klikk for å starte på nytt."
                : "Windows reports a pending restart (" + RebootPendingReader.Detail() + "). Click to restart this PC.")
            : null;
        if (needed)
        {
            RebootNeededBanner.Text = RebootBannerText();
            RebootNeededBanner.Opacity = 1;
            if (!_rebootBlinkTimer.IsEnabled) _rebootBlinkTimer.Start();
        }
        else
        {
            _rebootBlinkTimer.Stop();
            _rebootPrompt = false;
            RebootNeededBanner.Opacity = 1;
            RebootNeededBanner.Text = RebootBannerText();
        }
        RefreshLongUptimeHint();
    }

    private void RefreshLongUptimeHint()
    {
        if (LongUptimeHint is null) return;
        var up = DateTime.Now - _bootedAt;
        if (up < TimeSpan.Zero) up = TimeSpan.Zero;
        bool show = up >= LongUptimeThreshold && !_windowsRebootPending;
        LongUptimeHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        bool norwegian = _schedule.Language == "no";
        int days = Math.Max(7, up.Days);
        LongUptimeHint.Text = norwegian
            ? $"PC-en har vært på i {days} dager. En omstart før installasjon anbefales."
            : $"This PC has been running for {days} days. A restart before installing updates is recommended.";
        LongUptimeHint.ToolTip = norwegian
            ? "Dette er en anbefaling, ikke et krav fra Windows. Klikk for å starte på nytt."
            : "This is a recommendation, not a Windows requirement. Click to restart this PC.";
    }

    private void RebootBlinkTimer_Tick(object? sender, EventArgs e)
    {
        if (RebootNeededBanner.Visibility != Visibility.Visible)
        {
            _rebootBlinkTimer.Stop();
            return;
        }
        _rebootPrompt = !_rebootPrompt;
        RebootNeededBanner.Text = RebootBannerText();
        RebootNeededBanner.Opacity = 1;
    }

    private void RebootNeededBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RebootNeededBanner.Visibility != Visibility.Visible) return;
        ConfirmAndRestart();
    }

    private void LongUptimeHint_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LongUptimeHint.Visibility != Visibility.Visible) return;
        ConfirmAndRestart();
    }

    private void ConfirmAndRestart()
    {
        bool norwegian = _schedule.Language == "no";
        var answer = AppDialog.Show(this,
            norwegian
                ? "Vil du starte denne datamaskinen på nytt nå?\n\nLagre arbeidet ditt først. Det du ikke har lagret, går tapt."
                : "Restart this computer now?\n\nSave your work first. Anything you have not saved will be lost.",
            norwegian ? "Omstart" : "Restart computer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/r /t 0",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, norwegian ? "Omstart" : "Restart computer",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshMonitorPanel()
    {
        var status = MonitorStatus.Read();
        if (status is null)
        {
            MonitorStateText.Text = "Background monitoring: no status yet";
            MonitorDetailsText.Text = "Install/start the background agent to see status";
            MonitorUpdatesText.Text = "Updates: —";
            MonitorElapsedText.Text = "";
            return;
        }
        MonitorStateText.Text = status.State switch
        {
            "scanning" => "Background monitoring: checking for updates …",
            "completed" => "Background monitoring: last check completed",
            "failed" => "Background monitoring: last check failed",
            _ => "Background monitoring: waiting for next check"
        };
        string last = status.LastCompleted?.ToString("dd.MM HH:mm") ?? "—";
        MonitorDetailsText.Text = $"Last: {last}  |  Available: {status.Pending}  |  New: {status.New}  |  Next scheduled: {status.NextScheduled:HH:mm}";
        MonitorUpdatesText.Text = status.Updates.Length == 0 ? "Updates: none recorded" : "Updates: " + string.Join("; ", status.Updates.Take(3));
        MonitorUpdatesText.ToolTip = status.Updates.Length == 0 ? "No recorded updates" : string.Join(Environment.NewLine, status.Updates);
        MonitorElapsedText.Text = status.State == "scanning" && status.Started.HasValue
            ? $"Checking: {(int)Math.Max(0, (DateTime.Now - status.Started.Value).TotalSeconds)} s"
            : status.State == "failed" ? (status.Error ?? "Error – see agent.log") : "";
    }

    public void ShowProxySettings()
    {
        var dialog = new ProxySettingsWindow { Owner = this };
        dialog.ShowDialog();
    }

    private void ProxyDetails_Click(object sender, RoutedEventArgs e) => ShowProxySettings();

    private readonly ConnectivityMonitor _connectivity = new();
    private void UpdateConnectivity(ConnectivityMonitor.Probe internet, ConnectivityMonitor.Probe update, string proxy)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => UpdateConnectivity(internet, update, proxy))); return; }
        InternetLamp.Fill = internet.Ok ? Brushes.ForestGreen : Brushes.OrangeRed;
        UpdateServerLamp.Fill = update.Ok ? Brushes.ForestGreen : Brushes.OrangeRed;
        InternetStatusText.Text = "Internet: " + internet.Message;
        UpdateServerStatusText.Text = "Microsoft TCP: " + (update.Message == "TCP OK" ? "OK" : update.Message);
        var wua = MonitorStatus.Read();
        bool wuaOk = wua?.LastCompleted is not null && wua.State != "failed";
        WuaLamp.Fill = wuaOk ? Brushes.ForestGreen : wua?.State == "failed" ? Brushes.OrangeRed : Brushes.Gray;
        WuaStatusText.Text = wuaOk ? "WUA: OK" : wua?.State == "failed" ? "WUA: Error" : "WUA: Not confirmed";
        WuaStatusText.ToolTip = wuaOk ? $"Last successful WUA check: {wua!.LastCompleted:dd.MM HH:mm}" :
            wua?.Error ?? "Waiting for successful background-agent check.";
        ProxyStatusText.Text = proxy;
        InternetStatusText.ToolTip = internet.Detail;
        UpdateServerStatusText.ToolTip = update.Detail;
        ProxyStatusText.ToolTip = "Proxy configuration from .NET. DNS/TCP/ICMP probes use direct connections and do not test the proxy. Windows Update may use WinHTTP or other proxy settings.";
    }
    private MachineIdentityReader.Identity _machineIdentity = new("—", "—", "—", "—", "—");
    private void RefreshUptime()
    {
        var up = DateTime.Now - _bootedAt;
        if (up < TimeSpan.Zero) up = TimeSpan.Zero;
        string text = $"{up.Days:00}:{up.Hours:00}:{up.Minutes:00}:{up.Seconds:00}";
        if (UptimeValue.Text != text)
        {
            UptimeValue.Text = text;
            FitIdentityBox(UptimeValue);
        }
        bool norwegian = _schedule.Language == "no";
        string when = _bootedAt.ToString("dd.MM.yyyy HH:mm:ss");
        UptimeValue.ToolTip = norwegian ? $"Sist omstart: {when}" : $"Last reboot: {when}";
        RefreshLongUptimeHint();
    }
    private string UptimeCopyText()
    {
        bool norwegian = _schedule.Language == "no";
        string when = _bootedAt.ToString("dd.MM.yyyy HH:mm:ss");
        string duration = UptimeValue.Text;
        return norwegian ? $"{duration} (sist omstart {when})" : $"{duration} (last reboot {when})";
    }
    private void ShowMachineIdentity(MachineIdentityReader.Identity identity)
    {
        _machineIdentity = identity;
        MachineManufacturerValue.Text = identity.Manufacturer;
        MachineModelValue.Text = identity.Model;
        MachineProductValue.Text = identity.Product;
        MachineSerialValue.Text = identity.Serial;
        MachineDeviceValue.Text = identity.DeviceName;
        foreach (var box in new System.Windows.Controls.TextBox[] { MachineManufacturerValue, MachineModelValue, MachineProductValue, MachineSerialValue, MachineDeviceValue, UptimeValue })
        {
            box.ToolTip = box.Text;
            FitIdentityBox(box);
        }
        RefreshUptime();
    }
    private static void FitIdentityBox(System.Windows.Controls.TextBox box)
    {
        string text = string.IsNullOrEmpty(box.Text) ? "—" : box.Text;
        double dpi = 1;
        try { dpi = System.Windows.Media.VisualTreeHelper.GetDpi(box).PixelsPerDip; } catch { }
        var formatted = new System.Windows.Media.FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface(box.FontFamily, box.FontStyle, FontWeights.SemiBold, FontStretches.Normal),
            box.FontSize,
            System.Windows.Media.Brushes.Black,
            dpi);
        box.Width = Math.Clamp(formatted.WidthIncludingTrailingWhitespace + 14, 28, 260);
    }
    private async Task LoadMachineIdentityAsync()
    {
        try
        {
            var identity = await Task.Run(MachineIdentityReader.Read);
            ShowMachineIdentity(identity);
        }
        catch { }
    }
    private void CopyMachineField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        string value = button.Name switch
        {
            "MachineModelCopy" => _machineIdentity.Model,
            "MachineProductCopy" => _machineIdentity.Product,
            "MachineSerialCopy" => _machineIdentity.Serial,
            "MachineDeviceCopy" => _machineIdentity.DeviceName,
            _ => ""
        };
        if (CopyIdentityText(value)) FlashCopied(button);
    }
    private static bool CopyIdentityText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text is "…" or "—") return false;
        try
        {
            System.Windows.Clipboard.SetDataObject(text, true);
            return true;
        }
        catch { return false; }
    }
    private void FlashCopied(Button button)
    {
        bool norwegian = _schedule.Language == "no";
        button.ToolTip = norwegian ? "Kopiert" : "Copied";
        var iconTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        iconTimer.Tick += (_, _) =>
        {
            iconTimer.Stop();
            button.ToolTip = _schedule.Language == "no" ? "Kopier verdien" : "Copy this value";
        };
        iconTimer.Start();
    }
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            StopFixItIconAnimation();
            if (_fixItWindow is null) return;
            _fixItWindow.SuppressDetach();
            try { _fixItWindow.Close(); } catch { /* ignore */ }
            _fixItWindow = null;
        };
        ShowMachineIdentity(MachineIdentityReader.ReadFast());
        AppSubtitle.Text = $"Windows Update Management · Background Monitoring v{ProductVersion.Display}";
        RefreshOsSupportStatus();
        _appEntries = AppEntries; DataContext = this;
        _entriesView = CollectionViewSource.GetDefaultView(Entries);
        _entriesView.Filter = VisibleInTab;
        UpdatesGrid.ItemsSource = _entriesView;
        SetTab("software");
        _schedule = ReminderSchedule.Load();
        DayChoice.SelectedIndex = _schedule.Weekday == 7 ? 0 : Math.Clamp(_schedule.Weekday, 0, 6) + 1;
        TimeChoice.Text = _schedule.Time;
        ApplyLanguage();
        RemindersEnabled.IsChecked = _schedule.Enabled;
        LoadAutomationPolicyIntoUi();
        RefreshScheduleStatus();
        _reminderTimer.Tick += (_, _) => CheckReminder();
        _reminderTimer.Start();
        _monitorUiTimer.Tick += (_, _) =>
        {
            ReloadDisplaySettings();
            RefreshMonitorPanel();
            RefreshAutomationStatusTexts();
            RefreshRebootNeededBanner();
            TrySyncAppsFromWingetCache();
        };
        _monitorUiTimer.Start();
        _uptimeTimer.Tick += (_, _) => RefreshUptime();
        _uptimeTimer.Start();
        RefreshUptime();
        _pendingReconcileTimer.Tick += (_, _) => { _ = ReconcilePendingFromHistoryAsync(); };
        _pendingReconcileTimer.Start();
        Activated += (_, _) =>
        {
            ReloadDisplaySettings();
            TrySyncAppsFromWingetCache();
            _ = ReconcilePendingFromHistoryAsync();
        };
        WingetAvailableCache.Changed += () =>
        {
            try { Dispatcher.BeginInvoke(new Action(TrySyncAppsFromWingetCache)); }
            catch { /* window may be closing */ }
        };
        TrySyncAppsFromWingetCache();
        _rebootBlinkTimer.Tick += RebootBlinkTimer_Tick;
        RefreshMonitorPanel();
        RefreshAutomationStatusTexts();
        RefreshRebootNeededBanner();
        Loaded += async (_, _) =>
        {
            StartFixItIconAnimation();
            CheckReminder();
            _ = LoadMachineIdentityAsync();
            await RefreshDefenderAsync();
            await RefreshFirewallAsync();
            RefreshRebootNeededBanner();
            // Quiet self-update check when the main window opens (prompt only if newer).
            AppUpdatePrompt.StartCheck(
                automatic: true,
                onUpdateInstallRequested: StartAppSelfUpdateInstall,
                balloon: null);
            if (UpdateUntilCurrent.Load().Active)
                await RunUpdateUntilCurrentAsync();
        };
        _connectivity.Updated += UpdateConnectivity;
        Loaded += (_, _) => _connectivity.Start();
        Closed += (_, _) => _connectivity.Dispose();
    }

    private void StartAppSelfUpdateInstall(AppUpdateCheckResult offer)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var progressUi = new SelfUpdateProgressWindow { Owner = this };
            progressUi.Show();
            try
            {
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text = "Downloading Update Express update…";
                progressUi.SetStage("Downloading Update Express update…");
                await AppUpdateInstaller.PrepareAndLaunchAsync(offer, stage =>
                {
                    AgentLog.Write("App update (UI): " + stage);
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            StatusText.Text = stage;
                            progressUi.SetStage(stage);
                        }));
                    }
                    catch { }
                }).ConfigureAwait(true);
                // LaunchSelfUpdate shuts down this process on success.
            }
            catch (Exception ex)
            {
                AgentLog.Write("App update install from main window failed: " + ex);
                AppUpdatePrompt.ReleaseCheckLock();
                try { progressUi.Close(); } catch { }
                StatusText.Text = "Update Express update failed.";
                MessageBox.Show(this,
                    "Could not install the Update Express update.\n\n" + ex.Message +
                    "\n\nThe installed version was not changed.",
                    "Update Express",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        });
    }
    private string _osSupportDetails = "";
    private void RefreshOsSupportStatus()
    {
        var info = WindowsSupportReader.Read();
        OsVersionLabel.Text = info.Name;
        OsSupportIcon.Text = info.Status switch { "supported" => "✔", "expired" => "✖", _ => "⚠" };
        OsSupportIcon.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                info.Status switch { "supported" => "#008B68", "expired" => "#C62828", _ => "#B7791F" })!);
        _osSupportDetails = info.Details;
        OsVersionLabel.ToolTip = info.Details;
        OsSupportIcon.ToolTip = info.Details;
        OsSupportInfo.ToolTip = "Show Windows support details";
    }
    private void OsSupportInfo_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, _osSupportDetails +
            "\n\nReference: https://learn.microsoft.com/windows/release-health/supported-versions-windows-client",
            "Windows Security Support", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private string FormatTime(DateTime time) => _schedule.Format(time);
    private void ReloadDisplaySettings()
    {
        var fresh = ReminderSchedule.Load();
        string dateFormat = string.IsNullOrWhiteSpace(fresh.DateFormat) ? "text" : fresh.DateFormat;
        bool changed = !string.Equals(fresh.Language, _schedule.Language, StringComparison.OrdinalIgnoreCase)
            || fresh.Use24HourClock != _schedule.Use24HourClock
            || !string.Equals(dateFormat, _schedule.DateFormat ?? "text", StringComparison.OrdinalIgnoreCase);
        if (!changed) return;
        _schedule.Language = fresh.Language;
        _schedule.Use24HourClock = fresh.Use24HourClock;
        _schedule.DateFormat = dateFormat;
        ApplyLanguage();
        RefreshScheduleStatus();
        // History Date/Time follows Settings date format + 12h/24h clock.
        if (_showingHistory)
            SetTab("history");
    }
    private bool _languageReady;
    private static readonly Dictionary<string, string> Norwegian = new(StringComparer.Ordinal)
    {
        ["Windows Update Status – Health Check"] = "Windows Update-status – helsesjekk", ["Check for Updates"] = "Søk etter oppdateringer", ["Open Windows Update"] = "Åpne Windows Update", ["Open Windows Update to install"] = "Åpne Windows Update for å installere",
        ["Manufacturer"] = "Produsent", ["Model"] = "Modell/nr", ["Product no."] = "Produktnr.", ["Serial no."] = "Serienr.", ["Device name"] = "Enhetsnavn", ["Uptime"] = "Oppetid",
        ["Copy"] = "Kopier",
        ["Install Selected Updates"] = "Installer valgte oppdateringer", ["Installation History"] = "Installasjonshistorikk",
        ["Windows Update fix"] = "Windows Update-fiks", ["WUA deep check"] = "WUA dybdesjekk",
        ["WUA status report"] = "WUA statusrapport", ["Error report"] = "Feilrapport",
        ["Reboot needed"] = "Omstart nødvendig",
        ["Export CSV"] = "Eksporter CSV", ["Diagnostics"] = "Diagnostikk", ["Check Status"] = "Kontroller status",
        ["Refresh"] = "Oppdater", ["Private"] = "Privat", ["Public"] = "Offentlig", ["Domain"] = "Domene",
        ["Check for Updates & Results"] = "Søk etter oppdateringer og resultater", ["Application Updates (WinGet)"] = "Programoppdateringer (WinGet)",
        ["Windows Updates"] = "Windows-oppdateringer", ["Drivers / Firmware"] = "Drivere / fastvare",
        ["Applications"] = "Programmer", ["History"] = "Historikk", ["Select All"] = "Velg alle",
        ["Select"] = "Velg", ["Category"] = "Kategori", ["Update"] = "Oppdatering",
        ["Status"] = "Status", ["Date / Time"] = "Dato / tid", ["Error Code"] = "Feilkode",
        ["Priority"] = "Prioritet", ["Version"] = "Versjon", ["Application"] = "Program", ["Package ID"] = "Pakke-ID", ["Installed"] = "Installert",
        ["Available"] = "Tilgjengelig", ["Check Application Updates (WinGet)"] = "Søk etter programoppdateringer (WinGet)",
        ["Export Raw WinGet Report"] = "Eksporter WinGet-rapport", ["Install Selected Applications"] = "Installer valgte programmer",
        ["Maintenance & Notifications – Pilot"] = "Vedlikehold og varsler – pilot", ["Weekday:"] = "Ukedag:",
        ["Time:"] = "Tid:", ["Time format:"] = "Klokkeformat:", ["Language:"] = "Språk:",
        ["Enable Reminders"] = "Aktiver påminnelser", ["Save Schedule"] = "Lagre tidsplan",
        ["Test Notification"] = "Test varsel", ["Notification History"] = "Varselhistorikk",
        ["Windows automation (3 categories)"] = "Windows-automatisering (3 kategorier)",
        ["Enable Windows automation"] = "Aktiver Windows-automatisering",
        ["1. Defender signatures"] = "1. Defender-signaturer",
        ["2. Critical updates"] = "2. Kritiske oppdateringer",
        ["3. General Windows updates"] = "3. Generelle Windows-oppdateringer",
        ["Drivers automatic"] = "Drivere automatisk",
        ["Application automatic"] = "Programmer automatisk",
        ["Firmware: manual only"] = "Fastvare: kun manuelt", ["Save Policy"] = "Lagre policy",
        ["Interval (minutes)"] = "Intervall (minutter)", ["Preferred time"] = "Foretrukket tid",
        ["Mode: Auto-install (K1)"] = "Modus: Auto-installasjon (K1)",
        ["Mode: Download + ask (soon)"] = "Modus: Last ned + spør (kommer)",
        ["Mode: Download + ask (K2)"] = "Modus: Last ned + spør (K2)",
        ["Mode: Download + ask (K3)"] = "Modus: Last ned + spør (K3)",
        ["Optional auto-reboot (K2/K3 only)"] = "Valgfri auto-omstart (kun K2/K3)",
        ["Allow optional auto-reboot after Critical/General installs (default off)"] =
            "Tillat valgfri auto-omstart etter kritisk/generell installasjon (standard av)",
        ["Ask for confirmation"] = "Spør om bekreftelse",
        ["Only outside active hours"] = "Kun utenfor arbeidstid",
        ["Active hours"] = "Arbeidstid",
        ["Countdown (sec)"] = "Nedtelling (sek)",
        ["Critical and general updates download in the background and ask before install. Default preferred time is 12:00. Auto-reboot is optional and off by default. Drivers and applications follow the same ask-before-install path when enabled below."] =
            "Kritiske og generelle oppdateringer lastes ned i bakgrunnen og spør før installasjon. Standard foretrukket tid er 12:00. Auto-omstart er valgfri og av som standard. Drivere og programmer følger samme spør-før-installasjon-løp når de er aktivert nedenfor.",
        ["Update until current"] = "Oppdater til ferdig",
        ["Searches, downloads, and installs Windows software updates, restarts when Windows requires it, then repeats until none remain. Drivers, firmware, and feature updates stay manual."] =
            "Søker, laster ned og installerer Windows-programvareoppdateringer, starter på nytt når Windows krever det, og gjentar til ingen gjenstår. Drivere, fastvare og funksjonsoppdateringer forblir manuelle.",
        ["Start update cycle"] = "Start oppdateringsrunden",
        ["Stop update cycle"] = "Stopp oppdateringsrunden",
        ["Not running"] = "Kjører ikke",
        ["Enabled"] = "Aktivert",
        ["Every day"] = "Hver dag", ["Monday"] = "Mandag", ["Tuesday"] = "Tirsdag", ["Wednesday"] = "Onsdag",
        ["Thursday"] = "Torsdag", ["Friday"] = "Fredag", ["Saturday"] = "Lørdag", ["Sunday"] = "Søndag"
    };
    private void ApplyLanguage()
    {
        _languageReady = true;
        bool norwegian = _schedule.Language == "no";
        void Translate(object? element)
        {
            if (element is System.Windows.Controls.ContentControl control && control.Content is string content)
            {
                if (control.Tag is not string original) { original = content; control.Tag = original; }
                var match = Norwegian.Keys.FirstOrDefault(k => original == k || original.EndsWith("  " + k, StringComparison.Ordinal));
                if (match != null) control.Content = norwegian ? original.Replace(match, Norwegian[match]) : original;
            }
            if (element is System.Windows.Controls.TextBlock block && block.Name is not ("DefenderLabel" or "DefenderChecked" or "HealthSummary" or "StatusText" or "ScheduleStatus" or "AppsInfo" or "AppsSelectedCount" or "SelectedCountText" or "AutomationStatusText" or "DefenderAutomationStatus" or "CriticalAutomationStatus" or "GeneralAutomationStatus" or "FooterSafetyText" or "RebootNeededBanner" or "LongUptimeHint" or "UpdateUntilCurrentStatus") && !string.IsNullOrEmpty(block.Text))
            {
                if (block.Tag is not string original) { original = block.Text; block.Tag = original; }
                if (Norwegian.TryGetValue(original, out var translated)) block.Text = norwegian ? translated : original;
            }
            if (element is System.Windows.Controls.DataGrid grid)
                foreach (var column in grid.Columns)
                {
                    if (column.Header is string header)
                    {
                        var english = Norwegian.FirstOrDefault(kv => kv.Value == header).Key ?? header;
                        column.Header = norwegian && Norwegian.TryGetValue(english, out var translated) ? translated : english;
                    }
                }
            if (element is System.Windows.DependencyObject obj)
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj); i++) Translate(System.Windows.Media.VisualTreeHelper.GetChild(obj, i));
        }
        Translate(this);
        string copyTip = norwegian ? "Kopier verdien" : "Copy this value";
        MachineModelCopy.ToolTip = copyTip;
        MachineProductCopy.ToolTip = copyTip;
        MachineSerialCopy.ToolTip = copyTip;
        MachineDeviceCopy.ToolTip = copyTip;
        if (RebootNeededBanner is not null) RebootNeededBanner.Text = RebootBannerText();
        RefreshLongUptimeHint();
        ListHeading.Text = norwegian ? "Søk etter oppdateringer og resultater" : "Check for Updates & Results";
        RefreshScheduleStatus();
        RefreshUpdateUntilCurrentUi();
        // Translate() restores ScanButton from Tag; keep the busy label while a scan is running.
        if (_scanButtonSearching)
            ScanButton.Content = norwegian ? "Søker...." : "Searching....";
        if (FixItOpenButton is not null)
            FixItOpenButton.ToolTip = norwegian
                ? "Åpne FIX IT! – reparer og diagnostiser Windows Update-problemer"
                : "Open FIX IT! – repair and diagnose Windows Update problems";
        _fixItWindow?.ApplyLanguage(norwegian);
    }
    private void RefreshScheduleStatus()
    {
        bool no = _schedule.Language == "no";
        ScheduleStatus.Text = _schedule.Enabled
            ? (no ? $"Påminnelser aktivert. Neste: {FormatTime(_schedule.NextOccurrence(DateTime.Now))}. Hold programmet åpent." : $"Reminders enabled. Next: {FormatTime(_schedule.NextOccurrence(DateTime.Now))}. Keep the application running.")
            : (no ? "Påminnelser deaktivert. Du kan teste varsler uten å endre Windows." : "Reminders disabled. You can test notifications without modifying Windows.");
    }
    private void SaveSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (!TimeSpan.TryParseExact(TimeChoice.Text.Trim(), @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var at) || at >= TimeSpan.FromDays(1))
        {
            MessageBox.Show(this, "Enter time as HH:mm, for example 20:00.", "Invalid time", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _schedule.Weekday = DayChoice.SelectedIndex == 0 ? 7 : DayChoice.SelectedIndex - 1;
        _schedule.Time = TimeChoice.Text.Trim();
        _schedule.Enabled = RemindersEnabled.IsChecked == true;
        _schedule.LastPromptedOccurrence = null;
        _schedule.ConfirmedOccurrence = null;
        _schedule.SnoozedUntil = null;
        _schedule.Save();
        ReminderSchedule.Log("schedule_saved", $"Day={_schedule.Weekday}; Time={_schedule.Time}; Enabled={_schedule.Enabled}");
        RefreshScheduleStatus();
    }
    private void TestReminder_Click(object sender, RoutedEventArgs e) => ShowReminder(true, DateTime.Now);
    private void ReminderHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(ReminderSchedule.EventsPath))
        {
            MessageBox.Show(this, "No notification history yet.", "Notification History"); return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{ReminderSchedule.EventsPath}\"") { UseShellExecute = true });
    }
    private void CheckReminder()
    {
        if (!_schedule.Enabled) return;
        var now = DateTime.Now;
        if (_schedule.SnoozedUntil is DateTime snooze && now < snooze) return;
        var todayIndex = ((int)now.DayOfWeek + 6) % 7;
        if (_schedule.Weekday != 7 && todayIndex != _schedule.Weekday) return;
        if (!TimeSpan.TryParseExact(_schedule.Time, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var at)) return;
        var due = now.Date.Add(at);
        if (now < due || now >= due.AddHours(2)) return;
        if (_schedule.LastPromptedOccurrence == due && _schedule.SnoozedUntil is null) return;
        if (_schedule.LastPromptedOccurrence == due && _schedule.SnoozedUntil is DateTime later && now < later) return;
        ShowReminder(false, due);
    }
    private void ShowReminder(bool isTest, DateTime occurrence)
    {
        if (!isTest)
        {
            _schedule.LastPromptedOccurrence = occurrence;
            _schedule.SnoozedUntil = null;
            _schedule.Save();
        }
        var dialog = new ReminderDialog(isTest, occurrence, _schedule.Language == "no") { Owner = this };
        dialog.ShowDialog();
        ReminderSchedule.Log(isTest ? "test_reminder" : "reminder", dialog.Decision);
        if (!isTest)
        {
            if (dialog.Decision == "confirmed") _schedule.ConfirmedOccurrence = occurrence;
            if (dialog.Decision == "snoozed") _schedule.SnoozedUntil = DateTime.Now.AddMinutes(30);
            _schedule.Save();
        }
        RefreshScheduleStatus();
    }
    private void ResetInstallButtonLabel()
    {
        const string english = "↓  Install Selected Updates";
        // ApplyLanguage stores the first text it sees in Tag. A "Please wait…" label
        // captured there would be restored on the next language pass.
        if (InstallButton.Tag is not string tag || tag.Contains("Please wait", StringComparison.Ordinal))
            InstallButton.Tag = english;
        InstallButton.Content = _schedule.Language == "no"
            ? "↓  Installer valgte oppdateringer"
            : english;
    }

    private void ResetScanButtonLabel()
    {
        _scanButtonSearching = false;
        const string english = "⌕  Check for Updates";
        // ApplyLanguage stores the first text it sees in Tag. A busy "Searching...." /
        // "Søker...." label captured there would be restored on the next language pass.
        if (ScanButton.Tag is not string tag
            || tag.Contains("Searching", StringComparison.Ordinal)
            || tag.Contains("Søker", StringComparison.Ordinal))
            ScanButton.Tag = english;
        ScanButton.Content = _schedule.Language == "no"
            ? "⌕  Søk etter oppdateringer"
            : english;
    }

    private void SetScanButtonSearching()
    {
        _scanButtonSearching = true;
        ScanButton.Content = _schedule.Language == "no" ? "Søker...." : "Searching....";
    }

    private async Task LoadAsync(Func<List<UpdateEntry>> operation)
    {
        if (_busy) return;
        _busy = true;
        InstallButton.Content = "Please wait…";
        ScanButton.IsEnabled = InstallButton.IsEnabled = false;
        SetHistoryActionsEnabled(false);
        UpdateSelectionControls();
        StatusText.Text = "Loading installation history…";
        try
        {
            var result = await WindowsUpdateReader.RunStaAsync(operation);
            _historySnapshot = result;
            RefreshHealthSummary();
            SetTab("history");
            StatusText.Text = $"History loaded: {result.Count} entries. Pending updates remain separate.";
            HidePendingAlreadyInHistory(result);
        }
        catch (Exception ex)
        {
            StatusText.Text = "History load failed. See error details.";
            MessageBox.Show(this, ex.Message, "Windows Update history error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
            SetHistoryActionsEnabled(true);
            ResetInstallButtonLabel();
            UpdateSelectionControls();
        }
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        ScanButton.IsEnabled = AppsScanButton.IsEnabled = false;
        SetHistoryActionsEnabled(false);
        UpdateSelectionControls(); UpdateAppSelection();
        SetScanButtonSearching();
        try
        {
            // Capture WPF checkbox state on the UI thread BEFORE passing work to the STA worker.
            // Accessing IncludeOptionalUpdates from the worker throws InvalidOperationException.
            bool includeOptional = IncludeOptionalUpdates.IsChecked == true;
            bool allDrivers = ShowAllDrivers.IsChecked == true;
            var windows = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(includeOptional, allDrivers));
            ReplacePendingSnapshot(windows);
            _lastSuccessfulScan = DateTime.Now;
            SetTab("software");
            int driverCount = windows.Count(e => e.Category == "Driver / firmware");
            try
            {
                var apps = await WingetReader.ScanAsync();
                WingetAvailableCache.Write(apps.Updates, apps.Raw);
                ApplyAppScanResult(apps.Updates, apps.Raw);
                ReconcileApplicationsPromptWithScan(apps.Updates);
                StatusText.Text = $"Windows: {windows.Count - driverCount}, drivers: {driverCount}, applications: {apps.Updates.Count}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "WinGet scan failed: " + ex.Message;
                StatusText.Text = $"Windows scan complete; WinGet failed: {ex.Message}";
            }
            RefreshHealthSummary();
        }
        catch (Exception ex)
        {
            ActivityLog.TryWrite("UpdateScan", "Failed", new { Exception = ex.ToString() });
            MessageBox.Show(this, ex.Message, "Update scan error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = AppsScanButton.IsEnabled = true;
            SetHistoryActionsEnabled(true);
            ResetScanButtonLabel();
            UpdateSelectionControls(); UpdateAppSelection();
        }
    }
    private async void WindowsUpdateFix_Click(object sender, RoutedEventArgs e) => await RunWindowsUpdateFixAsync();

    private void StartFixItIconAnimation()
    {
        if (FixItIconImage is null || ClickItIconImage is null) return;
        _fixItIconTimer.Tick -= FixItIconTimer_Tick;
        _fixItIconTimer.Tick += FixItIconTimer_Tick;
        if (!_fixItIconTimer.IsEnabled)
            _fixItIconTimer.Start();
    }

    private void StopFixItIconAnimation()
    {
        _fixItIconTimer.Stop();
        _fixItIconTimer.Tick -= FixItIconTimer_Tick;
        FixItIconImage?.BeginAnimation(UIElement.OpacityProperty, null);
        ClickItIconImage?.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private void FixItIconTimer_Tick(object? sender, EventArgs e)
    {
        if (_fixItIconAnimating || FixItIconImage is null || ClickItIconImage is null) return;
        _showingClickItIcon = !_showingClickItIcon;
        _fixItIconAnimating = true;

        var duration = TimeSpan.FromSeconds(0.4);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var showClick = _showingClickItIcon;
        var clickAnim = new DoubleAnimation(showClick ? 0 : 1, showClick ? 1 : 0, duration) { EasingFunction = ease };
        var fixAnim = new DoubleAnimation(showClick ? 1 : 0, showClick ? 0 : 1, duration) { EasingFunction = ease };
        clickAnim.Completed += (_, _) => _fixItIconAnimating = false;

        ClickItIconImage.BeginAnimation(UIElement.OpacityProperty, clickAnim);
        FixItIconImage.BeginAnimation(UIElement.OpacityProperty, fixAnim);
    }

    private void FixItOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_fixItWindow is { IsLoaded: true })
        {
            if (_fixItWindow.WindowState == WindowState.Minimized)
                _fixItWindow.WindowState = WindowState.Normal;
            _fixItWindow.Activate();
            return;
        }

        _fixItWindow = new FixItWindow(this);
        _fixItWindow.ApplyLanguage(_schedule.Language == "no");
        SetHistoryActionsEnabled(_historyActionsEnabled);
        SetFixItHealthEnabled(!_healthCheckRunning);
        SetFixItDiagnosticsBusy(_diagnosticsRunning);
        _fixItWindow.Show();
    }

    internal void DetachFixItWindow(FixItWindow window)
    {
        if (ReferenceEquals(_fixItWindow, window))
            _fixItWindow = null;
    }

    private void SetHistoryActionsEnabled(bool enabled)
    {
        _historyActionsEnabled = enabled;
        if (_fixItWindow?.HistoryButton is { } history)
            history.IsEnabled = enabled;
    }

    private void SetFixItHealthEnabled(bool enabled)
    {
        if (_fixItWindow?.HealthButton is { } health)
            health.IsEnabled = enabled;
    }

    private void SetFixItDiagnosticsBusy(bool busy)
    {
        if (_fixItWindow?.DiagnosticsButton is not { } diagnostics) return;
        diagnostics.IsEnabled = !busy;
        if (busy)
            diagnostics.Content = "Please wait…";
        else
            diagnostics.Content = _schedule.Language == "no" ? "⚙  Diagnostikk" : "⚙  Diagnostics";
    }

    internal void FixItWindowsUpdateFix() => _ = RunWindowsUpdateFixAsync();
    internal void FixItWuaDeepCheck() => RunWuaDeepCheck();
    internal void FixItHistory() => _ = LoadAsync(WindowsUpdateReader.HistoryForDisplay);
    internal void FixItExportCsv() => Export_Click(_fixItWindow ?? (object)this, new RoutedEventArgs());
    internal void FixItErrorReport() => RunErrorReport();
    internal void FixItWuaStatusReport() => RunWuaStatusReport();
    internal void FixItDiagnostics() => _ = RunDiagnosticsAsync();
    internal void FixItHealth() => _ = RunHealthCheckAsync();

    private async Task RunWindowsUpdateFixAsync()
    {
        if (_busy) return;
        var dlg = new WindowsUpdateFixDialog(null, null, IncludeOptionalUpdates.IsChecked == true, ShowAllDrivers.IsChecked == true) { Owner = _fixItWindow as Window ?? this };
        dlg.ShowDialog();
        if (dlg.FixCompleted)
            await ReloadAfterWindowsUpdateFixAsync(dlg.ReplacedUpdateCache, dlg.ScanCompleted);
    }

    internal async Task ReloadAfterWindowsUpdateFixAsync(bool replacedUpdateCache, bool scanCompleted)
    {
        if (_busy) return;
        _busy = true;
        ScanButton.IsEnabled = false;
        SetHistoryActionsEnabled(false);
        try
        {
            StatusText.Text = "Reloading history after Windows Update fix…";
            if (scanCompleted)
            {
                var pending = HistoryArchive.CopyPendingEntries() ?? new List<UpdateEntry>();
                ReplacePendingSnapshot(pending);
                _lastSuccessfulScan = DateTime.Now;
            }
            else if (replacedUpdateCache)
            {
                _pendingSnapshot.Clear();
                _pendingSnapshotAt = null;
                RefreshFeatureUpdateButton();
            }
            var history = await WindowsUpdateReader.RunStaAsync(WindowsUpdateReader.HistoryForDisplay);
            _historySnapshot = history;
            RefreshHealthSummary();
            SetTab(_activeTab);
            int fixedCount = history.Count(entry => entry.IsHistoryFixed);
            StatusText.Text = scanCompleted
                ? $"History kept: {history.Count} events ({fixedCount} marked Fixed). Pending updates were scanned again."
                : replacedUpdateCache
                    ? $"History kept: {history.Count} events ({fixedCount} marked Fixed). The new scan did not finish. Click Check for Updates."
                    : $"History kept: {history.Count} events ({fixedCount} marked Fixed).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Reload after Windows Update fix failed.";
            MessageBox.Show(this, ex.Message, "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
            SetHistoryActionsEnabled(true);
            UpdateSelectionControls();
        }
    }

    private void WuaDeepCheck_Click(object sender, RoutedEventArgs e) => RunWuaDeepCheck();

    private void RunWuaDeepCheck()
    {
        if (_busy) return;
        var dlg = new WuaDeepCheckDialog { Owner = _fixItWindow as Window ?? this };
        dlg.ShowDialog();
    }

    private void WuaStatusReport_Click(object sender, RoutedEventArgs e) => RunWuaStatusReport();

    private void RunWuaStatusReport()
    {
        var dlg = new WuaStatusReportDialog { Owner = _fixItWindow as Window ?? this };
        dlg.ShowDialog();
    }

    private void ErrorReport_Click(object sender, RoutedEventArgs e) => RunErrorReport();

    private void RunErrorReport()
    {
        var dlg = new ErrorReportDialog { Owner = _fixItWindow as Window ?? this };
        dlg.ShowDialog();
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e) => await RunDiagnosticsAsync();

    private async Task RunDiagnosticsAsync()
    {
        if (_diagnosticsRunning) return;
        _diagnosticsRunning = true;
        SetFixItDiagnosticsBusy(true);
        try
        {
            StatusText.Text = "Collecting diagnostics…";
            var report = await WindowsUpdateReader.RunStaAsync(() => new List<UpdateEntry> { new() { Title = WindowsUpdateReader.Diagnostics() } });
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"UpdateExpress-Diagnostikk-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, report[0].Title, new UTF8Encoding(true));
            StatusText.Text = "Diagnostics saved: " + path;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Diagnostics error", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            _diagnosticsRunning = false;
            SetFixItDiagnosticsBusy(false);
        }
    }
    private async void History_Click(object sender, RoutedEventArgs e) => await LoadAsync(WindowsUpdateReader.HistoryForDisplay);
    private void HistoryPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab != "history") return;
        var visible = Entries.Where(e => VisibleInTab(e)).ToList();
        var selected = visible.Where(x => x.IsSelected).ToList();
        var rows = selected.Count > 0 ? selected : visible;
        if (rows.Count == 0) { MessageBox.Show(this, "No history records available."); return; }
        var dialog = new SaveFileDialog { Filter = "PDF document (*.pdf)|*.pdf", FileName = $"UpdateExpress-History-{DateTime.Now:yyyyMMdd-HHmmss}.pdf" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Services.HistoryPdfReport.Write(dialog.FileName, rows, selected.Count > 0 ? "Selected history entries" : "All displayed history entries");
            StatusText.Text = $"Exported {rows.Count} history entries to PDF.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "PDF export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void ErrorCodeFix_Click(object sender, RoutedEventArgs e)
    {
        UpdateEntry? entry = null;
        if (sender is FrameworkElement fe && fe.DataContext is UpdateEntry fromSender)
            entry = fromSender;
        else if (UpdatesGrid.SelectedItem is UpdateEntry selected)
            entry = selected;
        if (entry is null || !entry.HasFixableErrorCode) return;
        ShowErrorFix(entry);
    }

    private void ShowErrorFix(UpdateEntry entry)
    {
        bool offered = HistoryArchive.IsStillOffered(entry, _pendingSnapshot.ToList());
        var dlg = new ErrorCodeFixDialog(entry, IncludeOptionalUpdates.IsChecked == true, ShowAllDrivers.IsChecked == true, offered) { Owner = this };
        dlg.ShowDialog();
        if (dlg.Hidden)
        {
            string hiddenIdentity = HistoryArchive.DisplayIdentityKey(entry);
            _historySnapshot.RemoveAll(row =>
                string.Equals(HistoryArchive.DisplayIdentityKey(row), hiddenIdentity, StringComparison.OrdinalIgnoreCase));
            if (_showingHistory) SetTab("history");
            RefreshHealthSummary();
        }
        if (dlg.ReinstallRequested)
            ReinstallOffered(entry);
    }

    private void ReinstallOffered(UpdateEntry history)
    {
        var match = HistoryArchive.FindOffered(history, _pendingSnapshot.ToList());
        if (match is null)
        {
            bool norwegian = _schedule.Language == "no";
            MessageBox.Show(this,
                norwegian
                    ? "Windows tilbyr ikke denne oppdateringen nå, så den kan ikke installeres på nytt herfra."
                    : "Windows is not offering this update now, so it cannot be installed again from here.",
                "Update Express", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var item in _pendingSnapshot) item.IsSelected = false;
        foreach (var app in _appEntries) app.IsSelected = false;
        match.IsSelected = true;
        Install_Click(this, new RoutedEventArgs());
    }

    private void UpdateDetails_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (UpdatesGrid.SelectedItem is not UpdateEntry entry) return;
        if (entry.OpensWindowsUpdate)
        {
            OpenWindowsUpdate_Click(sender, e);
            return;
        }
        if (entry.HasFixableErrorCode)
        {
            ShowErrorFix(entry);
            return;
        }
        string assessment = _activeTab == "history"
            ? "Historical event only. Current installed status and removal capability are NOT verified."
            : _activeTab == "drivers"
                ? "Pending driver/firmware update. Removal is not supported in Update Express."
                : "Pending Windows update. Removal is not applicable until installation is verified.";
        if (entry.Category == "Driver / firmware")
        {
            MessageBox.Show(this,
                $"Driver: {entry.DisplayTitle}\nOffered version: {entry.DriverVersion}\nClassification: {entry.DriverStatus}\n" +
                $"Hardware ID: {entry.DriverHardwareId}\nModel: {entry.DriverModel}\n" +
                $"Manufacturer: {entry.DriverManufacturer}\nClass: {entry.DriverClass}\n" +
                $"Installed version: {(string.IsNullOrWhiteSpace(entry.InstalledDriverVersion) ? "Not verified" : entry.InstalledDriverVersion)}\nReason: {entry.DriverReason}",
                "Driver details (read-only)", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string note = string.IsNullOrWhiteSpace(entry.StatusNote) ? "" : entry.StatusNote + "\n\n";
        MessageBox.Show(this, $"Update: {entry.Title}\nKB: {entry.KB}\nCategory: {entry.Category}\nPriority: {entry.PriorityDisplay}\nStatus: {entry.Status}\nDate: {entry.DateShown}\nError: {entry.ErrorCode}\n\n{note}{assessment}\n\nUninstall is not implemented.", "Update Details (read-only)", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Entries.Count == 0) { MessageBox.Show(this, "No entries to export."); return; }
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"UpdateExpress-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("KB;Update;Status;DateTime;Source;ErrorCode\r\n");
        foreach (var x in Entries) sb.AppendLine(string.Join(';', new[] { x.KB, x.Title, x.Status, x.DateShown, x.Source, x.ErrorCode }.Select(Q)));
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        StatusText.Text = $"Exported {Entries.Count} entries to {dialog.FileName}";
    }
    private void ActivityLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(ActivityLog.FilePath))
            {
                MessageBox.Show(this, "No Update Express activity has been logged yet.\n\n" + ActivityLog.FilePath, "Activity Log");
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe")
            {
                ArgumentList = { ActivityLog.FilePath }, UseShellExecute = true
            });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Activity Log", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ActivityExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(ActivityLog.FilePath)) { MessageBox.Show(this, "No activity log exists yet."); return; }
            var dialog = new SaveFileDialog { Filter = "Text log (*.txt)|*.txt|JSON Lines (*.jsonl)|*.jsonl", FileName = $"UpdateExpress-Activity-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
            if (dialog.ShowDialog(this) != true) return;
            // Export unchanged JSONL records: avoids losing identity, timestamps or event details.
            File.Copy(ActivityLog.FilePath, dialog.FileName, true);
            MessageBox.Show(this, "Activity log exported to:\n" + dialog.FileName, "Activity Log");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Activity Log", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // Pending snapshots persist when switching tabs. History is never installable.
        var software = _pendingSnapshot.Where(x => x.IsSelected && x.Category != "Driver / firmware" && x.SafeDefault && !string.IsNullOrWhiteSpace(x.UpdateId)).ToList();
        var drivers = _pendingSnapshot.Where(x => x.IsSelected && x.Category == "Driver / firmware" && !string.IsNullOrWhiteSpace(x.UpdateId)).ToList();
        var apps = _appEntries.Where(x => x.IsSelected).ToList();
        if (software.Count + drivers.Count + apps.Count == 0) return;
        // Only installation requires elevation; scanning and diagnostics remain available.
        // WUA may return 0x80240044 when a non-elevated process attempts a download.
        if (!UpdateExpress.Services.ElevationHelper.IsElevated)
        {
            var choice = AppDialog.Show(this,
                "Installation requires administrator privileges.\n\n" +
                "Restart Update Express as administrator?\n\n" +
                "No updates will be installed automatically. Scan again and select updates after restart.",
                "Administrator privileges required", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes) return;
            try
            {
                UpdateExpress.Services.ElevationHelper.RestartElevated();
                System.Windows.Application.Current.Shutdown();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                AppDialog.Show(this, "Administrator approval cancelled. No installation was started.",
                    "Update Express", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppDialog.Show(this, "Unable to restart as administrator: " + ex.Message,
                    "Update Express", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }
        if (drivers.Any(x => !IsManuallyInstallableDriver(x)))
        {
            AppDialog.Show(this, "A selected driver or firmware update has no verified hardware ID, so it was not installed.", "Driver safety", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (drivers.Count > 0)
        {
            var summary = string.Join("\n", drivers.Select(d => $"• {d.Title} [{d.DriverHardwareId}]"));
            if (AppDialog.Show(this,
                $"Install {drivers.Count} manually selected driver or firmware update(s), sequentially?\n\n{summary}\n\nEach one is rechecked against WUA and its hardware ID. A failure will not stop the next one. Nothing is installed automatically, and there is no automatic restart.",
                "Confirm driver installation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        var queue = new List<InstallationQueueItem>();
        queue.AddRange(software.Select(x => new InstallationQueueItem("Windows Update", x.Title)));
        queue.AddRange(drivers.Select(x => new InstallationQueueItem("Driver", x.Title)));
        queue.AddRange(apps.Select(x => new InstallationQueueItem("WinGet", x.DisplayName)));
        var manager = new InstallationManagerWindow(queue) { Owner = this };
        if (AppDialog.Show(this, $"Install {queue.Count} selected updates from all tabs?\n\nWindows Updates: {software.Count}\nDrivers: {drivers.Count}\nWinGet: {apps.Count}\n\nNo automatic restart will be performed.", "Confirm combined installation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true;
        InstallButtonActive = true;
        InstallButton.Content = "Installing updates…";
        ScanButton.IsEnabled = AppsScanButton.IsEnabled = false;
        SetHistoryActionsEnabled(false);
        UpdateSelectionControls();
        manager.Show();
        manager.PrepareRunning();
        var runId = Guid.NewGuid().ToString("N");
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateExpress");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "installation-manager.jsonl");
        void Log(string source, string status, object detail)
        {
            try { File.AppendAllText(logPath, JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.Now, RunId = runId, Source = source, Status = status, Detail = detail }) + Environment.NewLine); } catch { }
            ActivityLog.TryWrite("InstallationManager", status, detail, source: source, runId: runId);
        }
        int cursor = 0;
        bool norwegian = _schedule.Language == "no";
        string completedLabel = InstallationManagerWindow.StatusCompleted(norwegian);
        string verifyingLabel = InstallationManagerWindow.StatusVerifying(norwegian);
        string notVerifiedLabel = InstallationManagerWindow.StatusNotVerified(norwegian);
        string timedOutLabel = InstallationManagerWindow.StatusVerifyTimedOut(norwegian);
        string retryGuidance = norwegian
            ? "Lukk, skann på nytt, og prøv installasjon igjen om oppdateringen fortsatt tilbys."
            : "Close, rescan, and retry install if the update is still offered.";
        // Soft-timeout UI must not start overlapping WUA work: keep references and await
        // them before clearing _busy / enabling another install.
        var outstandingWork = new List<Task>();
        try
        {
            Log("InstallationManager", "Started", queue.Select(q => new { q.Source, q.Name }).ToArray());
            if (software.Count > 0)
            {
                int start = cursor;
                manager.SetNotice(WuaProgressText.PleaseWait(norwegian));
                manager.SetStage(norwegian ? "Forbereder nedlasting…" : "Preparing download…");
                try
                {
                    bool acceptUiProgress = true;
                    IProgress<WuaActivity> activity = new Progress<WuaActivity>(update =>
                    {
                        if (!acceptUiProgress) return;
                        manager.ApplyActivity(update, norwegian);
                        Log("WUA", "Progress", WuaProgressText.Stage(norwegian, update.Phase, update.Kb, update.Number, update.Total, update.Percent));
                    });
                    IProgress<string> progress = new Progress<string>(stage => Log("WUA", "Progress", stage));
                    // Always await WUA install completion — never abandon the STA thread mid-install.
                    var results = await AwaitInstallationTaskAsync(
                        WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.InstallSelected(software, progress.Report, activity.Report)),
                        manager,
                        softTimeout: TimeSpan.FromMinutes(90),
                        timeoutMessage: norwegian
                            ? "WUA-installasjon tar lengre tid enn forventet — venter fortsatt på Windows Update Agent…"
                            : "WUA installation is taking longer than expected — still waiting for Windows Update Agent…",
                        abandonOnTimeout: false);
                    acceptUiProgress = false;
                    foreach (var entry in results) Log("WUA", entry.Status, new { entry.Title, entry.KB, entry.UpdateId, entry.ErrorCode });
                    for (int i = 0; i < software.Count; i++)
                    {
                        var requested = software[i];
                        var result = WindowsUpdateReader.MatchInstallResult(results, requested, i, software.Count);
                        var state = result == null ? notVerifiedLabel :
                            result.Status is "Installert" or "Already up to date" ? verifyingLabel :
                            result.Status is "Delvis fullført" ? notVerifiedLabel :
                            "Failed" + (string.IsNullOrWhiteSpace(result.ErrorCode) ? "" : " " + result.ErrorCode);
                        manager.CompleteItem(start + i, state, state.StartsWith("Failed", StringComparison.Ordinal));
                    }
                }
                catch (Exception ex) { for (int i = 0; i < software.Count; i++) manager.CompleteItem(start + i, "Failed", true); Log("WUA", "Failed", ex.ToString()); }
                cursor += software.Count;
            }
            foreach (var d in drivers)
            {
                int idx = cursor++;
                manager.SetStage("Rechecking driver identity: " + d.Title + "…");
                try
                {
                    bool acceptUiProgress = true;
                    IProgress<WuaActivity> activity = new Progress<WuaActivity>(update =>
                    {
                        if (!acceptUiProgress) return;
                        manager.ApplyActivity(update, norwegian);
                        Log("Driver", "Progress", new { d.Title, update.Phase, update.Kb, update.Percent });
                    });
                    IProgress<string> progress = new Progress<string>(stage => Log("Driver", "Progress", new { d.Title, Stage = stage }));
                    var results = await AwaitInstallationTaskAsync(
                        WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.InstallSelectedDriver(d, progress.Report, true, activity.Report)),
                        manager,
                        softTimeout: TimeSpan.FromMinutes(45),
                        timeoutMessage: norwegian
                            ? "Driverinstallasjon tar lengre tid enn forventet — venter fortsatt…"
                            : "Driver installation is taking longer than expected — still waiting…",
                        abandonOnTimeout: false);
                    acceptUiProgress = false;
                    foreach (var entry in results) Log("Driver", entry.Status, new { entry.Title, entry.UpdateId, entry.ErrorCode });
                    var driverResult = results.FirstOrDefault();
                    string driverState = driverResult == null ? notVerifiedLabel :
                        driverResult.Status == "Installert" ? verifyingLabel :
                        driverResult.Status == "Delvis fullført" ? notVerifiedLabel :
                        "Failed" + (string.IsNullOrWhiteSpace(driverResult.ErrorCode) ? "" : " " + driverResult.ErrorCode);
                    manager.CompleteItem(idx, driverState, driverState.StartsWith("Failed", StringComparison.Ordinal));
                }
                catch (Exception ex)
                {
                    string hresult = ex.HResult < 0 ? $" 0x{unchecked((uint)ex.HResult):X8}" : "";
                    manager.CompleteItem(idx, "Failed" + hresult, true);
                    Log("Driver", "Failed", new { d.Title, d.UpdateId, d.DriverHardwareId, Exception = ex.ToString(), InnerException = ex.InnerException?.ToString() });
                    manager.SetStage("Driver failed: " + ex.Message);
                }
            }
            foreach (var app in apps)
            {
                int idx = cursor++;
                manager.BeginItem(idx, "Installing " + app.DisplayName + "…");
                int code; string output;
                try { using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(20)); (code, output) = await WingetReader.UpgradeAsync(app.Id, timeout.Token); }
                catch (Exception ex) { code = -1; output = ex.ToString(); }
                Log("WinGet", code == 0 ? "Verification pending" : "Failed", new { app.Name, app.Id, app.Installed, app.Available, ExitCode = code, Output = output });
                manager.CompleteItem(idx, code == 0 ? verifyingLabel : $"Failed (exit {code})", code != 0);
            }
            // Terminal install outcomes are on screen — unlock Close before the long rescan.
            manager.AllowClose(norwegian);
            manager.SetStage(norwegian ? "Oppdaterer Windows Update- og WinGet-resultater…" : "Refreshing Windows Update and WinGet results…");
            bool verifyTimedOut = false;
            bool stillOfferedAfterSuccess = false;
            string catalogLagGuidance = norwegian
                ? "Installasjonen lyktes, men Windows kan fortsatt vise oppdateringen til katalogen er oppdatert eller PC-en er startet på nytt."
                : "Install succeeded, but Windows may still offer the update until its catalog refreshes or the PC is restarted.";
            try
            {
                var pendingTask = WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(true, true));
                outstandingWork.Add(pendingTask);
                var pending = await AwaitInstallationTaskAsync(
                    pendingTask,
                    manager,
                    softTimeout: TimeSpan.FromMinutes(5),
                    timeoutMessage: norwegian ? "Verifisering (WUA-rescan) tidsavbrutt." : "Verification (WUA rescan) timed out.",
                    abandonOnTimeout: true);
                outstandingWork.Remove(pendingTask);
                ReplacePendingSnapshot(pending);
                var historyTask = WindowsUpdateReader.RunStaAsync(WindowsUpdateReader.HistoryForDisplay);
                outstandingWork.Add(historyTask);
                _historySnapshot = await AwaitInstallationTaskAsync(
                    historyTask,
                    manager,
                    softTimeout: TimeSpan.FromMinutes(3),
                    timeoutMessage: norwegian ? "Verifisering (historikk) tidsavbrutt." : "Verification (history) timed out.",
                    abandonOnTimeout: true);
                outstandingWork.Remove(historyTask);
                HidePendingAlreadyInHistory(_historySnapshot);
                foreach (var item in software.Select((update, i) => new { update, i }))
                {
                    if (queue[item.i].State.StartsWith("Failed", StringComparison.Ordinal)) continue;
                    bool remains = pending.Any(x => x.UpdateId.Equals(item.update.UpdateId, StringComparison.OrdinalIgnoreCase));
                    bool historyOk = HistoryConfirmsInstall(_historySnapshot, item.update);
                    string next = ResolveVerifiedState(queue[item.i].State, remains, historyOk, completedLabel, notVerifiedLabel);
                    if (InstallationManagerWindow.IsCompletedState(next) && remains)
                        stillOfferedAfterSuccess = true;
                    manager.CompleteItem(item.i, next, InstallationManagerWindow.IsUnverifiedState(next));
                    Log("WUA", next, new { item.update.UpdateId, item.update.Title, remains, historyOk });
                }
                for (int i = 0; i < drivers.Count; i++)
                {
                    int driverIndex = software.Count + i;
                    if (queue[driverIndex].State.StartsWith("Failed", StringComparison.Ordinal)) continue;
                    bool remains = pending.Any(x => x.UpdateId.Equals(drivers[i].UpdateId, StringComparison.OrdinalIgnoreCase));
                    bool historyOk = HistoryConfirmsInstall(_historySnapshot, drivers[i]);
                    string next = ResolveVerifiedState(queue[driverIndex].State, remains, historyOk, completedLabel, notVerifiedLabel);
                    if (InstallationManagerWindow.IsCompletedState(next) && remains)
                        stillOfferedAfterSuccess = true;
                    manager.CompleteItem(driverIndex, next, InstallationManagerWindow.IsUnverifiedState(next));
                    Log("Driver", next, new { drivers[i].UpdateId, drivers[i].Title, remains, historyOk });
                }
                Log("WUA", "Rescan completed", new { Count = pending.Count });
            }
            catch (TimeoutException tex)
            {
                verifyTimedOut = true;
                Log("WUA", "Rescan timed out", tex.Message);
                manager.SetStage(tex.Message + " " + retryGuidance);
                // Installer already reported success (Verifying) — do not demote to timed out.
                for (int i = 0; i < software.Count + drivers.Count; i++)
                    if (InstallationManagerWindow.IsVerifyingState(queue[i].State))
                        manager.CompleteItem(i, completedLabel, false);
            }
            catch (Exception ex) { Log("WUA", "Rescan failed", ex.Message); manager.SetStage("Windows Update rescan failed: " + ex.Message); }
            try
            {
                var scanTask = WingetReader.ScanAsync();
                outstandingWork.Add(scanTask);
                var scan = await AwaitInstallationTaskAsync(
                    scanTask,
                    manager,
                    softTimeout: TimeSpan.FromMinutes(5),
                    timeoutMessage: norwegian ? "Verifisering (WinGet-rescan) tidsavbrutt." : "Verification (WinGet rescan) timed out.",
                    abandonOnTimeout: true);
                outstandingWork.Remove(scanTask);
                WingetAvailableCache.Write(scan.Updates, scan.Raw);
                ApplyAppScanResult(scan.Updates, scan.Raw);
                ReconcileApplicationsPromptWithScan(scan.Updates);
                foreach (var item in apps.Select((app, i) => new { app, i }))
                {
                    int qi = software.Count + drivers.Count + item.i;
                    if (queue[qi].State.StartsWith("Failed", StringComparison.Ordinal)) continue;
                    bool remains = scan.Updates.Any(x => x.Id.Equals(item.app.Id, StringComparison.OrdinalIgnoreCase));
                    string next = remains ? notVerifiedLabel :
                        InstallationManagerWindow.IsVerifyingState(queue[qi].State) ? completedLabel : notVerifiedLabel;
                    manager.CompleteItem(qi, next, InstallationManagerWindow.IsUnverifiedState(next));
                    Log("WinGet", remains ? "Not verified" : "Verified absent from upgrade list", new { item.app.Id, item.app.Available });
                }
            }
            catch (TimeoutException tex)
            {
                verifyTimedOut = true;
                Log("WinGet", "Rescan timed out", tex.Message);
                manager.SetStage(tex.Message + " " + retryGuidance);
                for (int i = 0; i < apps.Count; i++)
                {
                    int qi = software.Count + drivers.Count + i;
                    if (InstallationManagerWindow.IsVerifyingState(queue[qi].State))
                        manager.CompleteItem(qi, timedOutLabel, true);
                }
            }
            catch (Exception ex) { Log("WinGet", "Rescan failed", ex.Message); manager.SetStage("WinGet rescan failed: " + ex.Message); }
            SetTab(_activeTab);
            RefreshHealthSummary();
            Log("InstallationManager", "Finished", new { Total = queue.Count, verifyTimedOut, stillOfferedAfterSuccess });
            string? guidance = queue.Any(q => InstallationManagerWindow.IsUnverifiedState(q.State))
                ? retryGuidance
                : stillOfferedAfterSuccess || verifyTimedOut
                    ? catalogLagGuidance
                    : null;
            manager.Finish(norwegian, guidance, verifyTimedOut ? timedOutLabel : notVerifiedLabel);
            StatusText.Text = norwegian
                ? "Installasjon ferdig. Se resultatene i Installation Manager."
                : "Installation finished. Review Installation Manager results.";
        }
        catch (Exception ex)
        {
            Log("InstallationManager", "Failed", ex.ToString());
            manager.SetStage("Installation interrupted: " + ex.Message);
            manager.Finish(norwegian, retryGuidance, notVerifiedLabel);
            MessageBox.Show(this, ex.Message, "Installation Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (outstandingWork.Count > 0)
            {
                manager.SetStage(norwegian
                    ? "Venter på at bakgrunns-WUA/WinGet skal bli ferdig før ny installasjon tillates…"
                    : "Waiting for background WUA/WinGet work to finish before allowing another install…");
                try { await Task.WhenAll(outstandingWork.Select(DrainTaskAsync)); }
                catch { /* logged inside DrainTaskAsync */ }
            }
            _busy = false;
            InstallButtonActive = false;
            ResetInstallButtonLabel();
            ScanButton.IsEnabled = AppsScanButton.IsEnabled = true;
            SetHistoryActionsEnabled(true);
            UpdateSelectionControls();
            RefreshRebootNeededBanner();
        }
    }

    private void ResultsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (ResultsRow == null) return;
        ResultsRow.Height = new GridLength(1, GridUnitType.Star);
    }

    private void ResultsExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (ResultsRow == null) return;
        ResultsRow.Height = GridLength.Auto;
    }

    private void ResultsExpander_SizeChanged(object sender, SizeChangedEventArgs e) => StretchResultsHeader();

    private void RefreshUpdateUntilCurrentUi()
    {
        if (UpdateUntilCurrentButton is null || UpdateUntilCurrentStatus is null) return;
        bool norwegian = _schedule.Language == "no";
        var state = UpdateUntilCurrent.Load();
        UpdateUntilCurrentButton.Content = state.Active
            ? (norwegian ? "Stopp oppdateringsrunden" : "Stop update cycle")
            : (norwegian ? "Start oppdateringsrunden" : "Start update cycle");
        UpdateUntilCurrentStatus.Text = string.IsNullOrWhiteSpace(state.Message)
            ? (norwegian ? "Kjører ikke" : "Not running")
            : state.Message;
    }

    private async void UpdateUntilCurrent_Click(object sender, RoutedEventArgs e)
    {
        bool norwegian = _schedule.Language == "no";
        var state = UpdateUntilCurrent.Load();
        if (state.Active || UpdateUntilCurrent.IsRunning)
        {
            UpdateUntilCurrent.Stop(norwegian
                ? "Stoppet. Den pågående runden fullføres, uten ny omstart."
                : "Stopped. The current round finishes, without another restart.");
            RebootCoordinator.CancelCountdownReboot("Update until current stopped by user.");
            RefreshUpdateUntilCurrentUi();
            return;
        }

        if (_busy)
        {
            MessageBox.Show(this,
                norwegian
                    ? "En annen operasjon kjører allerede. Vent til den er ferdig."
                    : "Another operation is already running. Wait until it finishes.",
                norwegian ? "Oppdater til ferdig" : "Update until current",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = AppDialog.Show(this,
            norwegian
                ? "Oppdater til ferdig søker, laster ned og installerer Windows-oppdateringer.\n\nNår Windows krever omstart, starter PC-en på nytt etter 90 sekunder. Etter innlogging må administrator-spørsmålet godkjennes, så fortsetter runden til det ikke er flere slike oppdateringer.\n\nDrivere, fastvare og funksjonsoppdateringer tas ikke med. Høyeste antall runder er 8."
                : "Update until current searches, downloads, and installs Windows software updates.\n\nWhen Windows requires a restart, the PC restarts after 90 seconds. After you sign in, approve the administrator prompt and the cycle continues until none of those updates remain.\n\nDrivers, firmware, and feature updates are not included. The cycle stops after 8 rounds.",
            norwegian ? "Oppdater til ferdig" : "Update until current",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;
        if (!ElevationHelper.IsElevated)
        {
            MessageBox.Show(this,
                norwegian
                    ? "Administratorrettigheter kreves. Start Update Express på nytt som administrator."
                    : "Administrator privileges are required. Restart Update Express as administrator.",
                norwegian ? "Oppdater til ferdig" : "Update until current",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MaintenanceExpander.IsExpanded = true;
        if (!UpdateUntilCurrent.TryEnter())
        {
            MessageBox.Show(this,
                norwegian
                    ? "Oppdateringsrunden kjører allerede."
                    : "The update cycle is already running.",
                norwegian ? "Oppdater til ferdig" : "Update until current",
                MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshUpdateUntilCurrentUi();
            return;
        }

        UpdateUntilCurrent.Start();
        RefreshUpdateUntilCurrentUi();
        await RunUpdateUntilCurrentAsync(alreadyEntered: true);
    }

    private async Task RunUpdateUntilCurrentAsync(bool alreadyEntered = false)
    {
        if (!alreadyEntered && !UpdateUntilCurrent.TryEnter())
        {
            RefreshUpdateUntilCurrentUi();
            return;
        }

        bool norwegian = _schedule.Language == "no";
        string[] previousIds = Array.Empty<string>();
        _busy = true;
        ScanButton.IsEnabled = InstallButton.IsEnabled = AppsScanButton.IsEnabled = false;
        SetHistoryActionsEnabled(false);
        UpdateSelectionControls();
        UpdateUntilCurrent.ClearResumeLaunchMarker();
        try
        {
            while (true)
            {
                var state = UpdateUntilCurrent.Load();
                if (!state.Active)
                {
                    RefreshUpdateUntilCurrentUi();
                    return;
                }

                state.ResumeAfterReboot = false;
                if (state.Round >= state.MaxRounds)
                {
                    UpdateUntilCurrent.Stop(norwegian
                        ? $"Stoppet etter {state.MaxRounds} runder. Kjør runden på nytt hvis Windows fortsatt tilbyr oppdateringer."
                        : $"Stopped after {state.MaxRounds} rounds. Start it again if Windows still offers updates.");
                    RefreshUpdateUntilCurrentUi();
                    return;
                }

                if (RebootPendingReader.IsRebootPending())
                {
                    BeginUpdateUntilCurrentReboot(state, norwegian);
                    return;
                }

                state.Round++;
                state.Phase = "scanning";
                state.Message = norwegian
                    ? $"Runde {state.Round} av {state.MaxRounds}: søker…"
                    : $"Round {state.Round} of {state.MaxRounds}: searching…";
                UpdateUntilCurrent.Save(state);
                RefreshUpdateUntilCurrentUi();

                List<UpdateEntry> pending;
                try
                {
                    pending = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(false, false));
                }
                catch (Exception ex)
                {
                    UpdateUntilCurrent.Stop(norwegian ? "Søket feilet: " + ex.Message : "Search failed: " + ex.Message);
                    RefreshUpdateUntilCurrentUi();
                    return;
                }

                var matches = pending.Where(UpdateUntilCurrent.IsInCycle).ToList();
                if (matches.Count == 0)
                {
                    var done = UpdateUntilCurrent.Load();
                    done.Active = false;
                    done.Phase = "complete";
                    done.ResumeAfterReboot = false;
                    done.Message = norwegian
                        ? "Ferdig. Ingen flere Windows-programvareoppdateringer å installere her."
                        : "Finished. No further Windows software updates to install here.";
                    UpdateUntilCurrent.Save(done);
                    ActivityLog.TryWrite("UpdateUntilCurrent", "Complete", new { done.Round }, mode: "Manual", source: "MainWindow");
                    RefreshUpdateUntilCurrentUi();
                    MessageBox.Show(this, done.Message, norwegian ? "Oppdater til ferdig" : "Update until current",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var ids = matches.Select(entry => entry.UpdateId).Where(id => !string.IsNullOrWhiteSpace(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
                if (previousIds.Length > 0 && ids.SequenceEqual(previousIds, StringComparer.OrdinalIgnoreCase))
                {
                    UpdateUntilCurrent.Stop(norwegian
                        ? "De samme oppdateringene ligger fortsatt igjen etter installasjon, så runden er stoppet."
                        : "The same updates are still offered after installation, so the cycle stopped.");
                    RefreshUpdateUntilCurrentUi();
                    return;
                }
                previousIds = ids;

                state = UpdateUntilCurrent.Load();
                if (!state.Active) { RefreshUpdateUntilCurrentUi(); return; }
                state.Phase = "installing";
                state.Message = norwegian
                    ? $"Runde {state.Round} av {state.MaxRounds}: installerer {matches.Count} oppdatering(er)…"
                    : $"Round {state.Round} of {state.MaxRounds}: installing {matches.Count} update(s)…";
                UpdateUntilCurrent.Save(state);
                RefreshUpdateUntilCurrentUi();

                var queue = matches.Select(entry => new InstallationQueueItem("Windows Update", entry.Title)).ToList();
                string subtitle = norwegian
                    ? "Oppdater til ferdig. Windows starter på nytt når en runde krever det, og søket kjøres på nytt etterpå."
                    : "Update until current. Windows restarts when a round requires it, then the search runs again.";
                var manager = new InstallationManagerWindow(queue, subtitle) { Owner = this };
                manager.Show();
                manager.PrepareRunning();
                manager.SetNotice(WuaProgressText.PleaseWait(norwegian));
                manager.SetStage(norwegian ? "Forbereder nedlasting…" : "Preparing download…");

                List<UpdateEntry> results;
                try
                {
                    IProgress<WuaActivity> activity = new Progress<WuaActivity>(update =>
                    {
                        try { manager.ApplyActivity(update, norwegian); } catch { }
                    });
                    IProgress<string> progress = new Progress<string>(stage => AgentLog.Write("Update until current: " + stage));
                    results = await WindowsUpdateReader.RunStaAsync(() =>
                        WindowsUpdateReader.InstallSelected(matches, progress.Report, activity.Report));
                    for (int i = 0; i < matches.Count; i++)
                    {
                        var result = WindowsUpdateReader.MatchInstallResult(results, matches[i], i, matches.Count);
                        string itemState = result == null ? InstallationManagerWindow.StatusNotVerified(norwegian) :
                            result.Status is "Installert" or "Already up to date" ? InstallationManagerWindow.StatusCompleted(norwegian) :
                            result.Status == "Delvis fullført" ? InstallationManagerWindow.StatusNotVerified(norwegian) :
                            "Failed" + (string.IsNullOrWhiteSpace(result.ErrorCode) ? "" : " " + result.ErrorCode);
                        manager.CompleteItem(i, itemState, itemState.StartsWith("Failed", StringComparison.Ordinal));
                    }
                    manager.Finish(norwegian);
                }
                catch (Exception ex)
                {
                    try { manager.Finish(); manager.SetStage(ex.Message); } catch { }
                    UpdateUntilCurrent.Stop(norwegian ? "Installasjonen feilet: " + ex.Message : "Installation failed: " + ex.Message);
                    RefreshUpdateUntilCurrentUi();
                    return;
                }

                bool anyOk = results.Any(result => result.Status is "Installert" or "Already up to date" or "Delvis fullført");
                bool reboot = results.Any(result => (result.Source ?? "").Contains("Restart required", StringComparison.OrdinalIgnoreCase))
                    || RebootPendingReader.IsRebootPending();
                if (!anyOk)
                {
                    UpdateUntilCurrent.Stop(norwegian
                        ? "Ingen oppdateringer ble installert, så runden er stoppet."
                        : "No updates were installed, so the cycle stopped.");
                    RefreshUpdateUntilCurrentUi();
                    return;
                }

                state = UpdateUntilCurrent.Load();
                if (!state.Active)
                {
                    RefreshUpdateUntilCurrentUi();
                    return;
                }
                if (reboot)
                {
                    BeginUpdateUntilCurrentReboot(state, norwegian);
                    return;
                }
            }
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = AppsScanButton.IsEnabled = true;
            SetHistoryActionsEnabled(true);
            ResetInstallButtonLabel();
            UpdateSelectionControls();
            UpdateUntilCurrent.Exit();
            RefreshUpdateUntilCurrentUi();
        }
    }

    private void BeginUpdateUntilCurrentReboot(UpdateUntilCurrentState state, bool norwegian)
    {
        state.Active = true;
        state.ResumeAfterReboot = true;
        state.Phase = "reboot";
        state.Message = norwegian
            ? "Omstart om 90 sekunder. Logg inn igjen og godkjenn administrator-spørsmålet."
            : "Restart in 90 seconds. Sign in again and approve the administrator prompt.";
        UpdateUntilCurrent.Save(state);
        RefreshUpdateUntilCurrentUi();
        ActivityLog.TryWrite("UpdateUntilCurrent", "Reboot", new { state.Round }, mode: "Manual", source: "MainWindow");
        RebootCoordinator.StartCountdownReboot(
            "Update until current",
            UpdateUntilCurrent.RebootCountdownSeconds,
            norwegian
                ? "Update Express fortsetter oppdateringsrunden etter omstart."
                : "Update Express continues the update cycle after restart.");
        MessageBox.Show(this, state.Message, norwegian ? "Oppdater til ferdig" : "Update until current",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void StretchResultsHeader()
    {
        if (ResultsHeaderBar == null || ResultsExpander == null) return;
        // The expander arrow sits in its own column, so the title row is a little narrower than the panel.
        double width = ResultsExpander.ActualWidth - 26;
        if (width < 240) return;
        if (double.IsNaN(ResultsHeaderBar.Width) || Math.Abs(ResultsHeaderBar.Width - width) > 0.5)
            ResultsHeaderBar.Width = width;
    }

    /// <summary>
    /// Await install/verify work while pulsing the Installation Manager elapsed clock.
    /// When <paramref name="abandonOnTimeout"/> is false (installs), soft timeout only updates
    /// the stage text and keeps waiting so overlapping WUA work cannot start.
    /// When true (verify rescans), throws TimeoutException for a terminal UI state; callers
    /// must keep the Task in outstandingWork and await it before clearing _busy.
    /// </summary>
    private static async Task<T> AwaitInstallationTaskAsync<T>(
        Task<T> task,
        InstallationManagerWindow manager,
        TimeSpan? softTimeout,
        string? timeoutMessage,
        bool abandonOnTimeout)
    {
        var started = DateTime.UtcNow;
        bool softNotified = false;
        while (!task.IsCompleted)
        {
            if (softTimeout is { } limit && DateTime.UtcNow - started > limit)
            {
                if (abandonOnTimeout)
                    throw new TimeoutException(timeoutMessage ?? "Timed out.");
                if (!softNotified && !string.IsNullOrWhiteSpace(timeoutMessage))
                {
                    softNotified = true;
                    manager.SetStage(timeoutMessage);
                }
            }
            manager.PulseElapsed();
            await Task.WhenAny(task, Task.Delay(500)).ConfigureAwait(true);
        }
        return await task.ConfigureAwait(true);
    }

    private static async Task DrainTaskAsync(Task task)
    {
        try { await task.ConfigureAwait(true); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Outstanding install/verify drain: " + ex.Message); }
    }

    private static string ResolveVerifiedState(
        string prior,
        bool remainsInPending,
        bool historyOk,
        string completedLabel,
        string notVerifiedLabel)
    {
        if (prior.StartsWith("Failed", StringComparison.Ordinal) ||
            prior.StartsWith("Feilet", StringComparison.Ordinal))
            return prior;
        // WUA/installer ResultCode success was mapped to Verifying. Catalog lag or reboot-needed
        // can leave KB-less packages (e.g. PowerShell LTS) in pending briefly — trust the
        // installer result instead of demoting to Not verified.
        if (InstallationManagerWindow.IsVerifyingState(prior))
            return completedLabel;
        // Unmatched installer row: only accept history success when the update is also gone.
        if (historyOk && !remainsInPending)
            return completedLabel;
        return notVerifiedLabel;
    }

    private static bool HistoryConfirmsInstall(IEnumerable<UpdateEntry>? history, UpdateEntry requested)
    {
        if (history == null || requested == null) return false;
        foreach (var entry in history)
        {
            if (!entry.IsHistorySuccessful) continue;
            if (!string.IsNullOrWhiteSpace(requested.UpdateId) &&
                !string.IsNullOrWhiteSpace(entry.UpdateId) &&
                entry.UpdateId.Equals(requested.UpdateId, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(requested.Title) &&
                !string.IsNullOrWhiteSpace(entry.Title) &&
                entry.Title.Equals(requested.Title, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(requested.KB) &&
                !string.IsNullOrWhiteSpace(entry.KB) &&
                entry.KB.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(kb => requested.KB.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Any(r => r.Equals(kb, StringComparison.OrdinalIgnoreCase))))
                return true;
        }
        return false;
    }

}
