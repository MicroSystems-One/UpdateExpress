using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Windows;
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
    private UpdatePolicy _policy = UpdatePolicy.Load();
    private DateTime? _lastSuccessfulScan;
    private readonly ObservableCollection<UpdateEntry> _pendingSnapshot = new();
    private List<UpdateEntry> _historySnapshot = new();
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
    private static bool IsManuallyInstallableDriver(UpdateEntry e) =>
        e.Category == "Driver / firmware" &&
        !e.DriverStatus.Equals("Firmware", StringComparison.OrdinalIgnoreCase) &&
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
        IEnumerable<UpdateEntry> source = _showingHistory ? _historySnapshot : _pendingSnapshot;
        Entries.Clear();
        foreach (var entry in source) Entries.Add(entry);
        _entriesView.Refresh();
        // All tabs share the same visible column order. Empty values remain blank.
        SoftwareTab.FontWeight = tab == "software" ? FontWeights.Bold : FontWeights.Normal;
        DriversTab.FontWeight = tab == "drivers" ? FontWeights.Bold : FontWeights.Normal;
        HistoryTab.FontWeight = tab == "history" ? FontWeights.Bold : FontWeights.Normal;
        AppsTab.FontWeight = tab == "apps" ? FontWeights.Bold : FontWeights.Normal;
        WindowsUpdatesTable.Visibility = tab == "apps" ? Visibility.Collapsed : Visibility.Visible;
        AppsActions.Visibility = tab == "apps" ? Visibility.Visible : Visibility.Collapsed;
        AppsToolbar.Visibility = tab == "apps" ? Visibility.Visible : Visibility.Collapsed;
        SelectAllUpdates.Visibility = Visibility.Visible;
        SelectedCountText.Visibility = Visibility.Visible;
        HistoryPdfButton.Visibility = tab == "history" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesGrid.Items.Refresh();
        UpdateSelectionControls();
        if (_languageReady) ApplyLanguage();
    }
    private void SoftwareTab_Click(object sender, RoutedEventArgs e) { SetTab("software"); }
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
    private void DriversTab_Click(object sender, RoutedEventArgs e) { SetTab("drivers"); }
    private void AppsTab_Click(object sender, RoutedEventArgs e) { SetTab("apps"); ListHeading.Text = _schedule.Language == "no" ? "Programoppdateringer (WinGet)" : "Application Updates (WinGet)"; UpdateAppSelection(); }
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

    private async void AppsScan_Click(object sender, RoutedEventArgs e)
    {
        ScanProgressText.Text = "Scanning applications with WinGet…";
        ScanProgressText.Text = "Scanning applications (WinGet)…";
        ScanProgressText.Visibility = Visibility.Visible;
        AppsScanButton.IsEnabled = false;
        try
        {
            var result = await WingetReader.ScanAsync();
            _rawWinget = result.Raw;
            _appEntries.Clear(); LoadAppPreferences(result.Updates);
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
        if (MessageBox.Show(this, $"Install {selected.Count} selected application updates?\n\n{names}\n\nSave your work and close affected applications. Remote support software may disconnect. No automatic restart will be initiated by Update Express.", "Confirm WinGet Installation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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
            RefreshHealthSummary();
            ActivityLog.TryWrite("WinGetUpgrade", "Completed - rescan required", new { Count = selected.Count }, source: "WinGet", runId: activityRunId);
            MessageBox.Show(this, string.Join("\n", logs) + "\n\nPlease rescan to verify the installed versions.\nLog: " + logPath, "WinGet Installation Results", MessageBoxButton.OK, MessageBoxImage.Information);
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
            "Optional auto-reboot: " + (_policy.WindowsAutomation.AutoReboot.Enabled
                ? (_policy.WindowsAutomation.AutoReboot.Mode == "outsideActiveHours"
                    ? "outside active hours"
                    : "ask for confirmation")
                : "off") + ".\n" +
            "Driver/application checkboxes remain preferences only.",
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
            $"  |  General={status.General.LastResult}";
        AutomationStatusText.ToolTip =
            $"Defender: {status.Defender.Detail}\nCritical: {status.Critical.Detail}\nGeneral: {status.General.Detail}";
    }
    private async void HistoryTab_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync(WindowsUpdateReader.History, "Windows Update History (last 500)");
        if (_showingHistory) RefreshHealthSummary();
    }
    private async void Health_Click(object sender, RoutedEventArgs e)
    {
        HealthButton.IsEnabled = false;
        HealthSummary.Text = "Reading update history and pending updates…";
        try
        {
            // Separate STA calls: no COM objects cross thread boundaries.
            var history = await WindowsUpdateReader.RunStaAsync(WindowsUpdateReader.History);
            var pending = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(true, true));
            _historySnapshot = history;
            _pendingSnapshot.Clear(); foreach (var item in pending) _pendingSnapshot.Add(item);
            RefreshHealthSummary();
            StatusText.Text = "Status check complete. No installation performed.";
        }
        catch (Exception ex) { HealthSummary.Text = "Status check failed: " + ex.Message; }
        finally { HealthButton.IsEnabled = true; }
    }
    private string CategorySummary()
    {
        int windows = _pendingSnapshot.Count(x => x.Category != "Driver / firmware" && !string.IsNullOrEmpty(x.UpdateId));
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
        var latestSuccess = success.Select(x => DateTime.TryParse(x.Date, out var d) ? d : DateTime.MinValue).DefaultIfEmpty().Max();
        var pendingKbs = _pendingSnapshot.SelectMany(x => SplitKbs(x.KB)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int laterSuccess = 0, stillPending = 0, unresolved = 0;
        foreach (var failure in failures)
        {
            var keys = SplitKbs(failure.KB).ToArray();
            if (keys.Length == 0) { unresolved++; continue; }
            var failureDate = DateTime.TryParse(failure.Date, out var d) ? d : DateTime.MinValue;
            if (keys.Any(k => pendingKbs.Contains(k))) { stillPending++; continue; }
            if (success.Any(x => DateTime.TryParse(x.Date, out var sd) && sd > failureDate && SplitKbs(x.KB).Intersect(keys, StringComparer.OrdinalIgnoreCase).Any())) laterSuccess++;
            else unresolved++;
        }
        HealthSummary.Text = CategorySummary() + $"\nHistory: {_historySnapshot.Count}  |  Failures: {failures.Count}  |  Resolved KB: {laterSuccess}  |  Unresolved: {unresolved}";
        HealthSummary.ToolTip = "History is limited to 500 events. Matching KB numbers do not prove repair.";
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
    public MainWindow()
    {
        InitializeComponent();
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
        if (_schedule.Use24HourClock) Clock24Option.IsChecked = true; else Clock12Option.IsChecked = true;
        if (_schedule.Language == "no") NorwegianOption.IsChecked = true; else EnglishOption.IsChecked = true;
        ApplyLanguage();
        RemindersEnabled.IsChecked = _schedule.Enabled;
        LoadAutomationPolicyIntoUi();
        RefreshScheduleStatus();
        _reminderTimer.Tick += (_, _) => CheckReminder();
        _reminderTimer.Start();
        _monitorUiTimer.Tick += (_, _) => { RefreshMonitorPanel(); RefreshAutomationStatusTexts(); };
        _monitorUiTimer.Start();
        RefreshMonitorPanel();
        RefreshAutomationStatusTexts();
        Loaded += async (_, _) =>
        {
            CheckReminder();
            await RefreshDefenderAsync();
            await RefreshFirewallAsync();
            // Quiet self-update check when the main window opens (prompt only if newer).
            AppUpdatePrompt.StartCheck(
                automatic: true,
                onUpdateInstallRequested: StartAppSelfUpdateInstall,
                balloon: null);
        };
        _connectivity.Updated += UpdateConnectivity;
        Loaded += (_, _) => _connectivity.Start();
        Closed += (_, _) => _connectivity.Dispose();
    }

    private void StartAppSelfUpdateInstall(AppUpdateCheckResult offer)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text = "Downloading Update Express update…";
                await AppUpdateInstaller.PrepareAndLaunchAsync(offer, stage =>
                {
                    AgentLog.Write("App update (UI): " + stage);
                    try { Dispatcher.BeginInvoke(new Action(() => StatusText.Text = stage)); }
                    catch { }
                }).ConfigureAwait(true);
                // LaunchSelfUpdate shuts down this process on success.
            }
            catch (Exception ex)
            {
                AgentLog.Write("App update install from main window failed: " + ex);
                AppUpdatePrompt.ReleaseCheckLock();
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
    private string FormatTime(DateTime time) => Clock24Option.IsChecked == true ? time.ToString("ddd dd MMM yyyy HH:mm", System.Globalization.CultureInfo.GetCultureInfo("en-US")) : time.ToString("ddd dd MMM yyyy h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
    private void ClockFormat_Changed(object sender, RoutedEventArgs e)
    {
        if (_schedule == null || Clock24Option == null) return;
        _schedule.Use24HourClock = Clock24Option.IsChecked == true;
        _schedule.Save(); RefreshScheduleStatus();
    }
    private bool _languageReady;
    private static readonly Dictionary<string, string> Norwegian = new(StringComparer.Ordinal)
    {
        ["Windows Update Control"] = "Windows Update-kontroll", ["Check for Updates"] = "Søk etter oppdateringer",
        ["Install Selected Updates"] = "Installer valgte oppdateringer", ["Installation History"] = "Installasjonshistorikk",
        ["Windows Update fix"] = "Windows Update-fiks", ["Error report"] = "Feilrapport",
        ["Export CSV"] = "Eksporter CSV", ["Diagnostics"] = "Diagnostikk", ["Check Status"] = "Kontroller status",
        ["Refresh Security Status"] = "Oppdater sikkerhetsstatus", ["Private"] = "Privat", ["Public"] = "Offentlig", ["Domain"] = "Domene",
        ["Results"] = "Resultater", ["Application Updates (WinGet)"] = "Programoppdateringer (WinGet)",
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
        ["Drivers automatic (optional, not implemented)"] = "Drivere automatisk (valgfritt, ikke implementert)",
        ["Application automatic (optional, not implemented)"] = "Programmer automatisk (valgfritt, ikke implementert)",
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
        ["Critical and general updates download in the background and ask before install. Default preferred time is 12:00. Auto-reboot is optional and off by default."] =
            "Kritiske og generelle oppdateringer lastes ned i bakgrunnen og spør før installasjon. Standard foretrukket tid er 12:00. Auto-omstart er valgfri og av som standard.",
        ["Enabled"] = "Aktivert",
        ["Every day"] = "Hver dag", ["Monday"] = "Mandag", ["Tuesday"] = "Tirsdag", ["Wednesday"] = "Onsdag",
        ["Thursday"] = "Torsdag", ["Friday"] = "Fredag", ["Saturday"] = "Lørdag", ["Sunday"] = "Søndag"
    };
    private void Language_Changed(object sender, RoutedEventArgs e)
    {
        if (!_languageReady || EnglishOption == null || NorwegianOption == null) return;
        _schedule.Language = NorwegianOption.IsChecked == true ? "no" : "en";
        _schedule.Save(); ApplyLanguage();
    }
    private void ApplyLanguage()
    {
        if (EnglishOption == null) return;
        _languageReady = true;
        bool norwegian = NorwegianOption.IsChecked == true;
        void Translate(object? element)
        {
            if (element is System.Windows.Controls.ContentControl control && control.Content is string content)
            {
                if (control.Tag is not string original) { original = content; control.Tag = original; }
                var match = Norwegian.Keys.FirstOrDefault(k => original == k || original.EndsWith("  " + k, StringComparison.Ordinal));
                if (match != null) control.Content = norwegian ? original.Replace(match, Norwegian[match]) : original;
            }
            if (element is System.Windows.Controls.TextBlock block && block.Name is not ("DefenderLabel" or "DefenderChecked" or "HealthSummary" or "StatusText" or "ScheduleStatus" or "AppsInfo" or "AppsSelectedCount" or "SelectedCountText" or "AutomationStatusText" or "DefenderAutomationStatus" or "CriticalAutomationStatus" or "GeneralAutomationStatus" or "FooterSafetyText") && !string.IsNullOrEmpty(block.Text))
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
        ListHeading.Text = norwegian ? (_activeTab == "apps" ? "Programoppdateringer (WinGet)" : "Resultater") : (_activeTab == "apps" ? "Application Updates (WinGet)" : "Results");
        RefreshScheduleStatus();
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
        var dialog = new ReminderDialog(isTest, occurrence, _schedule.Language == "no", _schedule.Use24HourClock) { Owner = this };
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
    private async Task LoadAsync(Func<List<UpdateEntry>> operation, string heading)
    {
        if (_busy) return;
        _busy = true;
        InstallButton.Content = "Please wait…";
        ScanButton.IsEnabled = HistoryButton.IsEnabled = InstallButton.IsEnabled = false;
        UpdateSelectionControls();
        StatusText.Text = "Loading installation history…";
        try
        {
            var result = await WindowsUpdateReader.RunStaAsync(operation);
            _historySnapshot = result;
            RefreshHealthSummary();
            ListHeading.Text = heading;
            SetTab("history");
            StatusText.Text = $"History loaded: {result.Count} entries. Pending updates remain separate.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "History load failed. See error details.";
            MessageBox.Show(this, ex.Message, "Windows Update history error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; ScanButton.IsEnabled = HistoryButton.IsEnabled = true; UpdateSelectionControls(); }
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        ScanButton.IsEnabled = HistoryButton.IsEnabled = AppsScanButton.IsEnabled = false;
        UpdateSelectionControls(); UpdateAppSelection();
        ScanProgressText.Visibility = Visibility.Visible;
        try
        {
            ScanProgressText.Text = "Scanning Windows updates and drivers…";
            // Capture WPF checkbox state on the UI thread BEFORE passing work to the STA worker.
            // Accessing IncludeOptionalUpdates from the worker throws InvalidOperationException.
            bool includeOptional = IncludeOptionalUpdates.IsChecked == true;
            bool allDrivers = ShowAllDrivers.IsChecked == true;
            var windows = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(includeOptional, allDrivers));
            _pendingSnapshot.Clear(); foreach (var entry in windows) _pendingSnapshot.Add(entry);
            _lastSuccessfulScan = DateTime.Now;
            SetTab("software");
            int driverCount = windows.Count(e => e.Category == "Driver / firmware");
            ScanProgressText.Text = "Scanning applications (WinGet)…";
            try
            {
                var apps = await WingetReader.ScanAsync();
                _rawWinget = apps.Raw;
                _appEntries.Clear(); LoadAppPreferences(apps.Updates);
                RefreshHealthSummary();
                StatusText.Text = $"Windows: {windows.Count - driverCount}, drivers: {driverCount}, applications: {apps.Updates.Count}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "WinGet scan failed: " + ex.Message;
                StatusText.Text = $"Windows scan complete; WinGet failed: {ex.Message}";
            }
            RefreshHealthSummary();
            ScanProgressText.Text = "Scan complete";
            await Task.Delay(900);
        }
        catch (Exception ex)
        {
            ScanProgressText.Text = "Scan failed";
            ActivityLog.TryWrite("UpdateScan", "Failed", new { Exception = ex.ToString() });
            MessageBox.Show(this, ex.Message, "Update scan error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ScanProgressText.Visibility = Visibility.Collapsed;
            _busy = false;
            ScanButton.IsEnabled = HistoryButton.IsEnabled = AppsScanButton.IsEnabled = true;
            UpdateSelectionControls(); UpdateAppSelection();
        }
    }
    private void WindowsUpdateFix_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new WindowsUpdateFixDialog { Owner = this };
        dlg.ShowDialog();
    }

    private void ErrorReport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ErrorReportDialog { Owner = this };
        dlg.ShowDialog();
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (!DiagnosticsButton.IsEnabled) return;
        DiagnosticsButton.IsEnabled = false;
        DiagnosticsButton.Content = "Please wait…";
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
            DiagnosticsButton.Content = "⚙  Diagnostics";
            DiagnosticsButton.IsEnabled = true;
        }
    }
    private async void History_Click(object sender, RoutedEventArgs e) => await LoadAsync(WindowsUpdateReader.History, "Windows Update History (last 500)");
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
        var dlg = new ErrorCodeFixDialog(entry) { Owner = this };
        dlg.ShowDialog();
    }

    private void UpdateDetails_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (UpdatesGrid.SelectedItem is not UpdateEntry entry) return;
        if (entry.HasFixableErrorCode)
        {
            var dlg = new ErrorCodeFixDialog(entry) { Owner = this };
            dlg.ShowDialog();
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
        MessageBox.Show(this, $"Update: {entry.Title}\nKB: {entry.KB}\nCategory: {entry.Category}\nStatus: {entry.Status}\nDate: {entry.Date}\nError: {entry.ErrorCode}\n\n{assessment}\n\nUninstall is not implemented.", "Update Details (read-only)", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Entries.Count == 0) { MessageBox.Show(this, "No entries to export."); return; }
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"UpdateExpress-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("KB;Update;Status;DateTime;Source;ErrorCode\r\n");
        foreach (var x in Entries) sb.AppendLine(string.Join(';', new[] { x.KB, x.Title, x.Status, x.Date, x.Source, x.ErrorCode }.Select(Q)));
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
            var choice = MessageBox.Show(this,
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
                MessageBox.Show(this, "Administrator approval cancelled. No installation was started.",
                    "Update Express", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Unable to restart as administrator: " + ex.Message,
                    "Update Express", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }
        if (drivers.Any(x => !IsManuallyInstallableDriver(x)))
        {
            MessageBox.Show(this, "Only individually verified non-firmware drivers can be installed. Firmware installation remains blocked.", "Driver safety", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (drivers.Count > 0)
        {
            var summary = string.Join("\n", drivers.Select(d => $"• {d.Title} [{d.DriverHardwareId}]"));
            if (MessageBox.Show(this,
                $"Install {drivers.Count} manually selected driver(s), sequentially?\n\n{summary}\n\nEach driver is rechecked against WUA and its hardware ID. A failure will not stop subsequent drivers. Firmware is blocked. No automatic restart.",
                "Confirm driver installation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        var queue = new List<InstallationQueueItem>();
        queue.AddRange(software.Select(x => new InstallationQueueItem("Windows Update", x.Title)));
        queue.AddRange(drivers.Select(x => new InstallationQueueItem("Driver", x.Title)));
        queue.AddRange(apps.Select(x => new InstallationQueueItem("WinGet", x.DisplayName)));
        var manager = new InstallationManagerWindow(queue) { Owner = this };
        if (MessageBox.Show(this, $"Install {queue.Count} selected updates from all tabs?\n\nWindows Updates: {software.Count}\nDrivers: {drivers.Count}\nWinGet: {apps.Count}\n\nNo automatic restart will be performed.", "Confirm combined installation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true;
        InstallButton.Content = "Installing updates…";
        ScanButton.IsEnabled = HistoryButton.IsEnabled = AppsScanButton.IsEnabled = false;
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
        try
        {
            Log("InstallationManager", "Started", queue.Select(q => new { q.Source, q.Name }).ToArray());
            if (software.Count > 0)
            {
                int start = cursor;
                manager.BeginItem(cursor, "Downloading / installing Windows updates (WUA)…");
                try
                {
                    IProgress<string> progress = new Progress<string>(stage => { manager.SetStage(stage); Log("WUA", "Progress", stage); });
                    var results = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.InstallSelected(software, stage => ((IProgress<string>)progress).Report(stage)));
                    foreach (var entry in results) Log("WUA", entry.Status, new { entry.Title, entry.KB, entry.ErrorCode });
                    for (int i = 0; i < software.Count; i++)
                    {
                        var requested = software[i];
                        var result = results.FirstOrDefault(x => x.UpdateId.Equals(requested.UpdateId, StringComparison.OrdinalIgnoreCase))
                            ?? results.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.KB) && x.KB.Equals(requested.KB, StringComparison.OrdinalIgnoreCase));
                        var state = result == null ? "Not verified" :
                            result.Status is "Installert" or "Already up to date" ? "Verifying" :
                            result.Status is "Delvis fullført" ? "Not verified" :
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
                manager.BeginItem(idx, "Rechecking driver identity: " + d.Title + "…");
                try
                {
                    IProgress<string> progress = new Progress<string>(stage => { manager.SetStage(stage); Log("Driver", "Progress", new { d.Title, Stage = stage }); });
                    var results = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.InstallSelectedDriver(d, stage => progress.Report(stage), true));
                    foreach (var entry in results) Log("Driver", entry.Status, new { entry.Title, entry.ErrorCode });
                    var driverResult = results.FirstOrDefault();
                    string driverState = driverResult == null ? "Not verified" :
                        driverResult.Status == "Installert" ? "Verifying" :
                        driverResult.Status == "Delvis fullført" ? "Not verified" :
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
                manager.CompleteItem(idx, code == 0 ? "Verifying" : $"Failed (exit {code})", code != 0);
            }
            manager.SetStage("Refreshing Windows Update and WinGet results…");
            try
            {
                var pending = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(true, true));
                _pendingSnapshot.Clear(); foreach (var entry in pending) _pendingSnapshot.Add(entry);
                _historySnapshot = await WindowsUpdateReader.RunStaAsync(WindowsUpdateReader.History);
                foreach (var item in software.Select((update, i) => new { update, i }))
                {
                    if (queue[item.i].State.StartsWith("Failed", StringComparison.Ordinal)) continue;
                    bool remains = pending.Any(x => x.UpdateId.Equals(item.update.UpdateId, StringComparison.OrdinalIgnoreCase));
                    // Absence from pending list is not proof of installation. A successful
                    // installer result + absence is the minimum verification we can make here.
                    manager.CompleteItem(item.i, remains ? "Not verified" :
                        queue[item.i].State == "Verifying" ? "Completed" : "Not verified", remains);
                }
                for (int i = 0; i < drivers.Count; i++)
                {
                    int driverIndex = software.Count + i;
                    if (queue[driverIndex].State.StartsWith("Failed", StringComparison.Ordinal)) continue;
                    bool remains = pending.Any(x => x.UpdateId.Equals(drivers[i].UpdateId, StringComparison.OrdinalIgnoreCase));
                    manager.CompleteItem(driverIndex, remains ? "Not verified" :
                        queue[driverIndex].State == "Verifying" ? "Completed" : "Not verified", remains);
                }
                Log("WUA", "Rescan completed", new { Count = pending.Count });
            }
            catch (Exception ex) { Log("WUA", "Rescan failed", ex.Message); manager.SetStage("Windows Update rescan failed: " + ex.Message); }
            try
            {
                var scan = await WingetReader.ScanAsync();
                _rawWinget = scan.Raw;
                _appEntries.Clear(); LoadAppPreferences(scan.Updates);
                foreach (var item in apps.Select((app, i) => new { app, i }))
                {
                    if (queue[software.Count + drivers.Count + item.i].State.StartsWith("Failed", StringComparison.Ordinal)) continue;
                    bool remains = scan.Updates.Any(x => x.Id.Equals(item.app.Id, StringComparison.OrdinalIgnoreCase));
                    manager.CompleteItem(software.Count + drivers.Count + item.i, remains ? "Not verified" : "Completed", remains);
                    Log("WinGet", remains ? "Not verified" : "Verified absent from upgrade list", new { item.app.Id, item.app.Available });
                }
            }
            catch (Exception ex) { Log("WinGet", "Rescan failed", ex.Message); manager.SetStage("WinGet rescan failed: " + ex.Message); }
            SetTab(_activeTab);
            RefreshHealthSummary();
            Log("InstallationManager", "Finished", new { Total = queue.Count });
            manager.Finish();
            StatusText.Text = "Installation finished. Review Installation Manager results.";
        }
        catch (Exception ex)
        {
            Log("InstallationManager", "Failed", ex.ToString());
            manager.SetStage("Installation interrupted: " + ex.Message);
            manager.Finish();
            MessageBox.Show(this, ex.Message, "Installation Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            InstallButton.Content = "↓  Install Selected Updates";
            ScanButton.IsEnabled = HistoryButton.IsEnabled = AppsScanButton.IsEnabled = true;
            UpdateSelectionControls();
        }
    }

}
