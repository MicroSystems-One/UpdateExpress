using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using UpdateExpress.Models;
using UpdateExpress.Services;
using UpdateExpress.Services.OemDrivers;
namespace UpdateExpress;
public partial class MainWindow : Window
{
    public ObservableCollection<UpdateEntry> Entries { get; } = new();
    private ReminderSchedule _schedule = new();
    private readonly ICollectionView _entriesView;
    private string _activeTab = "all";
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
    // Keep Historikk↔pending sync snappy so Defender «Sjekket» can follow installs quickly.
    private readonly DispatcherTimer _pendingReconcileTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private int _reconcileRunning;
    private DateTime _lastReconcileAttempt = DateTime.MinValue;
    private bool VisibleInTab(object item)
    {
        if (item is not UpdateEntry e) return false;
        if (_activeTab is "history" or "all") return true;
        return _activeTab switch
        {
            "software" => e.Category != "Driver / firmware",
            "drivers" => e.Category == "Driver / firmware",
            _ => false
        };
    }
    public bool CanSelectDisplayedEntry => _activeTab is "history" or "drivers" or "software" or "all";
    private bool _busy;
    private bool _scanButtonSearching;
    private bool _historyActionsEnabled = true;
    private bool _healthCheckRunning;
    private bool _diagnosticsRunning;
    private FixItWindow? _fixItWindow;
    private readonly DispatcherTimer _installSelectionBlinkTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private bool _installSelectionBlinkOn;
    private static bool IsManuallyInstallableDriver(UpdateEntry e) =>
        e.Category == "Driver / firmware" &&
        !string.IsNullOrWhiteSpace(e.UpdateId) &&
        !string.IsNullOrWhiteSpace(e.DriverHardwareId);

    private static bool IsOemDownloadable(UpdateEntry e) =>
        e.Category == "Driver / firmware" && e.OpensExternalUrl;

    private bool IsEligiblePending(UpdateEntry e) =>
        !_showingHistory &&
        !string.IsNullOrWhiteSpace(e.UpdateId) &&
        (e.Category == "Driver / firmware" ? IsManuallyInstallableDriver(e) : e.SafeDefault);

    /// <summary>WUA-installable rows plus OEM catalog rows (both handled by Install Selected).</summary>
    private bool IsSelectablePending(UpdateEntry e) =>
        IsEligiblePending(e) || (!_showingHistory && IsOemDownloadable(e));

    private const string WinGetSource = "WinGet";

    private static bool IsWinGetRow(UpdateEntry e) =>
        string.Equals(e.Source, WinGetSource, StringComparison.OrdinalIgnoreCase);

    private UpdateEntry ToAllListAppEntry(AppUpdate app) => new()
    {
        IsSelected = app.IsSelected,
        UpdateId = app.Id,
        Category = "Application",
        SafeDefault = true,
        Title = app.DisplayName,
        VersionDisplay = app.VersionSummary,
        Status = app.Status,
        Priority = "Unrated",
        PriorityExplanation = app.PriorityExplanation,
        Source = WinGetSource,
        KB = app.KB
    };

    private void SyncWingetSelectionFromAllEntries()
    {
        if (_activeTab != "all") return;
        foreach (var app in _appEntries)
        {
            var row = Entries.FirstOrDefault(e => IsWinGetRow(e) &&
                string.Equals(e.UpdateId, app.Id, StringComparison.OrdinalIgnoreCase));
            if (row is not null) app.IsSelected = row.IsSelected;
        }
    }

    private void UpdateSelectionControls()
    {
        SyncWingetSelectionFromAllEntries();
        var visible = Entries.Where(e => VisibleInTab(e)).ToList();
        // On All, WinGet rows share the grid but selection is counted via _appEntries (avoid double-count).
        var eligibleWindows = visible.Where(e => !IsWinGetRow(e) && IsSelectablePending(e)).ToList();
        var selected = eligibleWindows.Count(e => e.IsSelected);
        int appCount = _appEntries.Count(a => a.IsSelected);
        bool includeApps = _activeTab is "apps" or "all";
        bool includeWindows = _activeTab is not "apps";
        int windowEligible = includeWindows ? eligibleWindows.Count : 0;
        int appEligible = includeApps ? _appEntries.Count : 0;
        int totalEligible = windowEligible + appEligible;
        int totalSelected = (includeWindows ? selected : 0) + (includeApps ? appCount : 0);
        SelectAllUpdates.IsEnabled = !_busy && totalEligible > 0;
        SelectAllUpdates.IsChecked = totalEligible > 0 && totalSelected == totalEligible;
        SelectedCountText.Text = _schedule.Language == "no" ? $"{totalSelected} valgt" : $"{totalSelected} selected";
        InstallButton.IsEnabled = !_busy && !_showingHistory &&
            (_pendingSnapshot.Any(e => e.IsSelected && (!string.IsNullOrWhiteSpace(e.UpdateId) || IsOemDownloadable(e))) || appCount > 0);
        UpdateInstallSelectionBlink();
    }

    private void UpdateCheckbox_Click(object sender, RoutedEventArgs e)
    {
        SyncWingetSelectionFromAllEntries();
        UpdateSelectionControls();
    }
    private void SelectAllUpdates_Click(object sender, RoutedEventArgs e)
    {
        bool value = SelectAllUpdates.IsChecked == true;
        if (_activeTab is "apps" or "all")
            foreach (var app in _appEntries) app.IsSelected = value;
        if (_activeTab is not "apps")
            foreach (var item in Entries.Where(e => VisibleInTab(e) && IsSelectablePending(e))) item.IsSelected = value;
        if (_activeTab == "all")
            SyncWingetSelectionFromAllEntries();
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
            // All: one shared list — append WinGet apps into the same grid (no second header).
            if (tab == "all")
            {
                foreach (var app in _appEntries)
                    Entries.Add(ToAllListAppEntry(app));
            }
        }
        _entriesView.Refresh();
        // All tabs share the same visible column order. Empty values remain blank.
        AllTab.FontWeight = SoftwareTab.FontWeight = DriversTab.FontWeight = HistoryTab.FontWeight = AppsTab.FontWeight = FontWeights.Normal;
        PaintResultTab(AllTab, tab == "all");
        PaintResultTab(SoftwareTab, tab == "software");
        PaintResultTab(DriversTab, tab == "drivers");
        PaintResultTab(AppsTab, tab == "apps");
        PaintResultTab(HistoryTab, tab == "history");
        bool showWindowsGrid = tab is not "apps";
        bool showAppsGrid = tab == "apps";
        bool showAppsToolbar = tab is "apps" or "all";
        WindowsUpdatesTable.Visibility = showWindowsGrid ? Visibility.Visible : Visibility.Collapsed;
        AppsActions.Visibility = showAppsGrid ? Visibility.Visible : Visibility.Collapsed;
        WindowsResultsRow.Height = showWindowsGrid ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        AppsResultsRow.Height = showAppsGrid ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        // Strip stays hidden; WinGet actions appear in the gear menu on apps/all tabs.
        AppsToolbar.Visibility = Visibility.Collapsed;
        var wingetMenu = showAppsToolbar ? Visibility.Visible : Visibility.Collapsed;
        if (AppsScanMenuItem != null) AppsScanMenuItem.Visibility = wingetMenu;
        if (AppsExportMenuItem != null) AppsExportMenuItem.Visibility = wingetMenu;
        SelectAllUpdates.Visibility = Visibility.Visible;
        SelectedCountText.Visibility = Visibility.Visible;
        HistoryPdfButton.Visibility = tab == "history" ? Visibility.Visible : Visibility.Collapsed;
        // History must not expose the OEM DOC download (even if a row still has URLs).
        if (OemDocColumn != null)
            OemDocColumn.Visibility = tab == "history" ? Visibility.Collapsed : Visibility.Visible;
        UpdatesGrid.Items.Refresh();
        UpdateSelectionControls();
        if (_languageReady) ApplyLanguage();
        RefreshFeatureUpdateButton();
        RefreshUpdateStatusPanel();
    }
    private static void PaintResultTab(Button button, bool selected)
    {
        if (selected)
        {
            // Same blue as RowPrimaryButton ("Check for updates" / "Se etter oppdateringer").
            button.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0067C0"));
            button.Foreground = System.Windows.Media.Brushes.White;
            button.BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0055A8"));
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
    public static readonly System.Windows.DependencyProperty InstallButtonPulseProperty =
        System.Windows.DependencyProperty.Register(nameof(InstallButtonPulse), typeof(bool), typeof(MainWindow), new System.Windows.PropertyMetadata(false));
    /// <summary>Soft red blink while one or more updates are selected (not during active install).</summary>
    public bool InstallButtonPulse
    {
        get => (bool)GetValue(InstallButtonPulseProperty);
        set => SetValue(InstallButtonPulseProperty, value);
    }
    private void AllTab_Click(object sender, RoutedEventArgs e)
    {
        SetTab("all");
        HidePendingAlreadyInHistory(_historySnapshot);
        UpdateAppSelection();
    }
    private void SoftwareTab_Click(object sender, RoutedEventArgs e)
    {
        SetTab("software");
        HidePendingAlreadyInHistory(_historySnapshot);
    }
    private void UpdateInstallSelectionBlink()
    {
        bool hasSelection = InstallButton.IsEnabled && !InstallButtonActive && !_busy && !_showingHistory;
        if (!hasSelection)
        {
            if (_installSelectionBlinkTimer.IsEnabled)
                _installSelectionBlinkTimer.Stop();
            _installSelectionBlinkOn = false;
            InstallButtonPulse = false;
            return;
        }
        if (!_installSelectionBlinkTimer.IsEnabled)
        {
            _installSelectionBlinkOn = true;
            InstallButtonPulse = true;
            _installSelectionBlinkTimer.Start();
        }
    }
    private void InstallSelectionBlinkTimer_Tick(object? sender, EventArgs e)
    {
        if (!InstallButton.IsEnabled || InstallButtonActive || _busy || _showingHistory)
        {
            _installSelectionBlinkTimer.Stop();
            _installSelectionBlinkOn = false;
            InstallButtonPulse = false;
            return;
        }
        _installSelectionBlinkOn = !_installSelectionBlinkOn;
        InstallButtonPulse = _installSelectionBlinkOn;
    }
    private void DriverMode_Changed(object sender, RoutedEventArgs e)
    {
        // Mode changes the WUA / OEM query, not just the UI. A fresh scan is required.
        if (!IsLoaded || StatusText is null) return;
        StatusText.Text = _schedule.Language == "no"
            ? "Drivermodus endret. Klikk Se etter oppdateringer for å oppdatere WUA-/OEM-resultater."
            : "Driver mode changed. Click Check for Updates to refresh WUA / OEM results.";
    }

    private static bool IsMissingIdentity(string value) =>
        string.IsNullOrWhiteSpace(value) || value is "—" or "…" or "-" or "N/A" or "n/a";

    private void OpenOemDriverEntry(UpdateEntry entry) =>
        _ = DownloadOemEntryAsync(entry, package: true, quiet: false);

    private void OpenOemReadmeEntry(UpdateEntry entry) =>
        _ = DownloadOemEntryAsync(entry, package: false, quiet: false);

    /// <summary>
    /// OEM part of Install Selected. Each package takes the same path as a double-clicked OEM row
    /// (download, then run its installer and wait for it), one at a time, without per-package questions.
    /// </summary>
    /// <param name="confirm">False when the combined Install Selected dialog already asked.</param>
    private async Task InstallSelectedOemAsync(IReadOnlyList<UpdateEntry> selected, bool confirm)
    {
        if (selected.Count == 0 || _busy) return;
        bool norwegian = _schedule.Language == "no";
        string title = norwegian ? "OEM-installasjon" : "OEM install";
        static string Bullets(IReadOnlyList<UpdateEntry> items, bool norwegian)
        {
            const int max = 12;
            string list = string.Join("\n", items.Take(max).Select(x => "• " + x.DisplayTitle));
            if (items.Count > max)
                list += norwegian ? $"\n… og {items.Count - max} til" : $"\n… and {items.Count - max} more";
            return list;
        }
        if (confirm)
        {
            string question = selected.Count == 1
                ? (norwegian ? "Laste ned og installere 1 valgt OEM-pakke?" : "Download and install 1 selected OEM package?")
                : (norwegian
                    ? $"Laste ned og installere {selected.Count} valgte OEM-pakker?"
                    : $"Download and install {selected.Count} selected OEM packages?");
            var answer = MessageBox.Show(this,
                question + "\n\n" + Bullets(selected, norwegian) + (norwegian
                    ? "\n\nHver pakke lastes ned til Nedlastinger\\UpdateExpress-OEM, og installasjonsprogrammet kjøres, én om gangen. Følg vinduet til hvert installasjonsprogram. Pakker uten installasjonsprogram (f.eks. zip) blir bare lagret.\n\nEt OEM-installasjonsprogram kan selv kreve eller utføre omstart.\n\nJA: start.\nNEI: avbryt."
                    : "\n\nEach package is downloaded to Downloads\\UpdateExpress-OEM and its installer is run, one at a time. Follow each installer's own window. Packages without an installer (e.g. zip) are only saved.\n\nAn OEM installer may require or perform a restart itself.\n\nYES: start.\nNO: cancel."),
                title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return;
        }

        _busy = true;
        InstallButtonActive = true;
        InstallButton.Content = norwegian ? "Installerer OEM-pakker…" : "Installing OEM packages…";
        UpdateSelectionControls();
        int ran = 0;
        var savedOnly = new List<UpdateEntry>();
        var notDownloaded = new List<UpdateEntry>();
        var installerFailed = new List<UpdateEntry>();
        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var entry = selected[i];
                StatusText.Text = $"OEM {i + 1}/{selected.Count}: {entry.DisplayTitle}";
                switch (await DownloadOemEntryAsync(entry, package: true, quiet: true).ConfigureAwait(true))
                {
                    case OemPackageOutcome.InstallerRan: ran++; break;
                    case OemPackageOutcome.Saved: savedOnly.Add(entry); break;
                    case OemPackageOutcome.InstallerFailed: installerFailed.Add(entry); break;
                    default: notDownloaded.Add(entry); break;
                }
            }
            int failed = notDownloaded.Count + installerFailed.Count;
            StatusText.Text = norwegian
                ? $"OEM ferdig: {ran} installasjonsprogram kjørt, {savedOnly.Count} bare lagret, {failed} feilet."
                : $"OEM finished: {ran} installer(s) run, {savedOnly.Count} saved only, {failed} failed.";
            if (savedOnly.Count + failed > 0)
            {
                if (savedOnly.Count > 0)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = OemPackageDownload.DownloadsRoot,
                            UseShellExecute = true
                        });
                    }
                    catch { /* folder open is best-effort */ }
                }

                // One summary instead of the per-package dialogs of the per-row flow.
                var summary = new StringBuilder(StatusText.Text);
                void Section(string heading, IReadOnlyList<UpdateEntry> items)
                {
                    if (items.Count > 0)
                        summary.Append("\n\n").Append(heading).Append('\n').Append(Bullets(items, norwegian));
                }
                Section(norwegian
                    ? "Lagret uten installasjonsprogram – installer fra Nedlastinger\\UpdateExpress-OEM (åpnet):"
                    : "Saved without an installer to run – install from Downloads\\UpdateExpress-OEM (opened):", savedOnly);
                Section(norwegian ? "Nedlasting feilet:" : "Download failed:", notDownloaded);
                Section(norwegian
                    ? "Installasjonsprogrammet feilet eller ble ikke startet:"
                    : "Installer failed or did not start:", installerFailed);
                summary.Append(norwegian
                    ? "\n\nDisse oppføringene forblir tilgjengelige under Drivere / fastvare."
                    : "\n\nThese entries stay available under Drivers / Firmware.");
                MessageBox.Show(this, summary.ToString(), title, MessageBoxButton.OK,
                    failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }
        finally
        {
            _busy = false;
            InstallButtonActive = false;
            ResetInstallButtonLabel();
            UpdateSelectionControls();
            RefreshHealthSummary();
        }
    }

    private enum OemPackageOutcome { NotDownloaded, Saved, InstallerRan, InstallerFailed }

    private static bool LooksLikeOemInstaller(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);

    /// <param name="quiet">
    /// True from Install Selected, which asked once for the whole selection: nothing is asked
    /// per package, and a saved installer is run straight away.
    /// </param>
    private async Task<OemPackageOutcome> DownloadOemEntryAsync(UpdateEntry entry, bool package, bool quiet)
    {
        bool norwegian = _schedule.Language == "no";
        string url = package ? entry.ExternalUrl : entry.ReadmeUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            if (!quiet)
            {
                MessageBox.Show(this,
                    package
                        ? (norwegian ? "Ingen pakke-/nedlastingslenke for denne OEM-oppføringen." : "No package download link for this OEM entry.")
                        : (norwegian ? "Ingen TXT/readme-lenke for denne OEM-oppføringen." : "No TXT/readme link for this OEM entry."),
                    norwegian ? "OEM-driver" : "OEM driver",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return OemPackageOutcome.NotDownloaded;
        }

        string brand = entry.Source.StartsWith("OEM / ", StringComparison.OrdinalIgnoreCase)
            ? entry.Source["OEM / ".Length..].Trim()
            : "OEM";
        if (!quiet)
        {
            string kindNo = package ? "pakken (exe/zip/…)" : "TXT/readme-filen";
            string kindEn = package ? "the package (exe/zip/…)" : "the TXT/readme file";
            string details =
                $"{(norwegian ? "Driver" : "Driver")}: {entry.DisplayTitle}\n" +
                $"{(norwegian ? "Versjon" : "Version")}: {entry.UnifiedVersion}\n" +
                $"{(norwegian ? "Kategori" : "Category")}: {entry.DriverClass}\n" +
                $"{(norwegian ? "Kilde" : "Source")}: {entry.Source}\n" +
                $"{(norwegian ? "Status" : "Status")}: {entry.DriverStatus}\n\n" +
                $"{entry.DriverReason}\n\n" +
                (norwegian
                    ? $"JA: last ned {kindNo} i Update Express (uten nettleser).\nNEI: lukk."
                    : $"YES: download {kindEn} in Update Express (no browser).\nNO: close.");
            var answer = MessageBox.Show(this, details,
                norwegian ? $"OEM-driver ({brand})" : $"OEM driver ({brand})",
                MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return OemPackageOutcome.NotDownloaded;
        }

        string? path = null;
        try
        {
            if (!quiet && StatusText is not null)
                StatusText.Text = norwegian ? "Laster ned OEM-pakke…" : "Downloading OEM package…";
            path = await OemPackageDownload.DownloadAsync(
                url,
                preferredFileName: null,
                allowTextReadme: !package,
                progress: msg => Dispatcher.Invoke(() =>
                {
                    if (StatusText is not null) StatusText.Text = msg;
                })).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ActivityLog.TryWrite("OemDownload", "Failed", new
            {
                Package = package,
                Title = entry.DisplayTitle,
                Url = url,
                Exception = ex.ToString()
            });
            if (!quiet)
            {
                var fallback = MessageBox.Show(this,
                    norwegian
                        ? $"Nedlasting i appen feilet:\n{ex.Message}\n\nÅpne lenken i nettleseren i stedet?"
                        : $"In-app download failed:\n{ex.Message}\n\nOpen the link in the browser instead?",
                    norwegian ? "OEM-nedlasting" : "OEM download",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (fallback == MessageBoxResult.Yes)
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch (Exception openEx)
                    {
                        MessageBox.Show(this, openEx.Message,
                            norwegian ? "Kunne ikke åpne lenke" : "Could not open link",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            return OemPackageOutcome.NotDownloaded;
        }

        ActivityLog.TryWrite("OemDownload", "Saved", new
        {
            Package = package,
            Title = entry.DisplayTitle,
            Path = path,
            OemPackageId = entry.OemPackageId
        });

        string note = package
            ? (norwegian
                ? $"OEM-pakke lastet ned til {path}"
                : $"OEM package downloaded to {path}")
            : (norwegian
                ? $"OEM TXT/readme lastet ned til {path}"
                : $"OEM TXT/readme downloaded to {path}");

        var outcome = OemPackageOutcome.Saved;
        if (package)
        {
            // Track download without hiding — reboot-pending can promote Chipset/Graphics
            // that were run from Downloads outside TryRunOemInstallerAsync.
            OemHandledStore.RememberDownloadPending(entry, path);
            if (StatusText != null) StatusText.Text = note;

            bool looksExe = LooksLikeOemInstaller(path);
            if (quiet && looksExe)
                outcome = await TryRunOemInstallerAsync(entry, path, norwegian, quiet: true).ConfigureAwait(true)
                    ? OemPackageOutcome.InstallerRan
                    : OemPackageOutcome.InstallerFailed;
            if (!quiet)
            {
                if (looksExe)
                {
                    var run = MessageBox.Show(this,
                        norwegian
                            ? $"Lagret:\n{path}\n\nJA: kjør installasjonsprogrammet nå.\nNEI: behold filen — du kan installere fra mappen (merk deretter som installert)."
                            : $"Saved:\n{path}\n\nYES: run the installer now.\nNO: keep the file — you can install from the folder (then mark as installed).",
                        norwegian ? $"OEM-driver ({brand})" : $"OEM driver ({brand})",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                    if (run == MessageBoxResult.Yes)
                    {
                        outcome = await TryRunOemInstallerAsync(entry, path, norwegian).ConfigureAwait(true)
                            ? OemPackageOutcome.InstallerRan
                            : OemPackageOutcome.InstallerFailed;
                    }
                    else
                    {
                        var mark = MessageBox.Show(this,
                            norwegian
                                ? "Installerer du denne OEM-pakken nå (f.eks. fra Nedlastinger), eller er den allerede installert / krever omstart?\n\nJA: skjul fra Drivers/Firmware og merk som Vellykket.\nNEI: behold tilgjengelig (omstart med ventende reboot kan fortsatt promotere den)."
                                : "Are you installing this OEM package now (e.g. from Downloads), or is it already installed / reboot-required?\n\nYES: hide from Drivers/Firmware and mark as Successful.\nNO: keep available (a pending reboot can still promote it).",
                            norwegian ? $"OEM-driver ({brand})" : $"OEM driver ({brand})",
                            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                        if (mark == MessageBoxResult.Yes)
                            ConfirmOemInstalled(entry, path, norwegian);
                    }
                }
                else
                {
                    MessageBox.Show(this,
                        norwegian
                            ? $"Lagret:\n{path}\n\nOppføringen forblir tilgjengelig til du bekrefter installasjon."
                            : $"Saved:\n{path}\n\nThe entry stays available until you confirm install.",
                        norwegian ? $"OEM-driver ({brand})" : $"OEM driver ({brand})",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        else if (!quiet)
        {
            MessageBox.Show(this,
                norwegian ? $"Lagret:\n{path}" : $"Saved:\n{path}",
                norwegian ? "OEM TXT/readme" : "OEM TXT/readme",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        if (!quiet)
            RefreshHealthSummary();
        return outcome;
    }

    /// <returns>
    /// False when the installer did not start or exited with a failure code, so the entry stays
    /// available. True otherwise, including a wait cut short by a restart (hold kept).
    /// </returns>
    /// <param name="quiet">True from Install Selected: failures go to its one summary, not a dialog each.</param>
    private async Task<bool> TryRunOemInstallerAsync(UpdateEntry entry, string path, bool norwegian, bool quiet = false)
    {
        bool holdRecorded = false;
        List<UpdateEntry> removedPending = new();
        UpdateEntry? startedRow = null;
        try
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = true };
            using var process = Process.Start(start);
            if (process is null)
            {
                ActivityLog.TryWrite("OemInstall", "NotStarted", new { Title = entry.DisplayTitle, Path = path });
                if (!quiet)
                    MessageBox.Show(this,
                        norwegian ? "Kunne ikke starte installasjonsprogrammet." : "Could not start the installer.",
                        norwegian ? "OEM-installasjon" : "OEM install",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // Persist hold + History as soon as the installer is running. Chipset/Graphics
            // often reboot before WaitForExit returns — without this, OemInstall never logged
            // and the next Search re-offered (+ re-downloaded) the same Id+Version.
            string startedNote = norwegian
                ? $"OEM-installasjon startet. Fil: {path}"
                : $"OEM installer started. File: {path}";
            OemHandledStore.RememberRebootHold(entry, path);
            holdRecorded = true;
            removedPending = RemoveOemPending(entry);
            startedRow = HistoryArchive.RecordOemOutcome(entry, "Vellykket", startedNote);
            RememberOemHistory(startedRow);
            ActivityLog.TryWrite("OemInstall", "RebootHold", new
            {
                Title = entry.DisplayTitle,
                Path = path,
                OemPackageId = entry.OemPackageId,
                Version = entry.UnifiedVersion
            });

            StatusText.Text = norwegian
                ? "Venter på OEM-installasjonsprogram…"
                : "Waiting for OEM installer…";
            await process.WaitForExitAsync().ConfigureAwait(true);
            // 0 = success; 3010 = reboot required; 1641 = reboot initiated.
            if (process.ExitCode is 0 or 3010 or 1641)
            {
                // History already written at start — upgrade hold to Installed only.
                OemHandledStore.RememberInstalled(entry, path);
                ActivityLog.TryWrite("OemInstall", "Installert", new
                {
                    Title = entry.DisplayTitle,
                    ExitCode = process.ExitCode,
                    Path = path,
                    OemPackageId = entry.OemPackageId
                });
                if (process.ExitCode is 3010 or 1641 || RebootPendingReader.IsRebootPending())
                    RefreshRebootNeededBanner();
                return true;
            }
            else
            {
                string note = norwegian
                    ? $"OEM-installasjon avsluttet med kode {process.ExitCode}. Fil: {path}"
                    : $"OEM installer exited with code {process.ExitCode}. File: {path}";
                // The row written at start was provisional: this attempt ends as one failed row.
                var failedRow = HistoryArchive.RecordOemOutcome(entry, "Mislykket", note, replaces: startedRow);
                ForgetOemHistory(startedRow);
                RememberOemHistory(failedRow);
                OemHandledStore.Forget(entry);
                holdRecorded = false;
                // Early RemoveOemPending cleared the Drivers row — put it back for retry.
                RestoreOemPending(removedPending);
                removedPending = new();
                ActivityLog.TryWrite("OemInstall", "Failed", new
                {
                    Title = entry.DisplayTitle,
                    ExitCode = process.ExitCode,
                    Path = path,
                    OemPackageId = entry.OemPackageId
                });
                return false;
            }
        }
        catch (Exception ex)
        {
            // Keep RebootHold if the process was interrupted (common when reboot starts).
            if (holdRecorded && RebootPendingReader.IsRebootPending())
            {
                RefreshRebootNeededBanner();
                ActivityLog.TryWrite("OemInstall", "RebootHold", new
                {
                    Title = entry.DisplayTitle,
                    Path = path,
                    OemPackageId = entry.OemPackageId,
                    Note = "Installer wait interrupted with reboot pending — hold kept."
                });
            }
            else if (holdRecorded)
            {
                // Still keep the hold: Search must not resurrect solely because wait failed.
                ActivityLog.TryWrite("OemInstall", "RebootHold", new
                {
                    Title = entry.DisplayTitle,
                    Path = path,
                    OemPackageId = entry.OemPackageId,
                    Note = "Installer wait interrupted — hold kept until confirmed or forgotten.",
                    Exception = ex.Message
                });
            }
            else
            {
                ActivityLog.TryWrite("OemInstall", "NotStarted", new
                {
                    Title = entry.DisplayTitle,
                    Path = path,
                    Exception = ex.Message
                });
            }
            if (!quiet)
                MessageBox.Show(this, ex.Message,
                    norwegian ? "OEM-installasjon" : "OEM install",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return holdRecorded;
        }
    }

    private void ConfirmOemInstalled(
        UpdateEntry entry,
        string? path,
        bool norwegian,
        string? note = null,
        int? exitCode = null)
    {
        note ??= norwegian
            ? $"OEM-installasjon bekreftet. Fil: {path}"
            : $"OEM install confirmed. File: {path}";
        RememberOemHistory(HistoryArchive.RecordOemOutcome(entry, "Vellykket", note));
        OemHandledStore.RememberInstalled(entry, path);
        RemoveOemPending(entry);
        ActivityLog.TryWrite("OemInstall", "Installert", new
        {
            Title = entry.DisplayTitle,
            ExitCode = exitCode,
            Path = path,
            OemPackageId = entry.OemPackageId
        });
    }

    private void RememberOemHistory(UpdateEntry? historyRow)
    {
        if (historyRow is null) return;
        historyRow.DateDisplay = _schedule.FormatDateValue(historyRow.Date);
        _historySnapshot.Insert(0, historyRow);
        if (_showingHistory)
        {
            Entries.Insert(0, historyRow);
            _entriesView.Refresh();
        }
        RefreshHealthSummary();
    }

    /// <summary>
    /// Drop a row from the loaded History. Matched by event, not by reference: History may
    /// have been reloaded from the archive while the installer ran.
    /// </summary>
    private void ForgetOemHistory(UpdateEntry? historyRow)
    {
        if (historyRow is null) return;
        HistoryArchive.RemoveEvent(_historySnapshot, historyRow);
        if (!_showingHistory) return;
        string key = HistoryArchive.EventKey(historyRow);
        foreach (var row in Entries
                     .Where(row => string.Equals(HistoryArchive.EventKey(row), key, StringComparison.OrdinalIgnoreCase))
                     .ToList())
            Entries.Remove(row);
    }

    /// <returns>Removed pending OEM rows (so a failed installer can restore them).</returns>
    private List<UpdateEntry> RemoveOemPending(UpdateEntry entry)
    {
        var remove = _pendingSnapshot
            .Where(e => IsOemDownloadable(e) && MatchesOemIdentity(e, entry))
            .ToList();
        if (remove.Count == 0) return remove;
        foreach (var item in remove) _pendingSnapshot.Remove(item);
        RefreshPendingGrid();
        return remove;
    }

    private void RestoreOemPending(IReadOnlyList<UpdateEntry> rows)
    {
        if (rows.Count == 0) return;
        foreach (var item in rows)
        {
            if (_pendingSnapshot.Any(e => IsOemDownloadable(e) && MatchesOemIdentity(e, item)))
                continue;
            _pendingSnapshot.Insert(0, item);
        }
        RefreshPendingGrid();
    }

    private void RefreshPendingGrid()
    {
        if (_showingHistory) return;
        Entries.Clear();
        foreach (var item in _pendingSnapshot) Entries.Add(item);
        if (_activeTab == "all")
        {
            foreach (var app in _appEntries)
                Entries.Add(ToAllListAppEntry(app));
        }
        _entriesView.Refresh();
        UpdateSelectionControls();
    }

    private static bool MatchesOemIdentity(UpdateEntry pending, UpdateEntry reference)
    {
        if (!string.IsNullOrWhiteSpace(reference.OemPackageId) &&
            string.Equals(pending.OemPackageId, reference.OemPackageId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pending.UnifiedVersion, reference.UnifiedVersion, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(reference.ExternalUrl) &&
            string.Equals(pending.ExternalUrl, reference.ExternalUrl, StringComparison.OrdinalIgnoreCase))
            return true;
        // History dismiss may have cleared URLs; match title+version for OEM rows.
        return string.Equals((pending.Title ?? "").Trim(), (reference.Title ?? "").Trim(), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(pending.UnifiedVersion, reference.UnifiedVersion, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenOemReadme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UpdateEntry entry })
            OpenOemReadmeEntry(entry);
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
        if (_activeTab == "all")
            SetTab("all");
        else
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

    /// <summary>
    /// The tray's Windows Update queues are a reason for Påminnelse and the tray does not scan
    /// again while one is waiting, so drop from them what this window's scan no longer offers.
    /// </summary>
    private static void ReconcileWindowsPromptsWithScan(IReadOnlyList<UpdateEntry> offered) =>
        CategoryPromptState.KeepOnlyOfferedInWindowsQueues(offered.Select(entry => entry.UpdateId ?? ""));

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
                if (code == 0)
                    logs.Add($"{app.Name}: Command completed");
                else
                {
                    var (state, tip, _) = WingetReader.DescribeUpgradeFailure(code, app.DisplayName, _schedule.Language == "no");
                    logs.Add($"{app.Name}: {state}" + (string.IsNullOrWhiteSpace(tip) ? "" : "\n  " + tip));
                }
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
        string sharedTime = (SharedInstallTime?.Text ?? CriticalPreferredTime.Text).Trim();
        if (!TimeSpan.TryParseExact(sharedTime, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            MessageBox.Show(this, "Install time must be HH:mm, for example 18:00.", "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        CriticalPreferredTime.Text = sharedTime;
        GeneralPreferredTime.Text = sharedTime;
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
        if (!int.TryParse(ApplicationsCheckHours.Text.Trim(), out int appsHours) || appsHours < 1)
        {
            MessageBox.Show(this, "Applications check interval must be at least 1 hour.", "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(DriversCheckHours.Text.Trim(), out int driversHours) || driversHours < 24)
        {
            MessageBox.Show(this, "Drivers check interval must be at least 24 hours.", "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(WindowsMonitorHours.Text.Trim(), out int monitorHours) || monitorHours < 1)
        {
            MessageBox.Show(this, "Windows updates interval must be at least 1 hour.", "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _policy.WindowsAutomation.Enabled = WindowsAutomationEnabled.IsChecked == true;
        _policy.WindowsAutomation.Defender.Enabled = DefenderAutoEnabled.IsChecked == true;
        _policy.WindowsAutomation.Defender.IntervalMinutes = defenderMinutes;
        _policy.WindowsAutomation.Defender.Mode = "autoInstall";
        _policy.WindowsAutomation.Critical.Enabled = CriticalAutoEnabled.IsChecked == true;
        _policy.WindowsAutomation.Critical.IntervalMinutes = criticalMinutes;
        _policy.WindowsAutomation.Critical.PreferredInstallTime = sharedTime;
        _policy.WindowsAutomation.Critical.Mode = "downloadAndPrompt";
        _policy.WindowsAutomation.General.Enabled = GeneralAutoEnabled.IsChecked == true;
        _policy.WindowsAutomation.General.IntervalMinutes = generalMinutes;
        _policy.WindowsAutomation.General.PreferredInstallTime = sharedTime;
        _policy.WindowsAutomation.General.Mode = "downloadAndPrompt";
        _policy.WindowsAutomation.AutoReboot.Enabled = AutoRebootEnabled.IsChecked == true;
        _policy.WindowsAutomation.AutoReboot.Mode = AutoRebootOutsideOption.IsChecked == true ? "outsideActiveHours" : "confirm";
        _policy.WindowsAutomation.AutoReboot.ActiveHoursStart = activeStart;
        _policy.WindowsAutomation.AutoReboot.ActiveHoursEnd = activeEnd;
        _policy.WindowsAutomation.AutoReboot.CountdownSeconds = countdown;
        _policy.WindowsAutomation.NeverAutoReboot = !_policy.WindowsAutomation.AutoReboot.Enabled;

        int daysMask = ReadInstallDaysMaskFromUi();
        if (daysMask == 0) daysMask = MaintenanceSetup.AllDaysMask;
        MaintenanceSetup.ApplyToPolicy(_policy, daysMask, sharedTime);
        _policy.SetupCompleted = true;

        _policy.AutomaticDrivers = AutoDrivers.IsChecked == true;
        _policy.AutomaticApplications = AutoApplications.IsChecked == true;
        _policy.ApplicationsIntervalMinutes = appsHours * 60;
        _policy.DriversIntervalMinutes = driversHours * 60;
        _policy.WindowsMonitorIntervalMinutes = monitorHours * 60;
        _policy.AutomaticApplicationIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _appEntries)
        {
            if (app.AutoUpdate) _policy.AutomaticApplicationIds.Add(app.Id);
            else _policy.AutomaticApplicationIds.Remove(app.Id);
        }
        _policy.Save();
        MaintenanceSetup.SyncReminderFromPolicy(_policy);
        _schedule = ReminderSchedule.Load();
        RemindersEnabled.IsChecked = _schedule.Enabled;
        RefreshMaintenanceSummary();
        RefreshScheduleStatus();
        RefreshAutomationStatusTexts();
        MessageBox.Show(this,
            "Windows automation policy saved.\n\n" +
            "Defender (K1) can auto-install via the background agent.\n" +
            "Critical updates and general Windows update download in the background and ask before install.\n" +
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
        string installTime = string.IsNullOrWhiteSpace(_policy.InstallTime)
            ? (string.IsNullOrWhiteSpace(wa.Critical.PreferredInstallTime) ? AutomationSchedule.DefaultPreferredTime : wa.Critical.PreferredInstallTime)
            : _policy.InstallTime;
        CriticalPreferredTime.Text = installTime;
        if (SharedInstallTime is not null) SharedInstallTime.Text = installTime;
        GeneralAutoEnabled.IsChecked = wa.General.Enabled;
        GeneralIntervalMinutes.Text = wa.General.IntervalMinutes.ToString();
        GeneralPreferredTime.Text = installTime;
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
        if (ApplicationsCheckHours is not null)
            ApplicationsCheckHours.Text = Math.Max(1, (_policy.ApplicationsIntervalMinutes + 59) / 60).ToString();
        if (DriversCheckHours is not null)
            DriversCheckHours.Text = Math.Max(24, (_policy.DriversIntervalMinutes + 59) / 60).ToString();
        if (WindowsMonitorHours is not null)
            WindowsMonitorHours.Text = Math.Max(1, (_policy.WindowsMonitorIntervalMinutes + 59) / 60).ToString();
        BuildInstallDayChips();
        RefreshMaintenanceSummary();
        RefreshAutomationStatusTexts();
    }

    private void MaybeShowSetupWizard()
    {
        // Prefer App startup wizard (before MainWindow). Reload policy if setup finished there.
        try
        {
            _policy = UpdatePolicy.Load();
            _policy.Normalize();
            if (_policy.SetupCompleted)
            {
                LoadAutomationPolicyIntoUi();
                _schedule = ReminderSchedule.Load();
                RemindersEnabled.IsChecked = _schedule.Enabled;
                TimeChoice.Text = _schedule.Time;
                DayChoice.SelectedIndex = _schedule.Weekday == 7 ? 0 : Math.Clamp(_schedule.Weekday, 0, 6) + 1;
                RefreshScheduleStatus();
                return;
            }
            var wizard = new SetupWizardWindow(this) { Owner = this };
            wizard.ShowDialog();
            _policy = UpdatePolicy.Load();
            LoadAutomationPolicyIntoUi();
            _schedule = ReminderSchedule.Load();
            RemindersEnabled.IsChecked = _schedule.Enabled;
            TimeChoice.Text = _schedule.Time;
            DayChoice.SelectedIndex = _schedule.Weekday == 7 ? 0 : Math.Clamp(_schedule.Weekday, 0, 6) + 1;
            RefreshScheduleStatus();
            if (MaintenanceExpander is not null)
                MaintenanceExpander.IsExpanded = true;
            try { InstallationManager.EnsureBackgroundAgentRunning(); }
            catch (Exception ex) { AgentLog.Write("Post-setup agent ensure failed: " + ex.Message); }
        }
        catch (Exception ex)
        {
            AgentLog.Write("Setup wizard failed: " + ex);
        }
    }

    private void ChangeSetup_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new SetupWizardWindow(this) { Owner = this };
        if (wizard.ShowDialog() == true)
        {
            _policy = UpdatePolicy.Load();
            LoadAutomationPolicyIntoUi();
            _schedule = ReminderSchedule.Load();
            RemindersEnabled.IsChecked = _schedule.Enabled;
            RefreshScheduleStatus();
        }
    }

    private readonly System.Windows.Controls.CheckBox[] _installDayBoxes = new System.Windows.Controls.CheckBox[7];

    private void BuildInstallDayChips()
    {
        if (InstallDayChips is null) return;
        InstallDayChips.Children.Clear();
        bool no = _schedule.Language == "no";
        var labels = no ? MaintenanceSetup.DayLabelsNo : MaintenanceSetup.DayLabelsEn;
        int mask = MaintenanceSetup.ClampMask(_policy.InstallDaysMask);
        if (mask == 0) mask = MaintenanceSetup.AllDaysMask;
        for (int i = 0; i < 7; i++)
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Content = labels[i],
                IsChecked = MaintenanceSetup.HasDay(mask, i),
                Margin = new Thickness(0, 0, 10, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _installDayBoxes[i] = box;
            InstallDayChips.Children.Add(box);
        }
    }

    private int ReadInstallDaysMaskFromUi()
    {
        int mask = 0;
        for (int i = 0; i < 7; i++)
            if (_installDayBoxes[i]?.IsChecked == true) mask |= 1 << i;
        return mask;
    }

    private void RefreshMaintenanceSummary()
    {
        if (MaintenanceSummaryText is null) return;
        _policy.Normalize();
        bool no = _schedule.Language == "no";
        MaintenanceSummaryText.Text = MaintenanceSetup.SummaryText(_policy, no);
    }

    private void RefreshAutomationStatusTexts()
    {
        var status = WindowsAutomationStatus.Load();
        bool no = _schedule.Language == "no";
        string Format(CategoryAutomationStatus item, string pendingNote)
        {
            if (item.LastRun is null) return (no ? "Status: " : "Status: ") + pendingNote;
            string next = item.NextDue?.ToString("dd.MM HH:mm") ?? "—";
            return no
                ? $"Status: {item.LastResult} · sist {item.LastRun:dd.MM HH:mm} · neste {next}"
                : $"Status: {item.LastResult} · last {item.LastRun:dd.MM HH:mm} · next {next}";
        }
        string waiting = no ? "venter på agent" : "waiting for agent";
        DefenderAutomationStatus.Text = Format(status.Defender, waiting);
        CriticalAutomationStatus.Text = Format(status.Critical, waiting);
        GeneralAutomationStatus.Text = Format(status.General, waiting);
        AutomationStatusText.Text = no
            ? $"Windows-automatisering: Defender={status.Defender.LastResult}" +
              $"  |  Kritisk={status.Critical.LastResult}" +
              $"  |  Generell={status.General.LastResult}" +
              $"  |  Drivere={status.Drivers.LastResult}" +
              $"  |  Programmer={status.Applications.LastResult}"
            : $"Windows automation: Defender={status.Defender.LastResult}" +
              $"  |  Critical={status.Critical.LastResult}" +
              $"  |  General={status.General.LastResult}" +
              $"  |  Drivers={status.Drivers.LastResult}" +
              $"  |  Apps={status.Applications.LastResult}";
        AutomationStatusText.ToolTip =
            $"Defender: {status.Defender.Detail}\nCritical: {status.Critical.Detail}\nGeneral: {status.General.Detail}\nDrivers: {status.Drivers.Detail}\nApplications: {status.Applications.Detail}";
        FollowDefenderAutomation(status.Defender);
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
            ReconcileWindowsPromptsWithScan(pending);
            RefreshHealthSummary();
            StatusText.Text = "Status check complete. No installation performed.";
            SyncDefenderHeaderFromHistory();
            HidePendingAlreadyInHistory(history);
            await RefreshDefenderAsync(quiet: true);
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
        // OEM catalog rows are listed in Drivers but are not WUA "available" installs.
        int drivers = _pendingSnapshot.Count(x => x.Category == "Driver / firmware" && !string.IsNullOrEmpty(x.UpdateId));
        int oem = _pendingSnapshot.Count(x => x.Category == "Driver / firmware" && x.OpensExternalUrl);
        if (_schedule.Language == "no")
            return oem > 0
                ? $"Windows: {windows} tilgjengelig  |  Drivere: {drivers} tilgjengelig (+{oem} OEM)  |  Programmer: {_appEntries.Count} tilgjengelig"
                : $"Windows: {windows} tilgjengelig  |  Drivere: {drivers} tilgjengelig  |  Programmer: {_appEntries.Count} tilgjengelig";
        return oem > 0
            ? $"Windows: {windows} available  |  Drivers: {drivers} available (+{oem} OEM)  |  Applications: {_appEntries.Count} available"
            : $"Windows: {windows} available  |  Drivers: {drivers} available  |  Applications: {_appEntries.Count} available";
    }

    private void RefreshHealthSummary()
    {
        if (HealthSummary is null) return;
        if (_historySnapshot.Count == 0 && _pendingSnapshot.Count == 0 && _installOutcomeSuccess is null && _installOutcomeFailures is null)
        {
            HealthSummary.Inlines.Clear();
            HealthSummary.Text = CategorySummary() + (_schedule.Language == "no"
                ? "\nHistorikk er ikke sjekket ennå."
                : "\nHistory not checked yet.");
            return;
        }
        var failures = _historySnapshot.Where(x => x.IsHistoryFailed).ToList();
        var success = _historySnapshot.Where(x => x.IsHistorySuccessful).ToList();
        var fixedEntries = _historySnapshot.Where(x => x.IsHistoryFixed).ToList();
        var pendingKbs = _pendingSnapshot.SelectMany(x => SplitKbs(x.KB)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolvedEntries = new List<UpdateEntry>();
        int laterSuccess = 0, stillPending = 0, unresolved = 0;
        foreach (var failure in failures)
        {
            var keys = SplitKbs(failure.KB).ToArray();
            if (keys.Length == 0) { unresolved++; continue; }
            var failureDate = ReminderSchedule.TryParseDate(failure.Date) ?? DateTime.MinValue;
            if (keys.Any(k => pendingKbs.Contains(k))) { stillPending++; continue; }
            if (success.Any(x => (ReminderSchedule.TryParseDate(x.Date) ?? DateTime.MinValue) > failureDate && SplitKbs(x.KB).Intersect(keys, StringComparer.OrdinalIgnoreCase).Any()))
            {
                laterSuccess++;
                resolvedEntries.Add(failure);
            }
            else unresolved++;
        }

        bool norwegian = _schedule.Language == "no";
        int failureCount = _installOutcomeFailures ?? failures.Count;
        int successCount = _installOutcomeSuccess ?? success.Count;
        bool blinkFailures = _installOutcomeFailures is > 0;
        bool blinkSuccess = _installOutcomeSuccess is > 0;
        string failuresLabel = norwegian ? "Feil" : "Failures";
        string successLabel = norwegian ? "Vellykket" : "Success";
        string fixedLabel = norwegian ? "Fikset" : "Fixed";
        string resolvedLabel = norwegian ? "Løst KB" : "Resolved KB";
        string unresolvedLabel = norwegian ? "Uløst" : "Unresolved";

        HealthSummary.Inlines.Clear();
        HealthSummary.Inlines.Add(new Run(CategorySummary() + "\n"));
        HealthSummary.Inlines.Add(new Run(norwegian
            ? $"Historikk: {_historySnapshot.Count}  |  "
            : $"History: {_historySnapshot.Count}  |  "));
        AppendOutcomeLink(failuresLabel, failureCount, "failures", blinkFailures, highlightRed: true);
        HealthSummary.Inlines.Add(new Run("  |  "));
        AppendOutcomeLink(successLabel, successCount, "success", blinkSuccess, highlightRed: false);
        HealthSummary.Inlines.Add(new Run("  |  "));
        AppendOutcomeLink(fixedLabel, fixedEntries.Count, "fixed", blink: false, highlightRed: false);
        HealthSummary.Inlines.Add(new Run("  |  "));
        AppendOutcomeLink(resolvedLabel, laterSuccess, "resolved", blink: false, highlightRed: false);
        HealthSummary.Inlines.Add(new Run("  |  "));
        if (unresolved > 0)
            AppendOutcomeLink(unresolvedLabel, unresolved, "unresolved", blink: false, highlightRed: true);
        else
            HealthSummary.Inlines.Add(new Run($"{unresolvedLabel}: {unresolved}"));
        HealthSummary.ToolTip = norwegian
            ? "Klikk Feil / Vellykket / Fikset / Løst KB / Uløst for oversikt. Uløst: Nullstill telleren eller Skjul inkompatible OEM-pakker. Fikset = senere suksess for samme KB, eller at den feilede oppdateringen ikke lenger tilbys."
            : "Click Failures / Success / Fixed / Resolved KB / Unresolved for a list. Unresolved: clear the counter or hide incompatible OEM packages. Fixed means a later success for the same KB, or the failed update is no longer offered.";
    }

    private void AppendOutcomeLink(string label, int count, string kind, bool blink, bool highlightRed)
    {
        var brush = blink && _installOutcomeBlinkOn
            ? (highlightRed
                ? new SolidColorBrush(Color.FromRgb(198, 40, 40))
                : new SolidColorBrush(Color.FromRgb(46, 125, 50)))
            : new SolidColorBrush(Color.FromRgb(82, 97, 122));
        var run = new Run($"{label}: {count}")
        {
            FontWeight = blink && _installOutcomeBlinkOn ? FontWeights.Bold : FontWeights.SemiBold
        };
        var link = new Hyperlink(run)
        {
            Tag = kind,
            Foreground = brush,
            TextDecorations = null,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = _schedule.Language == "no"
                ? "Klikk for oversikt"
                : "Click for overview"
        };
        link.Click += OutcomeLink_Click;
        HealthSummary.Inlines.Add(link);
    }

    private void OutcomeLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { Tag: string kind }) return;
        ShowOutcomeOverview(kind);
    }

    private void ShowOutcomeOverview(string kind)
    {
        bool norwegian = _schedule.Language == "no";
        var failures = MergeOutcomeEntries(
            _historySnapshot.Where(x => x.IsHistoryFailed),
            _installOutcomeFailureEntries);
        var success = MergeOutcomeEntries(
            _historySnapshot.Where(x => x.IsHistorySuccessful),
            _installOutcomeSuccessEntries);
        var fixedEntries = _historySnapshot.Where(x => x.IsHistoryFixed).ToList();
        var pendingKbs = _pendingSnapshot.SelectMany(x => SplitKbs(x.KB)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolvedEntries = new List<UpdateEntry>();
        var unresolvedEntries = new List<UpdateEntry>();
        foreach (var failure in failures.Where(x => !string.Equals(x.StatusNote, "install-outcome", StringComparison.OrdinalIgnoreCase)))
        {
            var keys = SplitKbs(failure.KB).ToArray();
            if (keys.Length == 0)
            {
                unresolvedEntries.Add(failure);
                continue;
            }
            var failureDate = ReminderSchedule.TryParseDate(failure.Date) ?? DateTime.MinValue;
            if (keys.Any(k => pendingKbs.Contains(k))) continue;
            if (success.Any(x => (ReminderSchedule.TryParseDate(x.Date) ?? DateTime.MinValue) > failureDate &&
                                 SplitKbs(x.KB).Intersect(keys, StringComparer.OrdinalIgnoreCase).Any()))
                resolvedEntries.Add(failure);
            else
                unresolvedEntries.Add(failure);
        }

        string heading;
        string subtitle;
        IReadOnlyList<UpdateEntry> entries;
        bool showFixIt = false;
        bool showUnresolvedActions = false;
        switch (kind)
        {
            case "failures":
                heading = norwegian ? "Feil" : "Failures";
                subtitle = norwegian
                    ? "Mislykkede historikkoppføringer og feil fra siste Installation Manager-kjøring. Bruk Fix it / hjelp når feilkode finnes."
                    : "Failed history entries and failures from the latest Installation Manager run. Use Fix it / help when an error code is available.";
                entries = failures;
                showFixIt = true;
                break;
            case "success":
                heading = norwegian ? "Vellykket" : "Success";
                subtitle = norwegian
                    ? "Vellykkede historikkoppføringer og fullførte rader fra siste installasjonsrunde."
                    : "Successful history entries and completed rows from the latest install run.";
                entries = success;
                break;
            case "fixed":
                heading = norwegian ? "Fikset" : "Fixed";
                subtitle = norwegian
                    ? "Historikk merket Fixed etter senere suksess eller fullført fiks."
                    : "History rows marked Fixed after a later success or a completed fix.";
                entries = fixedEntries;
                break;
            case "resolved":
                heading = norwegian ? "Løst KB" : "Resolved KB";
                subtitle = norwegian
                    ? "Tidligere feil der samme KB senere ble installert med suksess."
                    : "Earlier failures where the same KB later installed successfully.";
                entries = resolvedEntries;
                break;
            case "unresolved":
                heading = norwegian ? "Uløst" : "Unresolved";
                subtitle = norwegian
                    ? "Feil uten senere suksess for samme KB (inkl. OEM uten KB). Nullstill Unresolved fjerner telleren (merker Fixed). Skjul fra fremtidige oppdateringer skjuler pakken i Historikk og OEM-katalog (f.eks. Seagate-firmware uten komponent)."
                    : "Failures without a later success for the same KB (including OEM with no KB). Clear Unresolved resets the counter (marks Fixed). Hide from future updates removes the package from History and the OEM catalog (e.g. Seagate firmware when the PC lacks the component).";
                entries = unresolvedEntries;
                showUnresolvedActions = true;
                break;
            default:
                return;
        }

        var dlg = new OutcomeOverviewWindow(kind, heading, subtitle, entries, showFixIt, norwegian, showUnresolvedActions) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (dlg.FixRequestedEntry is UpdateEntry fixEntry)
            OpenOutcomeFix(fixEntry, norwegian);
        else if (dlg.ClearUnresolvedEntry is UpdateEntry clearEntry)
            ClearUnresolvedEntry(clearEntry, norwegian);
        else if (dlg.HideFromFutureEntry is UpdateEntry hideEntry)
            HideUnresolvedFromFuture(hideEntry, norwegian);
    }

    private void ClearUnresolvedEntry(UpdateEntry entry, bool norwegian)
    {
        string note = norwegian
            ? "Merket Fixed fordi Uløst ble nullstilt av brukeren. Opprinnelig feilkode er uendret."
            : "Marked Fixed because Unresolved was cleared by the user. The original error code is unchanged.";
        HistoryArchive.MarkClearedUnresolved(entry, note);
        entry.Status = "Fixed";
        entry.StatusNote = note;
        entry.ExternalUrl = "";
        entry.ReadmeUrl = "";
        foreach (var row in _historySnapshot.Where(r =>
                     string.Equals(HistoryArchive.DisplayIdentityKey(r), HistoryArchive.DisplayIdentityKey(entry), StringComparison.OrdinalIgnoreCase) &&
                     r.IsHistoryFailed))
        {
            row.Status = "Fixed";
            row.StatusNote = note;
            row.ExternalUrl = "";
            row.ReadmeUrl = "";
        }
        if (_showingHistory) SetTab("history");
        RefreshHealthSummary();
        StatusText.Text = norwegian ? "Uløst nullstilt." : "Unresolved cleared.";
    }

    private void HideUnresolvedFromFuture(UpdateEntry entry, bool norwegian)
    {
        HistoryArchive.HideFromDisplay(entry);
        OemHandledStore.RememberDismissed(entry);
        RemoveOemPending(entry);
        _historySnapshot.RemoveAll(row => OutcomeIdentity.SameUpdate(row, entry));
        if (_showingHistory) SetTab("history");
        else if (_activeTab is "drivers" or "all" or "software") SetTab(_activeTab);
        RefreshHealthSummary();
        StatusText.Text = norwegian
            ? "Skjult fra fremtidige oppdateringer."
            : "Hidden from future updates.";
        ActivityLog.TryWrite("OemHandled", "Dismissed", new
        {
            Title = entry.DisplayTitle,
            OemPackageId = entry.OemPackageId,
            Source = entry.Source
        });
    }

    private static List<UpdateEntry> MergeOutcomeEntries(IEnumerable<UpdateEntry> history, IReadOnlyList<UpdateEntry> recent) =>
        OutcomeIdentity.MergeRecentWithHistory(history, recent, ReminderSchedule.TryParseDate);

    private void OpenOutcomeFix(UpdateEntry entry, bool norwegian)
    {
        if (string.Equals(entry.Source, "WinGet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Category, "Application", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.ErrorCode, "0x8A15008E", StringComparison.OrdinalIgnoreCase))
        {
            var item = new InstallationQueueItem(entry.Source, entry.Title)
            {
                State = entry.ErrorCode == "0x8A15008E"
                    ? (norwegian ? "Feilet (teknologi-mismatch)" : "Failed (tech mismatch)")
                    : (string.IsNullOrWhiteSpace(entry.ErrorCode) ? entry.Status : "Failed " + entry.ErrorCode),
                Tip = string.IsNullOrWhiteSpace(entry.StatusNote) || entry.StatusNote == "install-outcome"
                    ? null
                    : entry.StatusNote
            };
            if (string.IsNullOrWhiteSpace(item.Tip) && entry.ErrorCode == "0x8A15008E")
            {
                var (_, tip, _) = WingetReader.DescribeUpgradeFailure(
                    WingetReader.ExitInstallTechnologyMismatch, entry.Title, norwegian);
                item.Tip = tip;
            }
            new InstallStatusHelpWindow(item, norwegian) { Owner = this }.ShowDialog();
            return;
        }
        ShowErrorFix(entry);
    }

    internal static UpdateEntry ToInstallOutcomeEntry(InstallationQueueItem item)
    {
        string blob = item.State + " " + (item.Tip ?? "");
        var hr = System.Text.RegularExpressions.Regex.Match(blob, @"0x[0-9A-Fa-f]{8}");
        string error = hr.Success ? hr.Value : "";
        if (string.IsNullOrWhiteSpace(error) &&
            (item.State.Contains("tech mismatch", StringComparison.OrdinalIgnoreCase)
             || item.State.Contains("teknologi-mismatch", StringComparison.OrdinalIgnoreCase)
             || (item.Tip?.Contains("0x8A15008E", StringComparison.OrdinalIgnoreCase) ?? false)
             || (item.Tip?.Contains("-1978335090", StringComparison.OrdinalIgnoreCase) ?? false)))
            error = "0x8A15008E";

        string category = item.Source.Contains("WinGet", StringComparison.OrdinalIgnoreCase) ? "Application"
            : item.Source.Contains("Driver", StringComparison.OrdinalIgnoreCase) ? "Driver / firmware"
            : "Windows Update";
        bool failed = item.State.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
            || item.State.StartsWith("Feilet", StringComparison.OrdinalIgnoreCase);
        bool completed = InstallationManagerWindow.IsCompletedState(item.State);

        return new UpdateEntry
        {
            Title = item.Name,
            // KB and UpdateId as Windows Update history records them, so this row and the
            // history row for the same install are recognised as one.
            KB = string.Join(", ", SplitKbs(item.Name)),
            Category = category,
            Source = item.Source,
            Status = failed ? "Failed" : completed ? "Successful" : item.State,
            ErrorCode = error,
            StatusNote = string.IsNullOrWhiteSpace(item.Tip) ? "install-outcome" : item.Tip,
            Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            UpdateId = item.UpdateId,
            SafeDefault = false
        };
    }

    private void SetInstallOutcome(int completed, int failed, IReadOnlyList<InstallationQueueItem>? queue = null)
    {
        _installOutcomeSuccess = completed;
        _installOutcomeFailures = failed;
        _installOutcomeFailureEntries.Clear();
        _installOutcomeSuccessEntries.Clear();
        if (queue is not null)
        {
            foreach (var item in queue)
            {
                if (item.State.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                    || item.State.StartsWith("Feilet", StringComparison.OrdinalIgnoreCase))
                    _installOutcomeFailureEntries.Add(ToInstallOutcomeEntry(item));
                else if (InstallationManagerWindow.IsCompletedState(item.State))
                    _installOutcomeSuccessEntries.Add(ToInstallOutcomeEntry(item));
            }
            // Persist Completed/Failed into Historikk's archive (WinGet never appears in WUA history).
            try { HistoryArchive.RecordInstallationManagerQueue(queue); }
            catch (Exception ex) { ActivityLog.TryWrite("InstallationManager", "HistoryRecordFailed", ex.Message); }
        }
        _installOutcomeBlinkOn = true;
        if (!_installOutcomeBlinkTimer.IsEnabled)
            _installOutcomeBlinkTimer.Start();
        RefreshHealthSummary();
    }

    private void ClearInstallOutcomeHighlight()
    {
        if (_installOutcomeSuccess is null && _installOutcomeFailures is null
            && _installOutcomeFailureEntries.Count == 0 && _installOutcomeSuccessEntries.Count == 0)
            return;
        _installOutcomeBlinkTimer.Stop();
        _installOutcomeSuccess = null;
        _installOutcomeFailures = null;
        _installOutcomeFailureEntries.Clear();
        _installOutcomeSuccessEntries.Clear();
        _installOutcomeBlinkOn = false;
        RefreshHealthSummary();
    }

    private void InstallOutcomeBlinkTimer_Tick(object? sender, EventArgs e)
    {
        if (_installOutcomeSuccess is null && _installOutcomeFailures is null)
        {
            _installOutcomeBlinkTimer.Stop();
            return;
        }
        _installOutcomeBlinkOn = !_installOutcomeBlinkOn;
        RefreshHealthSummary();
    }

    private void ReplacePendingSnapshot(IEnumerable<UpdateEntry> items)
    {
        _pendingSnapshot.Clear();
        foreach (var item in items) _pendingSnapshot.Add(item);
        _pendingSnapshotAt = _pendingSnapshot.Count == 0 ? null : DateTime.Now;
        RefreshFeatureUpdateButton();
    }

    /// <summary>
    /// After install: keep the last search results, minus rows that completed successfully.
    /// Failed / cancelled / not-verified rows stay listed.
    /// </summary>
    private void PruneSuccessfullyInstalledFromPending(
        IReadOnlyList<UpdateEntry> software,
        IReadOnlyList<UpdateEntry> drivers,
        IReadOnlyList<InstallationQueueItem> queue)
    {
        if (queue.Count == 0 || (software.Count == 0 && drivers.Count == 0)) return;
        var removeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removeTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < software.Count; i++)
        {
            if (i >= queue.Count || !InstallationManagerWindow.IsCompletedState(queue[i].State)) continue;
            if (!string.IsNullOrWhiteSpace(software[i].UpdateId))
                removeIds.Add(software[i].UpdateId);
            else if (!string.IsNullOrWhiteSpace(software[i].Title))
                removeTitles.Add(software[i].Title.Trim());
        }
        for (int i = 0; i < drivers.Count; i++)
        {
            int qi = software.Count + i;
            if (qi >= queue.Count || !InstallationManagerWindow.IsCompletedState(queue[qi].State)) continue;
            if (!string.IsNullOrWhiteSpace(drivers[i].UpdateId))
                removeIds.Add(drivers[i].UpdateId);
            else if (!string.IsNullOrWhiteSpace(drivers[i].Title))
                removeTitles.Add(drivers[i].Title.Trim());
        }
        if (removeIds.Count == 0 && removeTitles.Count == 0) return;
        CategoryPromptState.RemoveInstalledFromWindowsQueues(removeIds);

        for (int i = _pendingSnapshot.Count - 1; i >= 0; i--)
        {
            var entry = _pendingSnapshot[i];
            bool byId = !string.IsNullOrWhiteSpace(entry.UpdateId) && removeIds.Contains(entry.UpdateId);
            bool byTitle = !string.IsNullOrWhiteSpace(entry.Title) && removeTitles.Contains(entry.Title.Trim());
            if (byId || byTitle)
            {
                entry.IsSelected = false;
                _pendingSnapshot.RemoveAt(i);
            }
        }
        RefreshFeatureUpdateButton();
    }

    private void RefreshFeatureUpdateButton()
    {
        if (OpenWindowsUpdateButton is null) return;
        bool show = (_activeTab is "software" or "all") && _pendingSnapshot.Any(entry => entry.Source == FeatureUpdateOfferReader.Source);
        OpenWindowsUpdateButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void IncludeOptionalUpdates_Changed(object sender, RoutedEventArgs e) => RefreshOptionalParityHint();

    private void RefreshOptionalParityHint()
    {
        bool optionalOn = IncludeOptionalUpdates?.IsChecked == true;
        if (OptionalParityInfo is not null)
            OptionalParityInfo.Visibility = optionalOn ? Visibility.Visible : Visibility.Collapsed;
        if (OptionalOpenWindowsUpdateButton is not null)
            OptionalOpenWindowsUpdateButton.Visibility = optionalOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenWindowsUpdate_Click(object sender, RoutedEventArgs e)
    {
        // Opens Settings only (ms-settings:windowsupdate). No scan/download/install.
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
        if (DateTime.Now - _lastReconcileAttempt < TimeSpan.FromSeconds(3)) return;
        if (_healthCheckRunning) return;
        if (Interlocked.CompareExchange(ref _reconcileRunning, 1, 0) != 0) return;
        _lastReconcileAttempt = DateTime.Now;
        try
        {
            var history = await WindowsUpdateReader.RunStaAsync(WindowsUpdateReader.HistoryForDisplay);
            if (_busy || _pendingSnapshotAt is null) return;
            _historySnapshot = history;
            // Push Historikk Defender success into the header immediately (before Mp poll).
            bool defenderMoved = SyncDefenderHeaderFromHistory();
            HidePendingAlreadyInHistory(history);
            // This runs every few seconds: start PowerShell only when Historikk shows a new Defender install.
            if (defenderMoved) await RefreshDefenderAsync(quiet: true);
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
        var installedDefender = TryParseDefenderSignatureVersion(_lastDefender?.SignatureVersion);
        var hide = _pendingSnapshot
            .Where(item => PendingHistoryReconcile.ShouldHide(item, history, taken, installedDefender))
            .ToList();
        if (hide.Count == 0)
        {
            PruneMonitorDefenderOffers(installedDefender);
            return 0;
        }
        foreach (var item in hide) _pendingSnapshot.Remove(item);
        // Entries can hold an older copy of the pending rows. Rebuild it from the snapshot
        // unless History is on screen, so the Windows Updates grid drops the installed item.
        if (!_showingHistory)
        {
            Entries.Clear();
            foreach (var entry in _pendingSnapshot) Entries.Add(entry);
            _entriesView.Refresh();
        }
        PruneMonitorDefenderOffers(installedDefender);
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

    private void PruneMonitorDefenderOffers(Version? installedDefender)
    {
        if (installedDefender is null) return;
        MonitorStatus.PruneInstalledDefender(installedDefender);
        RefreshMonitorPanel();
    }

    private static Version? TryParseDefenderSignatureVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Count(c => c == '.') == 2) text += ".0";
        return Version.TryParse(text, out var version) ? version : null;
    }

    private static IEnumerable<string> SplitKbs(string? value) =>
        System.Text.RegularExpressions.Regex.Matches(value ?? "", @"KB\d{5,9}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Value.ToUpperInvariant()).Distinct();
    private DefenderState? _lastDefender;
    /// <summary>Newest Defender automation run already handled. Null until the first status read sets the baseline.</summary>
    private DateTime? _lastSeenDefenderAutomationRun;
    private bool _defenderAutomationBaselineSet;
    /// <summary>
    /// When the tray last installed signatures while this window was open. Defender stamps the
    /// same install a few seconds earlier, so without this the re-read after the run could move
    /// «Sjekket» back across a minute.
    /// </summary>
    private DateTime? _defenderAutomationInstallAt;
    private DateTime _lastActivationDefenderRead = DateTime.MinValue;
    /// <summary>Counts Defender reads so that only the newest one in flight writes the header.</summary>
    private int _defenderReadSeq;

    private async void DefenderRefresh_Click(object sender, RoutedEventArgs e) { await RefreshDefenderAsync(); await RefreshFirewallAsync(); }

    /// <summary>
    /// Apply Historikk's newest Defender success to the header without waiting for Get-MpComputerStatus.
    /// </summary>
    /// <returns>True when «Sjekket» moved forward.</returns>
    private bool SyncDefenderHeaderFromHistory()
    {
        if (_historySnapshot.Count == 0) return false;
        var fromHistory = PendingHistoryReconcile.LatestDefenderSuccessAt(_historySnapshot);
        if (fromHistory is null) return false;
        return ApplyDefenderShownTime(fromHistory.Value, "Historikk");
    }

    /// <summary>
    /// When the tray's Defender automation has installed new signatures, move «Sjekket» to that
    /// run and quietly re-read Defender. The first call only records the run that was already
    /// there when the window opened: the startup read covers it.
    /// </summary>
    private void FollowDefenderAutomation(CategoryAutomationStatus defender)
    {
        if (!_defenderAutomationBaselineSet)
        {
            _defenderAutomationBaselineSet = true;
            _lastSeenDefenderAutomationRun = defender.LastRun;
            return;
        }
        if (defender.LastRun is not DateTime run) return;
        if (_lastSeenDefenderAutomationRun is DateTime seen && seen >= run) return;
        _lastSeenDefenderAutomationRun = run;
        if (!DefenderHeaderSync.InstalledNewSignatures(defender.LastResult)) return;
        _defenderAutomationInstallAt = run;
        ApplyDefenderShownTime(run, "automation");
        _ = RefreshDefenderAsync(quiet: true);
    }

    /// <summary>
    /// Move «Sjekket» forward on the status already shown. Before the first Defender read there
    /// is no level or label to show next to a time, so nothing is drawn; that read picks up
    /// Historikk itself.
    /// </summary>
    private bool ApplyDefenderShownTime(DateTime when, string source)
    {
        if (_lastDefender is null) return false;
        var updated = DefenderHeaderSync.WithNewerSignatureTime(_lastDefender, when, source);
        if (ReferenceEquals(updated, _lastDefender)) return false;
        _lastDefender = updated;
        ApplyDefenderUi(updated);
        return true;
    }

    /// <summary>
    /// Read Defender status into the header. <paramref name="quiet"/> keeps the current label and
    /// «Sjekket» on screen while reading and when the read fails.
    /// </summary>
    private async System.Threading.Tasks.Task RefreshDefenderAsync(bool quiet = false)
    {
        bool norwegian = _schedule.Language == "no";
        int read = ++_defenderReadSeq;
        if (!quiet)
        {
            DefenderRefreshButton.IsEnabled = false;
            DefenderLabel.Text = norwegian ? "Sjekker…" : "Checking…";
        }
        try
        {
            var status = await UpdateExpress.Services.DefenderStatusReader.ReadAsync();
            // A read started later is still running and will show a newer status than this one.
            if (read != _defenderReadSeq) return;
            if (DefenderHeaderSync.KeepShownStatus(_lastDefender, status, quiet))
            {
                // Redraw: a refresh the user started may have left «Sjekker…» on screen.
                ApplyDefenderUi(_lastDefender!);
                return;
            }
            status = EnrichDefenderWithHistory(status, _historySnapshot);
            if (_defenderAutomationInstallAt is DateTime trayInstall)
                status = DefenderHeaderSync.WithNewerSignatureTime(status, trayInstall, "automation");
            _lastDefender = status;
            ApplyDefenderUi(_lastDefender);
            var installed = TryParseDefenderSignatureVersion(_lastDefender.SignatureVersion);
            if (installed is not null)
            {
                // Drop already-installed Defender lines from Results «Vis» / monitor-status.
                HidePendingCoveredByDefenderSignature(installed);
                PruneMonitorDefenderOffers(installed);
            }
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                _lastDefender = null;
                DefenderLabel.Text = norwegian ? "Ukjent" : "Unknown";
                DefenderChecked.Text = ex.Message;
            }
            else
                System.Diagnostics.Debug.WriteLine("Quiet Defender refresh failed: " + ex.Message);
        }
        finally
        {
            if (!quiet) DefenderRefreshButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Prefer the newer of Mp signature last-updated and Historikk's latest successful KB2267602.
    /// </summary>
    private static DefenderState EnrichDefenderWithHistory(DefenderState status, IReadOnlyList<UpdateEntry> history)
    {
        var fromHistory = PendingHistoryReconcile.LatestDefenderSuccessAt(history);
        return fromHistory is null
            ? status
            : DefenderHeaderSync.WithNewerSignatureTime(status, fromHistory.Value, "Historikk");
    }

    private void HidePendingCoveredByDefenderSignature(Version installed)
    {
        if (_pendingSnapshot.Count == 0) return;
        var hide = _pendingSnapshot
            .Where(item => PendingHistoryReconcile.DefenderInstalledCovers(item, installed))
            .ToList();
        if (hide.Count == 0) return;
        foreach (var item in hide) _pendingSnapshot.Remove(item);
        if (!_showingHistory)
        {
            Entries.Clear();
            foreach (var entry in _pendingSnapshot) Entries.Add(entry);
            _entriesView.Refresh();
        }
        RefreshHealthSummary();
        UpdateSelectionControls();
    }

    private void ApplyDefenderUi(DefenderState status)
    {
        bool norwegian = _schedule.Language == "no";
        DefenderShield.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(status.Level switch
        {
            "green" => "#168A57", "orange" => "#EA941B", "red" => "#D13C36", _ => "#9099A6"
        }));
        DefenderLabel.Text = LocalizeDefenderLabel(status.Label, norwegian);
        // «Sjekket» is the latest successful definition install / signature update — not the UI poll time.
        var shown = status.SignatureLastUpdated ?? status.CheckedAt;
        DefenderChecked.Text = norwegian
            ? $"Sjekket {shown:HH:mm}"
            : $"Checked {shown:HH:mm}";
        System.Windows.Controls.ToolTipService.SetToolTip(DefenderShield, status.Details);
        System.Windows.Controls.ToolTipService.SetToolTip(DefenderLabel, status.Details);
        System.Windows.Controls.ToolTipService.SetToolTip(DefenderChecked, status.Details);
    }

    private static string LocalizeDefenderLabel(string label, bool norwegian)
    {
        if (!norwegian) return label;
        return label switch
        {
            "Updated" => "Oppdatert",
            "Unknown" => "Ukjent",
            "Checking…" => "Sjekker…",
            "Protection disabled" => "Beskyttelse deaktivert",
            "Critical: 7+ days" => "Kritisk: 7+ dager",
            "Warning: 2–6 days" => "Advarsel: 2–6 dager",
            "Defender passive" => "Defender passiv",
            _ => label
        };
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
    /// <summary>True while a ReminderDialog Present callback is queued or modal — prevents stacking.</summary>
    private bool _reminderPresentQueued;
    private (bool IsTest, DateTime Occurrence, bool Norwegian, bool ShowMissed, string ReasonKey, IReadOnlyList<string>? PendingLines)? _pendingReminder;
    private readonly DispatcherTimer _monitorUiTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _uptimeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _bootedAt = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
    private static readonly TimeSpan LongUptimeThreshold = TimeSpan.FromDays(7);
    private readonly DispatcherTimer _rebootBlinkTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    private DispatcherTimer? _confirmedRebootTimer;
    private bool _rebootPrompt;
    private bool _windowsRebootPending;
    /// <summary>One session prompt for DownloadPending without installed-version evidence.</summary>
    private bool _oemDownloadPromotePromptShown;
    private readonly DispatcherTimer _installOutcomeBlinkTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private int? _installOutcomeSuccess;
    private int? _installOutcomeFailures;
    private bool _installOutcomeBlinkOn;
    /// <summary>Last Installation Manager failed rows (WinGet/WUA/driver) shown under Failures until the next scan clears the highlight.</summary>
    private readonly List<UpdateEntry> _installOutcomeFailureEntries = new();
    private readonly List<UpdateEntry> _installOutcomeSuccessEntries = new();

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
        bool becamePending = needed && !_windowsRebootPending;
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
            // Evidence-gated promote only — never Installed solely because reboot is pending.
            if (becamePending)
                PromoteOemDownloads(norwegian, previousBootOnly: false, allowConfirmPrompt: true);
        }
        else
        {
            _rebootBlinkTimer.Stop();
            _rebootPrompt = false;
            RebootNeededBanner.Opacity = 1;
            RebootNeededBanner.Text = RebootBannerText();
        }
        // After reboot (flags often cleared): evidence-first; one session confirm if still ambiguous.
        PromoteOemDownloads(norwegian, previousBootOnly: true, allowConfirmPrompt: true);
        RefreshLongUptimeHint();
        RefreshPatchConflictBanner();
    }

    /// <summary>
    /// Promote DownloadPending → Installed/Vellykket only with installed-version evidence,
    /// or (once per session, when allowed) explicit user confirmation.
    /// Never auto-mark solely because a download predates boot / reboot-pending (Codex P1).
    /// </summary>
    private void PromoteOemDownloads(bool norwegian, bool previousBootOnly, bool allowConfirmPrompt)
    {
        var pending = previousBootOnly
            ? OemHandledStore.ListDownloadPendingFromPreviousBoot()
            : OemHandledStore.ListDownloadPending(TimeSpan.FromDays(2));
        if (pending.Count == 0) return;

        var evidenced = new List<UpdateEntry>();
        var needsConfirm = new List<UpdateEntry>();
        foreach (var entry in pending)
        {
            if (OemHandledStore.IsHandled(entry)) continue;
            if (OemInstalledVersionFilter.IsOfferSatisfiedByInstalled(entry))
                evidenced.Add(entry);
            else
                needsConfirm.Add(entry);
        }

        string evidenceNote = norwegian
            ? "OEM-pakke merket: installert versjon ≥ tilbud (etter nedlasting)."
            : "OEM package marked: installed version ≥ offer (after download).";
        foreach (var entry in evidenced)
            ConfirmOemInstalled(entry, null, norwegian, evidenceNote);

        if (!allowConfirmPrompt || needsConfirm.Count == 0 || _oemDownloadPromotePromptShown)
            return;
        if (!IsLoaded || !IsVisible) return;

        _oemDownloadPromotePromptShown = true;
        var titles = string.Join("\n", needsConfirm.Select(e => "• " + e.DisplayTitle).Take(8));
        if (needsConfirm.Count > 8)
            titles += norwegian ? $"\n… (+{needsConfirm.Count - 8} til)" : $"\n… (+{needsConfirm.Count - 8} more)";
        var answer = MessageBox.Show(this,
            norwegian
                ? $"Disse OEM-pakkene er lastet ned, men installasjon er ikke bekreftet via Update Express:\n\n{titles}\n\nJA: merk som Vellykket / skjult (du har installert dem).\nNEI: behold dem tilgjengelige i Drivers."
                : $"These OEM packages were downloaded, but install was not confirmed through Update Express:\n\n{titles}\n\nYES: mark as Successful / hidden (you installed them).\nNO: keep them available in Drivers.",
            norwegian ? "OEM-nedlastinger" : "OEM downloads",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        string confirmNote = norwegian
            ? "OEM-pakke merket etter brukerbekreftelse (nedlasting uten WaitForExit)."
            : "OEM package marked after user confirmation (download without WaitForExit).";
        foreach (var entry in needsConfirm)
        {
            if (OemHandledStore.IsHandled(entry)) continue;
            ConfirmOemInstalled(entry, null, norwegian, confirmNote);
        }
    }

    private void RefreshPatchConflictBanner()
    {
        if (PatchConflictBanner is null) return;
        try
        {
            var conflict = PatchConflictGuard.Evaluate(_policy);
            bool norwegian = _schedule.Language == "no";
            if (!conflict.ShowBanner)
            {
                PatchConflictBanner.Visibility = Visibility.Collapsed;
                return;
            }

            PatchConflictBanner.Visibility = Visibility.Visible;
            PatchConflictBanner.Text = norwegian
                ? "⚠  Konflikt: N-able PME kjører samtidig med Update Express-automatisering. Klikk for PME-kontroll."
                : "⚠  Conflict: N-able PME is active while Update Express automation is on. Click for PME control.";
            PatchConflictBanner.ToolTip = conflict.Summary;
        }
        catch
        {
            PatchConflictBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void PatchConflictBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        PmeControl_Click(sender, e);

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
        bool no = _schedule.Language == "no";
        var installedDefender = TryParseDefenderSignatureVersion(_lastDefender?.SignatureVersion);
        var status = installedDefender is null
            ? MonitorStatus.Read()
            : MonitorStatus.PruneInstalledDefender(installedDefender) ?? MonitorStatus.Read();
        if (MonitorDetailsText is null || MonitorUpdatesText is null) { RefreshResultsHeading(); return; }
        if (status is null)
        {
            MonitorDetailsText.Text = no
                ? "Installer/start bakgrunnsagenten for å se status"
                : "Install/start the background agent to see status";
            MonitorUpdatesText.Text = no ? "Oppdateringer: —" : "Updates: —";
            if (AutomationStatusText is not null)
                AutomationStatusText.Text = no ? "Windows-automatisering: —" : "Windows automation: —";
            RefreshResultsHeading();
            return;
        }
        var updates = installedDefender is null
            ? status.Updates
            : PendingHistoryReconcile.FilterInstalledDefenderLines(status.Updates, installedDefender);
        MonitorDetailsText.Text = no
            ? $"Tilgjengelig: {Math.Min(status.Pending, updates.Length)}  |  Nye: {status.New}"
            : $"Available: {Math.Min(status.Pending, updates.Length)}  |  New: {status.New}";
        MonitorUpdatesText.Text = updates.Length == 0
            ? (no ? "Oppdateringer: ingen registrert" : "Updates: none recorded")
            : (no ? "Oppdateringer: " : "Updates: ") + string.Join("; ", updates.Take(3));
        MonitorUpdatesText.ToolTip = updates.Length == 0
            ? (no ? "Ingen registrerte oppdateringer" : "No recorded updates")
            : string.Join(Environment.NewLine, updates);
        RefreshResultsHeading();
    }

    /// <summary>
    /// Results expander title: “Søke resultater” / “Search results” plus three spaces and
    /// “Sist sjekket: HH:mm - neste sjekk: HH:mm - Sist omstart: dd.MM.yyyy HH:mm
    /// Siste planlagte vedlikehold: vellykket/feil” (language-aware).
    /// </summary>
    private void RefreshResultsHeading()
    {
        if (ListHeading is null) return;
        bool no = _schedule.Language == "no";
        string title = no ? "Søke resultater" : "Search results";
        var status = MonitorStatus.Read();
        DateTime? lastAt = _lastSuccessfulScan ?? status?.LastCompleted;
        string last = lastAt is DateTime t ? t.ToString("HH:mm") : "—";
        string next = status is not null && status.NextScheduled != default
            ? status.NextScheduled.ToString("HH:mm")
            : "—";
        string restart = _bootedAt.ToString("dd.MM.yyyy HH:mm");
        string maintenance = LastScheduledMaintenanceResult(no);
        string text = no
            ? $"{title}   Sist sjekket: {last} - neste sjekk: {next} - Sist omstart: {restart} Siste planlagte vedlikehold: {maintenance}"
            : $"{title}   Last checked: {last} - next check: {next} - Last restart: {restart} Last scheduled maintenance: {maintenance}";
        ListHeading.Text = text;
        // The heading trims with an ellipsis in a narrow window; keep the full line reachable.
        ListHeading.ToolTip = text;
    }

    /// <summary>
    /// Outcome of the last finished unattended «install at HH:mm» run (unattended-until-clear.json):
    /// failed when any item failed or the run was stopped, “—” when no run has finished yet.
    /// Stays on the previous run's outcome while the next run is armed or running.
    /// </summary>
    private static string LastScheduledMaintenanceResult(bool norwegian) =>
        UnattendedUntilClear.LastFinishedFailed(UnattendedUntilClear.Load()) switch
        {
            null => "—",
            true => norwegian ? "feil" : "failed",
            false => norwegian ? "vellykket" : "successful"
        };

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
        bool no = _schedule.Language == "no";
        InternetStatusText.Text = (no ? "Internett: " : "Internet: ") + internet.Message;
        UpdateServerStatusText.Text = (no ? "Microsoft TCP: " : "Microsoft TCP: ")
            + (update.Message == "TCP OK" ? "OK" : update.Message);
        var wua = MonitorStatus.Read();
        bool wuaOk = wua?.LastCompleted is not null && wua.State != "failed";
        WuaLamp.Fill = wuaOk ? Brushes.ForestGreen : wua?.State == "failed" ? Brushes.OrangeRed : Brushes.Gray;
        WuaStatusText.Text = wuaOk
            ? "WUA: OK"
            : wua?.State == "failed"
                ? (no ? "WUA: Feil" : "WUA: Error")
                : (no ? "WUA: Ikke bekreftet" : "WUA: Not confirmed");
        WuaStatusText.ToolTip = wuaOk
            ? (no
                ? $"Siste vellykkede WUA-sjekk: {wua!.LastCompleted:dd.MM HH:mm}"
                : $"Last successful WUA check: {wua!.LastCompleted:dd.MM HH:mm}")
            : wua?.Error ?? (no
                ? "Venter på vellykket bakgrunnsagentsjekk."
                : "Waiting for successful background-agent check.");
        ProxyStatusText.Text = proxy;
        InternetStatusText.ToolTip = internet.Detail;
        UpdateServerStatusText.ToolTip = update.Detail;
        ProxyStatusText.ToolTip = no
            ? "Proxy-konfigurasjon fra .NET. DNS/TCP/ICMP-prober bruker direkte tilkoblinger og tester ikke proxyen. Windows Update kan bruke WinHTTP eller andre proxy-innstillinger."
            : "Proxy configuration from .NET. DNS/TCP/ICMP probes use direct connections and do not test the proxy. Windows Update may use WinHTTP or other proxy settings.";
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
        RefreshManufacturerSupportButton();
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
        button.ToolTip = norwegian ? "Kopiert!" : "Copied!";
        CopiedToast.Show(button, norwegian);
        var iconTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
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
        // First paint: Results starts collapsed — force Auto row (Collapsed event does not run at init).
        ApplyResultsRowLayout();
        HeaderGrid.LayoutUpdated += (_, _) => UpdateThisPcLabelVisibility();
        AppIcon.Apply(this);
        // Tray skips Påminnelse while this Main UI session is open.
        MaintenanceReminderPromptGate.RegisterMainUiSession();
        Closed += (_, _) =>
        {
            MaintenanceReminderPromptGate.UnregisterMainUiSession();
            if (_fixItWindow is null) return;
            _fixItWindow.SuppressDetach();
            try { _fixItWindow.Close(); } catch { /* ignore */ }
            _fixItWindow = null;
        };
        ShowMachineIdentity(MachineIdentityReader.ReadFast());
        RefreshOsSupportStatus();
        _appEntries = AppEntries; DataContext = this;
        _entriesView = CollectionViewSource.GetDefaultView(Entries);
        _entriesView.Filter = VisibleInTab;
        UpdatesGrid.ItemsSource = _entriesView;
        SetTab("all");
        _schedule = ReminderSchedule.Load();
        DayChoice.SelectedIndex = _schedule.Weekday == 7 ? 0 : Math.Clamp(_schedule.Weekday, 0, 6) + 1;
        TimeChoice.Text = _schedule.Time;
        ApplyLanguage(); // sets AppSubtitle / Title from language + version
        RefreshOptionalParityHint();
        RemindersEnabled.IsChecked = _schedule.Enabled;
        LoadAutomationPolicyIntoUi();
        BuildInstallDayChips();
        RefreshMaintenanceSummary();
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
            bool defenderMoved = SyncDefenderHeaderFromHistory();
            _ = ReconcilePendingFromHistoryAsync();
            // Activated also fires before Loaded (which reads Defender itself) and when one of our
            // own dialogs closes; do not start PowerShell each time.
            if (IsLoaded && (defenderMoved || DateTime.Now - _lastActivationDefenderRead >= TimeSpan.FromSeconds(10)))
            {
                _lastActivationDefenderRead = DateTime.Now;
                _ = RefreshDefenderAsync(quiet: true);
            }
        };
        WingetAvailableCache.Changed += () =>
        {
            try { Dispatcher.BeginInvoke(new Action(TrySyncAppsFromWingetCache)); }
            catch { /* window may be closing */ }
        };
        TrySyncAppsFromWingetCache();
        _rebootBlinkTimer.Tick += RebootBlinkTimer_Tick;
        _installOutcomeBlinkTimer.Tick += InstallOutcomeBlinkTimer_Tick;
        _installSelectionBlinkTimer.Tick += InstallSelectionBlinkTimer_Tick;
        RefreshMonitorPanel();
        RefreshAutomationStatusTexts();
        RefreshRebootNeededBanner();
        Loaded += async (_, _) =>
        {
            try
            {
                if (FleetPolicy.TryApplyOnStartup())
                {
                    _policy = UpdatePolicy.Load();
                    LoadAutomationPolicyIntoUi();
                }
            }
            catch (Exception ex) { AgentLog.Write("Fleet policy startup apply failed: " + ex.Message); }
            RmmStatusExport.WriteBestEffort("mainwindow-loaded", force: true);
            CheckReminder();
            _ = LoadMachineIdentityAsync();
            _ = LoadWindows11EligibilityAsync();
            // Read Historikk alongside Defender so «Sjekket» can use the latest Defender success
            // without waiting for a later history load. Whichever finishes last applies both.
            var defenderTask = RefreshDefenderAsync();
            try
            {
                var history = await WindowsUpdateReader.RunStaAsync(WindowsUpdateReader.HistoryForDisplay);
                // A check or install started meanwhile has read a newer history; keep that one.
                if (_historySnapshot.Count == 0) _historySnapshot = history;
                SyncDefenderHeaderFromHistory();
            }
            catch (Exception ex)
            {
                AgentLog.Write("Startup Historikk read for the Defender header failed: " + ex.Message);
            }
            await defenderTask;
            await RefreshFirewallAsync();
            RefreshRebootNeededBanner();
            // Quiet self-update check when the main window opens (prompt only if newer).
            AppUpdatePrompt.StartCheck(
                automatic: true,
                onUpdateInstallRequested: StartAppSelfUpdateInstall,
                balloon: null);
            MaybeShowSetupWizard();
            if (UpdateUntilCurrent.Load().Active)
            {
                // Defense-in-depth: App elevates before MainWindow, but never resume
                // the cycle from a medium-IL process (would Access Denied and clear Active).
                if (!ElevationHelper.IsElevated)
                {
                    try
                    {
                        ElevationHelper.RestartElevated("--installed");
                        Close();
                    }
                    catch (Exception ex)
                    {
                        AgentLog.WriteInstall("Update-until-current resume needs elevation: " + ex.Message);
                    }
                    return;
                }
                await RunUpdateUntilCurrentAsync();
            }
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
    private string _osSupportReferenceUrl = WindowsSupportReader.ClientReferenceUrl;
    private void RefreshOsSupportStatus()
    {
        var info = WindowsSupportReader.Read();
        _osSupportReferenceUrl = info.ReferenceUrl;
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
        var dlg = new OsSupportInfoDialog(_osSupportDetails, _schedule.Language == "no", _osSupportReferenceUrl) { Owner = this };
        dlg.ShowDialog();
    }

    private const string Windows11AssistantLabel = "Windows 11 Assistant";
    private Windows11Eligibility.Decision? _windows11Decision;
    private bool _windows11AssistantBusy;

    /// <summary>
    /// The button stays hidden until the check, which runs off the UI thread, says this is a
    /// Windows 10 PC that meets every Windows 11 requirement. A check that fails or cannot
    /// decide leaves it hidden.
    /// </summary>
    private async Task LoadWindows11EligibilityAsync()
    {
        try
        {
            var decision = await Task.Run(Windows11Eligibility.Evaluate);
            _windows11Decision = decision;
            AgentLog.Write("Windows 11 Assistant button " + (decision.Eligible ? "shown" : "hidden") + ". " + decision.Summary);
        }
        catch (Exception ex)
        {
            _windows11Decision = null;
            AgentLog.Write("Windows 11 eligibility check failed; the Windows 11 Assistant button stays hidden: " + ex.Message);
        }
        RefreshWindows11AssistantButton();
    }

    private void RefreshWindows11AssistantButton()
    {
        if (Windows11AssistantButton is null || Windows11AssistantTestMark is null) return;
        bool norwegian = _schedule.Language == "no";
        bool test = Windows11Assistant.TestSwitchOn;
        bool show = test || _windows11Decision?.Eligible == true;
        Windows11AssistantButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Windows11AssistantTestMark.Visibility = show && test ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        // Same label in both languages. Tag keeps Translate() from replacing a busy label.
        Windows11AssistantButton.Tag = Windows11AssistantLabel;
        if (!_windows11AssistantBusy) Windows11AssistantButton.Content = Windows11AssistantLabel;
        Windows11AssistantButton.ToolTip = test
            ? (norwegian
                ? "TEST: knappen vises på grunn av testbryteren. Et klikk laster ned og kontrollerer Microsofts installasjonsassistent, men starter den ikke."
                : "TEST: the button is shown because of the test switch. A click downloads and checks Microsoft's Installation Assistant but does not start it.")
            : (norwegian
                ? "Denne PC-en oppfyller kravene til Windows 11. Last ned og start Microsofts installasjonsassistent for Windows 11."
                : "This PC meets the Windows 11 requirements. Download and start Microsoft's Windows 11 Installation Assistant.");
        Windows11AssistantTestMark.ToolTip = norwegian
            ? $"Testbryteren {Windows11Assistant.TestSwitchVariable}=1 er satt. Knappen sier ikke noe om denne PC-en kan oppgraderes."
            : $"The test switch {Windows11Assistant.TestSwitchVariable}=1 is set. The button says nothing about whether this PC can be upgraded.";
    }

    private async void Windows11Assistant_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_windows11AssistantBusy) return;
        bool norwegian = _schedule.Language == "no";
        bool test = Windows11Assistant.TestSwitchOn;
        // The switch may have been the only reason the button was visible.
        if (!test && _windows11Decision?.Eligible != true) { RefreshWindows11AssistantButton(); return; }

        string title = test ? Windows11AssistantLabel + " (TEST)" : Windows11AssistantLabel;
        string question = test
            ? (norwegian
                ? "TESTMODUS\n\nKnappen vises på grunn av testbryteren, ikke fordi denne PC-en er funnet klar for Windows 11.\n\nUpdate Express laster ned Microsofts installasjonsassistent for Windows 11 fra Microsoft og kontrollerer at filen er signert av Microsoft. Assistenten blir IKKE startet, og filen slettes etterpå.\n\nFortsette?"
                : "TEST MODE\n\nThe button is shown because of the test switch, not because this PC was found ready for Windows 11.\n\nUpdate Express downloads Microsoft's Windows 11 Installation Assistant from Microsoft and checks that the file is signed by Microsoft. The Assistant is NOT started, and the file is deleted afterwards.\n\nContinue?")
            : (norwegian
                ? "Update Express laster ned Microsofts installasjonsassistent for Windows 11 fra Microsoft, kontrollerer at filen er signert av Microsoft, og starter den.\n\nAssistenten er Microsofts eget program. Den kontrollerer PC-en på nytt og spør før den oppgraderer. Selve oppgraderingen tar lang tid og starter PC-en på nytt, så lagre arbeidet ditt og ta en sikkerhetskopi først.\n\nLaste ned og starte assistenten nå?"
                : "Update Express downloads Microsoft's Windows 11 Installation Assistant from Microsoft, checks that the file is signed by Microsoft, and starts it.\n\nThe Assistant is Microsoft's own program. It checks this PC again and asks before it upgrades. The upgrade itself takes a long time and restarts the PC, so save your work and take a backup first.\n\nDownload and start the Assistant now?");
        if (AppDialog.Show(this, question, title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _windows11AssistantBusy = true;
        Windows11AssistantButton.IsEnabled = false;
        string mode = test ? "Test" : "Manual";
        ActivityLog.TryWrite("Windows11Assistant", "Started", new { Windows11Assistant.DownloadUrl, Test = test }, mode);
        try
        {
            void Progress(string step) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_windows11AssistantBusy) return;
                Windows11AssistantButton.Content = step switch
                {
                    "download" => norwegian ? "Laster ned…" : "Downloading…",
                    "verify" => norwegian ? "Kontrollerer…" : "Checking…",
                    _ => norwegian ? "Starter…" : "Starting…"
                };
            }));
            var outcome = await Task.Run(() => Windows11Assistant.RunAsync(start: !test, Progress));
            ActivityLog.TryWrite("Windows11Assistant", outcome.Started ? "AssistantStarted" : "VerifiedNotStarted",
                new { outcome.DownloadedFrom, outcome.Bytes, outcome.Signer, outcome.FilePath }, mode);
            if (!outcome.Started)
            {
                AppDialog.Show(this,
                    norwegian
                        ? $"TESTMODUS: assistenten ble ikke startet.\n\nLastet ned fra: {outcome.DownloadedFrom}\nStørrelse: {outcome.Bytes:N0} byte\nSignert av: {outcome.Signer}\n\nFilen er slettet."
                        : $"TEST MODE: the Assistant was not started.\n\nDownloaded from: {outcome.DownloadedFrom}\nSize: {outcome.Bytes:N0} bytes\nSigned by: {outcome.Signer}\n\nThe file has been deleted.",
                    title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ActivityLog.TryWrite("Windows11Assistant", "Failed", new { ex.Message }, mode);
            var open = AppDialog.Show(this,
                (norwegian
                    ? "Installasjonsassistenten ble ikke startet.\n\n"
                    : "The Installation Assistant was not started.\n\n")
                + ex.Message
                + (norwegian
                    ? "\n\nVil du åpne Microsofts nedlastingsside for Windows 11 i nettleseren i stedet?"
                    : "\n\nOpen Microsoft's Windows 11 download page in your browser instead?"),
                title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (open == MessageBoxResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Windows11Assistant.DownloadPageUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception openEx)
                {
                    AppDialog.Show(this, openEx.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        finally
        {
            _windows11AssistantBusy = false;
            Windows11AssistantButton.IsEnabled = true;
            RefreshWindows11AssistantButton();
        }
    }

    private void ProductOverviewInfo_Click(object sender, RoutedEventArgs e)
    {
        bool norwegian = _schedule.Language == "no";
        var dlg = new ProductOverviewWindow(norwegian) { Owner = this };
        dlg.ShowDialog();
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
        ["Windows Update Status – Health Check"] = "Windows Update-status – helsesjekk",
        ["Fix your Windows update issues here...."] = "Fiks Windows Update-problemene dine her....",
        ["Repair stuck updates, open Windows Settings, or review installation help."] =
            "Reparer fastlåste oppdateringer, åpne Windows-innstillinger, eller se installasjonshjelp.",
        ["Open FIX IT!"] = "Åpne FIX IT!",
        ["Check for Updates"] = "Se etter oppdateringer",
        ["Check for updates"] = "Se etter oppdateringer",
        ["Open Windows Update"] = "Åpne Windows Update", ["Open Windows Update to install"] = "Åpne Windows Update for å installere",
        ["Some optional and preview updates may be available directly in Windows Update without appearing in UpdateExpress."] =
            "Enkelte valgfrie oppdateringer og forhåndsvisningsoppdateringer (Preview) kan være tilgjengelige direkte i Windows Update uten å vises i UpdateExpress.",
        ["Opens Windows Update in Settings (ms-settings:windowsupdate). Does not scan, download, or install."] =
            "Åpner Windows Update i Innstillinger (ms-settings:windowsupdate). Starter ikke skanning, nedlasting eller installasjon.",
        ["Manufacturer"] = "Produsent", ["Model"] = "Modell/nr", ["Product no."] = "Produktnr.", ["Serial no."] = "Serienr.", ["Device name"] = "Enhetsnavn", ["Uptime"] = "Oppetid",
        ["Copy"] = "Kopier",
        ["Install Selected Updates"] = "Installer valgte oppdateringer", ["Installation History"] = "Installasjonshistorikk",
        ["Windows Update fix"] = "Windows Update-fiks", ["WUA deep check"] = "WUA dybdesjekk",
        ["WUA status report"] = "WUA statusrapport", ["Error report"] = "Feilrapport",
        ["Reboot needed"] = "Omstart nødvendig",
        ["Export CSV"] = "Eksporter CSV", ["Diagnostics"] = "Diagnostikk", ["Check Status"] = "Kontroller status",
        ["Refresh"] = "Oppdater", ["This PC"] = "Denne PCen",
        ["Private"] = "Privat", ["Public"] = "Offentlig", ["Domain"] = "Domene",
        ["Search results"] = "Søke resultater", ["Check for Updates & Results"] = "Søke resultater", ["Application Updates (WinGet)"] = "Programoppdateringer (WinGet)",
        ["All"] = "Alle",
        ["Windows Updates"] = "Windows-oppdateringer", ["Drivers / Firmware"] = "Drivere / fastvare",
        ["Applications"] = "Programmer", ["History"] = "Historikk", ["Select All"] = "Velg alle",
        ["Select"] = "Velg", ["Category"] = "Kategori", ["Update"] = "Oppdatering",
        ["Status"] = "Status", ["Date / Time"] = "Dato / tid", ["Error Code"] = "Feilkode",
        ["Priority"] = "Prioritet", ["Version"] = "Versjon", ["Application"] = "Program", ["Package ID"] = "Pakke-ID", ["Installed"] = "Installert",
        ["Available"] = "Tilgjengelig", ["Check Application Updates (WinGet)"] = "Søk etter programoppdateringer (WinGet)",
        ["Export Raw WinGet Report"] = "Eksporter WinGet-rapport", ["Install Selected Applications"] = "Installer valgte programmer",
        ["Maintenance & Notifications"] = "Vedlikehold og varsler", ["Weekday:"] = "Ukedag:",
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
        ["Automatic check intervals"] = "Automatiske sjekkintervaller",
        ["How often the background agent looks for updates. Applications default to 4 hours; drivers to 24 hours; Windows updates to 6 hours. Firmware stays manual."] =
            "Hvor ofte bakgrunnsagenten ser etter oppdateringer. Programmer er standard 4 timer; drivere 24 timer; Windows-oppdateringer 6 timer. Fastvare forblir manuell.",
        ["Applications (hours)"] = "Programmer (timer)",
        ["Drivers (hours)"] = "Drivere (timer)",
        ["Windows updates (hours)"] = "Windows-oppdateringer (timer)",
        ["Advanced categories (intervals)"] = "Avanserte kategorier (intervaller)",
        ["Interval (minutes)"] = "Intervall (minutter)", ["Preferred time"] = "Foretrukket tid",
        ["Mode: Auto-install (K1)"] = "Modus: Auto-installasjon (K1)",
        ["Mode: Download + ask (soon)"] = "Modus: Last ned + spør (kommer)",
        ["Mode: Download + ask (critical updates)"] = "Modus: Last ned + spør (kritiske oppdateringer)",
        ["Mode: Download + ask (general Windows update)"] = "Modus: Last ned + spør (generell Windows-oppdatering)",
        ["Optional auto-reboot (critical updates / general Windows update only)"] =
            "Valgfri auto-omstart (kun kritiske oppdateringer / generell Windows-oppdatering)",
        ["Allow optional auto-reboot after Critical/General installs (default off)"] =
            "Tillat valgfri auto-omstart etter kritisk/generell installasjon (standard av)",
        ["Update Express: Defender signatures may auto-update when enabled. Critical and general Windows updates download automatically and require confirmation before install. Optional auto-reboot after critical updates / general Windows update is off by default (confirm or outside active hours). Drivers and WinGet applications use the same ask-before-install path when enabled; firmware stays manual."] =
            "Update Express: Defender-signaturer kan auto-oppdateres når det er aktivert. Kritiske og generelle Windows-oppdateringer lastes ned automatisk og krever bekreftelse før installasjon. Valgfri auto-omstart etter kritiske oppdateringer / generell Windows-oppdatering er av som standard (bekreft eller utenfor arbeidstid). Drivere og WinGet-programmer bruker samme spør-før-installasjon-løp når de er aktivert; fastvare forblir manuell.",
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
        ["Thursday"] = "Torsdag", ["Friday"] = "Fredag", ["Saturday"] = "Lørdag", ["Sunday"] = "Søndag",
        ["You're up to date"] = "Du er oppdatert",
        ["Last checked: —"] = "Sist sjekket: —",
        ["Not checked"] = "Ikke sjekket",
        ["Checking…"] = "Sjekker…",
        ["Checking Windows…"] = "Sjekker Windows…",
        ["Include optional"] = "Inkluder valgfrie",
        ["All WUA drivers"] = "Alle WUA-drivere",
        ["OEM drivers"] = "OEM-drivere",
        ["TXT file download"] = "Last ned TXT-fil",
        ["DOC ↓"] = "DOC ↓",
        ["Download the OEM TXT/README companion when available."] =
            "Last ned OEM TXT/README-filen når den finnes.",
        ["Downloaded"] = "Lastet ned",
        ["Change setup…"] = "Endre oppsett…",
        ["About Update Express vs Windows Update"] = "Om Update Express vs Windows Update",
        ["Maintenance window"] = "Vedlikeholdsvindu",
        ["Install days (same clock for all)"] = "Installasjonsdager (samme klokke for alle)",
        ["Install time"] = "Installasjonstid",
        ["Defender signatures update continuously when enabled. Critical and general updates download in the background and install in your maintenance window."] =
            "Defender-signaturer oppdateres kontinuerlig når det er aktivert. Kritiske og generelle oppdateringer lastes ned i bakgrunnen og installeres i vedlikeholdsvinduet.",
        ["Status: —"] = "Status: —",
        ["Status: waiting for agent"] = "Status: venter på agent",
        ["Internet: checking …"] = "Internett: sjekker …",
        ["Microsoft TCP: checking …"] = "Microsoft TCP: sjekker …",
        ["WUA: Not confirmed"] = "WUA: Ikke bekreftet",
        ["Proxy: checking …"] = "Proxy: sjekker …",
        ["Background monitoring: waiting for agent"] = "Bakgrunnsovervåking: venter på agent",
        ["Last check: —  |  Available: —  |  New: —"] = "Siste sjekk: —  |  Tilgjengelig: —  |  Nye: —",
        ["Updates: —"] = "Oppdateringer: —",
        ["Windows automation: —"] = "Windows-automatisering: —",
        ["Preparing installation…"] = "Forbereder installasjon…",
        ["Ready"] = "Klar",
        ["Show"] = "Vis",
        ["Hide"] = "Skjul"
    };
    private void ApplyLanguage()
    {
        HistoryStatusDisplay.NorwegianUi = _schedule.Language == "no";
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
            if (element is System.Windows.Controls.TextBlock block
                && block.Name is not (
                    "DefenderLabel" or "DefenderChecked" or "HealthSummary" or "StatusText" or "ScheduleStatus"
                    or "AppsInfo" or "AppsSelectedCount" or "SelectedCountText" or "AutomationStatusText"
                    or "DefenderAutomationStatus" or "CriticalAutomationStatus" or "GeneralAutomationStatus"
                    or "FooterSafetyText" or "RebootNeededBanner" or "LongUptimeHint" or "UpdateUntilCurrentStatus"
                    or "UpdateStatusTitle" or "UpdateStatusChecked" or "AppSubtitle" or "ListHeading"
                    or "MonitorDetailsText"
                    or "MonitorUpdatesText" or "InternetStatusText" or "UpdateServerStatusText"
                    or "WuaStatusText" or "ProxyStatusText" or "OsVersionLabel" or "OsSupportIcon"
                    or "MaintenanceSummaryText")
                && !string.IsNullOrEmpty(block.Text))
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
            // Expander / headered controls: Header is not a visual child — walk it explicitly.
            if (element is System.Windows.Controls.HeaderedContentControl headered && headered.Header is not null)
                Translate(headered.Header);
            if (element is System.Windows.Controls.Expander expander && expander.Header is not null
                && expander is not System.Windows.Controls.HeaderedContentControl)
                Translate(expander.Header);
            if (element is System.Windows.DependencyObject obj)
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj); i++) Translate(System.Windows.Media.VisualTreeHelper.GetChild(obj, i));
        }
        Translate(this);
        string copyTip = norwegian ? "Kopier verdien" : "Copy this value";
        MachineModelCopy.ToolTip = copyTip;
        MachineProductCopy.ToolTip = copyTip;
        MachineSerialCopy.ToolTip = copyTip;
        MachineDeviceCopy.ToolTip = copyTip;
        const string optionalParityEn = "Some optional and preview updates may be available directly in Windows Update without appearing in UpdateExpress.";
        string optionalParityTip = norwegian && Norwegian.TryGetValue(optionalParityEn, out var optionalNo) ? optionalNo : optionalParityEn;
        if (IncludeOptionalUpdates is not null) IncludeOptionalUpdates.ToolTip = optionalParityTip;
        if (OptionalParityInfo is not null) OptionalParityInfo.ToolTip = optionalParityTip;
        const string openWuEn = "Opens Windows Update in Settings (ms-settings:windowsupdate). Does not scan, download, or install.";
        if (OptionalOpenWindowsUpdateButton is not null)
            OptionalOpenWindowsUpdateButton.ToolTip = norwegian && Norwegian.TryGetValue(openWuEn, out var openWuNo) ? openWuNo : openWuEn;
        if (RebootNeededBanner is not null) RebootNeededBanner.Text = RebootBannerText();
        RefreshLongUptimeHint();
        RefreshAppChromeLanguage();
        // StatusFixItCta label stays English ("Fix Update issues here") per SVHA.
        if (StatusFixItCta is not null)
            StatusFixItCta.ToolTip = norwegian
                ? "Reparer og diagnostiser Windows Update-problemer"
                : "Repair and diagnose Windows Update problems";
        RefreshOptionalParityHint();
        RefreshScheduleStatus();
        RefreshUpdateUntilCurrentUi();
        RefreshMonitorPanel(); // also refreshes Results heading times
        RefreshResultsHeading();
        RefreshThisPcButton();
        RefreshManufacturerSupportButton();
        RefreshWindows11AssistantButton();
        RefreshMaintenanceSummary();
        BuildInstallDayChips();
        RefreshAutomationStatusTexts();
        if (ProductOverviewInfo is not null)
            ProductOverviewInfo.ToolTip = norwegian
                ? "Om Update Express vs Windows Update"
                : "About Update Express vs Windows Update";
        if (DefenderRefreshButton is not null)
            DefenderRefreshButton.ToolTip = norwegian
                ? "Oppdater Defender- og brannmurstatus"
                : "Refresh Defender and firewall status";
        if (_lastDefender is not null) ApplyDefenderUi(_lastDefender);
        else if (DefenderChecked is not null && (DefenderChecked.Text == "Not checked" || DefenderChecked.Text == "Ikke sjekket"))
            DefenderChecked.Text = norwegian ? "Ikke sjekket" : "Not checked";
        RefreshHealthSummary();
        // Translate() restores ScanButton from Tag; keep the busy label while a scan is running.
        if (_scanButtonSearching)
            ScanButton.Content = norwegian ? "Søker...." : "Searching....";
        else
            ResetScanButtonLabel();
        ResetInstallButtonLabel();
        if (FooterSafetyText is not null)
        {
            const string footerEn =
                "Update Express: Defender signatures may auto-update when enabled. Critical and general Windows updates download automatically and require confirmation before install. Optional auto-reboot after critical updates / general Windows update is off by default (confirm or outside active hours). Drivers and WinGet applications use the same ask-before-install path when enabled; firmware stays manual.";
            FooterSafetyText.Text = norwegian && Norwegian.TryGetValue(footerEn, out var footerNo) ? footerNo : footerEn;
        }
        _fixItWindow?.ApplyLanguage(norwegian);
        RefreshUpdateStatusPanel();
    }

    private void RefreshAppChromeLanguage()
    {
        bool norwegian = _schedule.Language == "no";
        if (AppSubtitle is not null)
        {
            AppSubtitle.Text = norwegian
                ? $"Windows Update-administrasjon · Bakgrunnsovervåking v{ProductVersion.Display}"
                : $"Windows Update Management · Background Monitoring v{ProductVersion.Display}";
        }
        Title = norwegian
            ? "Update Express – Windows Update-administrasjon"
            : "Update Express – Windows Update Management";
    }
    private void RefreshScheduleStatus()
    {
        bool no = _schedule.Language == "no";
        ScheduleStatus.Text = _schedule.Enabled
            ? (no
                ? $"Påminnelser aktivert. Neste: {FormatTime(_schedule.NextOccurrence(DateTime.Now))}. Vises også fra bakgrunnsagenten når hovedvinduet er lukket."
                : $"Reminders enabled. Next: {FormatTime(_schedule.NextOccurrence(DateTime.Now))}. Also shown from the background agent when the main window is closed.")
            : (no ? "Påminnelser deaktivert. Du kan teste varsler uten å endre Windows." : "Reminders disabled. You can test notifications without modifying Windows.");
    }
    private void SaveSchedule_Click(object sender, RoutedEventArgs e)
    {
        string timeText = (SharedInstallTime?.Text ?? TimeChoice.Text).Trim();
        if (!TimeSpan.TryParseExact(timeText, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var at) || at >= TimeSpan.FromDays(1))
        {
            MessageBox.Show(this, "Enter time as HH:mm, for example 18:00.", "Invalid time", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        int mask = ReadInstallDaysMaskFromUi();
        if (mask == 0)
        {
            MessageBox.Show(this, _schedule.Language == "no" ? "Velg minst én dag." : "Select at least one day.", "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _policy = UpdatePolicy.Load();
        MaintenanceSetup.ApplyToPolicy(_policy, mask, timeText);
        _policy.SetupCompleted = true;
        _policy.Save();
        TimeChoice.Text = timeText;
        if (SharedInstallTime is not null) SharedInstallTime.Text = timeText;
        CriticalPreferredTime.Text = timeText;
        GeneralPreferredTime.Text = timeText;
        DayChoice.SelectedIndex = mask == MaintenanceSetup.AllDaysMask ? 0 : MaintenanceSetup.FirstSelectedWeekday(mask) + 1;
        _schedule.Weekday = mask == MaintenanceSetup.AllDaysMask ? 7 : MaintenanceSetup.FirstSelectedWeekday(mask);
        _schedule.Time = timeText;
        _schedule.Enabled = RemindersEnabled.IsChecked == true;
        _schedule.LastPromptedOccurrence = null;
        _schedule.ConfirmedOccurrence = null;
        _schedule.SnoozedUntil = null;
        _schedule.Save();
        ReminderSchedule.Log("schedule_saved", $"Mask={mask}; Time={_schedule.Time}; Enabled={_schedule.Enabled}");
        RefreshMaintenanceSummary();
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
        // Tray may have snoozed/confirmed/prompted while Main was closed or while a tray
        // dialog was still open after Main opened — honor schedule.json, not a stale cache.
        SyncReminderPromptStateFromDisk();
        if (!_schedule.Enabled) return;
        // Another process (tray) already owns the live Påminnelse — do not stack a second one.
        if (MaintenanceReminderPromptGate.IsReminderPromptHeldElsewhere()) return;
        var now = DateTime.Now;
        if (_schedule.SnoozedUntil is DateTime snooze && now < snooze) return;
        var todayIndex = ((int)now.DayOfWeek + 6) % 7;
        int mask = MaintenanceSetup.ClampMask(_policy.InstallDaysMask);
        if (mask != 0)
        {
            if (!MaintenanceSetup.HasDay(mask, todayIndex)) return;
        }
        else if (_schedule.Weekday != 7 && todayIndex != _schedule.Weekday) return;
        if (!TimeSpan.TryParseExact(_schedule.Time, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var at)) return;

        // Already confirmed for today — do not keep prompting.
        if (_schedule.ConfirmedOccurrence is DateTime confirmed && confirmed.Date == now.Date) return;

        var due = now.Date.Add(at);
        var slot = ReminderSchedule.EffectiveReminderSlot(now, at);
        if (slot is null)
        {
            // Past the last :00/:30 before midnight with no confirm → remember for next day's dialog.
            if (now >= due
                && ReminderSchedule.NextHalfHourSlot(now) is null
                && _schedule.ConfirmedOccurrence?.Date != now.Date)
            {
                MarkMissedUnconfirmedDay(now.Date);
            }
            return;
        }

        if (_schedule.LastPromptedOccurrence == slot.Value) return;
        // Only prompt when maintenance is useful: pending updates, Windows reboot pending,
        // or long uptime (same 7-day floor as the Oppetid hint). Skip quietly otherwise so a
        // clean PC is not asked to confirm a restart for no reason.
        if (!TryGetMaintenanceReminderReason(out _))
            return;
        ShowReminder(false, slot.Value);
    }

    /// <summary>
    /// Pull prompt-related fields from schedule.json so tray decisions (snooze / confirm /
    /// LastPrompted) are visible to Main's timer without requiring a display-settings change.
    /// </summary>
    private void SyncReminderPromptStateFromDisk()
    {
        try
        {
            var fresh = ReminderSchedule.Load();
            _schedule.Enabled = fresh.Enabled;
            _schedule.Weekday = fresh.Weekday;
            _schedule.Time = fresh.Time;
            _schedule.LastPromptedOccurrence = fresh.LastPromptedOccurrence;
            _schedule.ConfirmedOccurrence = fresh.ConfirmedOccurrence;
            _schedule.SnoozedUntil = fresh.SnoozedUntil;
            _schedule.MissedUnconfirmedDate = fresh.MissedUnconfirmedDate;
        }
        catch
        {
            /* keep in-memory schedule */
        }
    }

    /// <summary>
    /// Real (non-test) reminders need a concrete reason: updates waiting, reboot pending, or uptime ≥ 7 days.
    /// </summary>
    private bool TryGetMaintenanceReminderReason(out string reasonKey)
    {
        reasonKey = "";
        int pending = CountAvailableUpdates();
        // The tray skips its updates-ready dialogs while this window is open and a Påminnelse
        // is due, so the updates it found must count here even before this window has scanned.
        if (pending > 0 || CategoryPromptState.LoadUpdatesWaitingForUser(DateTime.Now).Count > 0)
        {
            reasonKey = "pending";
            return true;
        }
        if (RebootPendingReader.IsRebootPending())
        {
            reasonKey = "reboot";
            return true;
        }
        var up = DateTime.Now - _bootedAt;
        if (up < TimeSpan.Zero) up = TimeSpan.Zero;
        if (up >= LongUptimeThreshold)
        {
            reasonKey = "uptime";
            return true;
        }
        return false;
    }

    /// <summary>
    /// Compact lines for the reminder dialog (name / KB / version), same sources as Results.
    /// </summary>
    private List<string> BuildPendingReminderLines()
    {
        var lines = new List<string>();
        const int maxLines = 12;

        foreach (var entry in _pendingSnapshot)
        {
            if (lines.Count >= maxLines) break;
            bool include = !string.IsNullOrWhiteSpace(entry.UpdateId)
                || entry.Source == FeatureUpdateOfferReader.Source;
            if (!include) continue;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(entry.KB))
                parts.Add(entry.KB.Trim());
            string title = (entry.DisplayTitle ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(title))
                parts.Add(title);
            string version = (entry.UnifiedVersion ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(version) &&
                !title.Contains(version, StringComparison.OrdinalIgnoreCase))
                parts.Add(version);
            if (parts.Count == 0) continue;
            lines.Add("•  " + string.Join("  ·  ", parts));
        }

        foreach (var app in _appEntries)
        {
            if (lines.Count >= maxLines) break;
            string name = (app.DisplayName ?? app.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            string ver = (app.VersionSummary ?? "").Trim();
            lines.Add(string.IsNullOrWhiteSpace(ver)
                ? "•  " + name
                : "•  " + name + "  ·  " + ver);
        }

        var trayOnly = CategoryPromptState.NotAlreadyListed(
            CategoryPromptState.LoadUpdatesWaitingForUser(DateTime.Now),
            _pendingSnapshot.Select(e => e.UpdateId ?? "").Concat(_appEntries.Select(a => a.Id ?? "")));
        foreach (var item in trayOnly)
        {
            if (lines.Count >= maxLines) break;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.KB)) parts.Add(item.KB.Trim());
            if (!string.IsNullOrWhiteSpace(item.Title)) parts.Add(item.Title.Trim());
            if (parts.Count == 0) continue;
            lines.Add("•  " + string.Join("  ·  ", parts));
        }

        int total = CountAvailableUpdates() + trayOnly.Count;
        if (total > lines.Count)
        {
            bool norwegian = _schedule.Language == "no";
            lines.Add(norwegian
                ? $"… og {total - lines.Count} til (se Søke resultater)."
                : $"… and {total - lines.Count} more (see Search results).");
        }

        return lines;
    }

    private void MarkMissedUnconfirmedDay(DateTime day)
    {
        var date = day.Date;
        if (_schedule.MissedUnconfirmedDate?.Date == date) return;
        _schedule.MissedUnconfirmedDate = date;
        _schedule.Save();
        ReminderSchedule.Log("reminder_deferred_next_day", date.ToString("yyyy-MM-dd"));
    }

    private void ShowReminder(bool isTest, DateTime occurrence)
    {
        bool norwegian = _schedule.Language == "no";
        bool showMissed = !isTest
            && _schedule.MissedUnconfirmedDate is DateTime missed
            && missed.Date < DateTime.Now.Date;

        string reasonKey = "";
        if (!isTest)
            TryGetMaintenanceReminderReason(out reasonKey);

        IReadOnlyList<string>? pendingLines = null;
        if (!isTest && string.Equals(reasonKey, "pending", StringComparison.OrdinalIgnoreCase))
            pendingLines = BuildPendingReminderLines();

        if (!isTest)
        {
            _schedule.LastPromptedOccurrence = occurrence;
            _schedule.SnoozedUntil = null;
            _schedule.Save();
        }

        _pendingReminder = (isTest, occurrence, norwegian, showMissed, reasonKey, pendingLines);

        // One instance only: refresh the open Påminnelse instead of stacking another window
        // (CheckReminder keeps firing inside ShowDialog's nested dispatcher loop / BeginInvoke queue).
        if (ReminderDialog.IsShowing && ReminderDialog.Current is ReminderDialog open)
        {
            open.ApplyContent(isTest, occurrence, norwegian, showMissed, reasonKey, pendingLines);
            ReminderSchedule.Log(isTest ? "test_reminder_refresh" : "reminder_refresh",
                occurrence.ToString("o") + (string.IsNullOrEmpty(reasonKey) ? "" : "|" + reasonKey));
            return;
        }

        if (_reminderPresentQueued)
            return;

        _reminderPresentQueued = true;

        // Defer until ApplicationIdle so a reminder that fires during lock/sleep/display
        // wake does not open a blank white WPF frame over a half-ready desktop.
        void Present()
        {
            try
            {
                var args = _pendingReminder
                    ?? (isTest, occurrence, norwegian, showMissed, reasonKey, pendingLines);

                if (ReminderDialog.IsShowing && ReminderDialog.Current is ReminderDialog existing)
                {
                    existing.ApplyContent(
                        args.IsTest, args.Occurrence, args.Norwegian, args.ShowMissed,
                        args.ReasonKey, args.PendingLines);
                    ReminderSchedule.Log(args.IsTest ? "test_reminder_refresh" : "reminder_refresh",
                        args.Occurrence.ToString("o") + (string.IsNullOrEmpty(args.ReasonKey) ? "" : "|" + args.ReasonKey));
                    return;
                }

                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                if (IsLoaded) Activate();

                // Tray may still own an open Påminnelse in another process.
                if (!MaintenanceReminderPromptGate.TryEnterReminderPrompt())
                {
                    ReminderSchedule.Log(
                        args.IsTest ? "test_reminder_skipped_tray_owns" : "reminder_skipped_tray_owns",
                        args.Occurrence.ToString("o"));
                    return;
                }

                try
                {
                    bool ownerOk = IsLoaded && IsVisible && WindowState != WindowState.Minimized;
                    var dialog = new ReminderDialog(
                        args.IsTest, args.Occurrence, args.Norwegian, args.ShowMissed,
                        args.ReasonKey, args.PendingLines)
                    {
                        Owner = ownerOk ? this : null,
                        WindowStartupLocation = ownerOk
                            ? WindowStartupLocation.CenterOwner
                            : WindowStartupLocation.CenterScreen
                    };
                    dialog.ShowDialog();
                    // Use dialog mode/occurrence after possible ApplyContent refresh (test↔real).
                    bool decidedIsTest = dialog.IsTest;
                    var decidedOccurrence = dialog.Occurrence;
                    string reasonForLog = "";
                    if (!decidedIsTest)
                    {
                        string key = args.ReasonKey;
                        if (_pendingReminder is { } latest && !latest.IsTest)
                            key = latest.ReasonKey;
                        if (!string.IsNullOrEmpty(key))
                            reasonForLog = "|" + key;
                    }

                    ReminderSchedule.Log(
                        decidedIsTest ? "test_reminder" : "reminder",
                        dialog.Decision + reasonForLog);
                    if (!decidedIsTest)
                    {
                        if (dialog.ShowMissedPreviousDay)
                            _schedule.MissedUnconfirmedDate = null;

                        if (dialog.Decision == "confirmed")
                        {
                            _schedule.ConfirmedOccurrence = decidedOccurrence;
                            _schedule.SnoozedUntil = null;
                            ScheduleConfirmedReboot(decidedOccurrence);
                        }
                        else if (dialog.Decision == "snoozed")
                        {
                            _schedule.SnoozedUntil = DateTime.Now.AddMinutes(30);
                        }
                        else
                        {
                            // Lukk / AFK auto-close (30 min): soft dismiss — roll forward; never confirm restart.
                            var next = ReminderSchedule.NextHalfHourSlot(DateTime.Now);
                            if (next is DateTime rolled)
                            {
                                _schedule.SnoozedUntil = rolled;
                                ReminderSchedule.Log("reminder_rolled", rolled.ToString("o"));
                            }
                            else
                            {
                                MarkMissedUnconfirmedDay(decidedOccurrence.Date);
                            }
                        }
                        _schedule.Save();
                    }
                    RefreshScheduleStatus();
                }
                finally
                {
                    MaintenanceReminderPromptGate.ReleaseReminderPrompt();
                }
            }
            catch (Exception ex)
            {
                ReminderSchedule.Log(isTest ? "test_reminder_error" : "reminder_error", ex.Message);
            }
            finally
            {
                _reminderPresentQueued = false;
            }
        }

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, Present);
    }

    /// <summary>
    /// After the user confirms the reminder, persist a pending reboot so tray/service
    /// can fire it even if MainWindow is closed. UI timer is a fast path while open.
    /// </summary>
    private void ScheduleConfirmedReboot(DateTime occurrence)
    {
        bool norwegian = _schedule.Language == "no";
        _confirmedRebootTimer?.Stop();
        _confirmedRebootTimer = null;

        int countdown = Math.Clamp(_policy.WindowsAutomation.AutoReboot.CountdownSeconds, 60, 3600);
        var when = occurrence <= DateTime.Now ? DateTime.Now : occurrence;
        var delay = when - DateTime.Now;
        string reason = norwegian
            ? "Bekreftet vedlikeholdspåminnelse."
            : "Confirmed maintenance reminder.";
        string comment = norwegian
            ? "Update Express: omstart etter bekreftet vedlikeholdspåminnelse. Lagre arbeidet."
            : "Update Express: restart after confirmed maintenance reminder. Save your work.";

        // Persist for tray agent / Windows service (survives closing MainWindow).
        RebootCoordinator.SchedulePending("reminder", when, reason);
        ReminderSchedule.Log("confirmed_reboot_armed", when.ToString("o"));

        if (delay <= TimeSpan.FromSeconds(5))
        {
            StatusText.Text = norwegian
                ? $"Omstart planlagt — Windows starter på nytt om {countdown} sekunder."
                : $"Restart scheduled — Windows will restart in {countdown} seconds.";
            RebootCoordinator.ClearPending();
            RebootCoordinator.StartCountdownReboot("reminder", countdown, comment);
            return;
        }

        StatusText.Text = norwegian
            ? $"Omstart bekreftet for {_schedule.Format(when)}. PC-en starter på nytt da (også hvis vinduet lukkes)."
            : $"Restart confirmed for {_schedule.Format(when)}. This PC will restart then (even if you close this window).";

        _confirmedRebootTimer = new DispatcherTimer { Interval = delay };
        _confirmedRebootTimer.Tick += (_, _) =>
        {
            _confirmedRebootTimer?.Stop();
            _confirmedRebootTimer = null;
            // Show the countdown notice; without it the user sees only the Windows sign-out notice.
            RebootCoordinator.RunDueJob(allowUiPrompt: true);
        };
        _confirmedRebootTimer.Start();
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
        const string english = "Check for updates";
        // ApplyLanguage stores the first text it sees in Tag. A busy "Searching...." /
        // "Søker...." label captured there would be restored on the next language pass.
        if (ScanButton.Tag is not string tag
            || tag.Contains("Searching", StringComparison.Ordinal)
            || tag.Contains("Søker", StringComparison.Ordinal)
            || tag.Contains("⌕", StringComparison.Ordinal))
            ScanButton.Tag = english;
        ScanButton.Content = _schedule.Language == "no"
            ? "Se etter oppdateringer"
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
            SyncDefenderHeaderFromHistory();
            HidePendingAlreadyInHistory(result);
            // Historikk may show a newer Defender install than the last header poll.
            await RefreshDefenderAsync(quiet: true);
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
        ClearInstallOutcomeHighlight();
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
            bool oemDrivers = ShowOemDrivers.IsChecked == true;
            var windows = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(includeOptional, allDrivers));
            ReconcileWindowsPromptsWithScan(windows);
            int oemCount = 0;
            if (oemDrivers)
            {
                try
                {
                    // Evidence-first before catalog merge; one session confirm if still ambiguous.
                    PromoteOemDownloads(_schedule.Language == "no", previousBootOnly: true, allowConfirmPrompt: true);
                    var identity = _machineIdentity;
                    if (IsMissingIdentity(identity.Manufacturer) || IsMissingIdentity(identity.Product) && IsMissingIdentity(identity.Model))
                        identity = await Task.Run(MachineIdentityReader.Read).ConfigureAwait(true);
                    var oem = await OemDriverSearch.SearchAsync(
                        identity,
                        msg => Dispatcher.Invoke(() =>
                        {
                            if (StatusText is not null) StatusText.Text = msg;
                        })).ConfigureAwait(true);
                    oemCount = oem.Count;
                    if (oemCount > 0)
                    {
                        // Keep WUA rows first, then OEM with Critical already grouped at the top of that block.
                        var merged = new List<UpdateEntry>(windows.Count + oem.Count);
                        merged.AddRange(windows);
                        merged.AddRange(oem);
                        windows = merged;
                    }
                }
                catch (Exception ex)
                {
                    ActivityLog.TryWrite("OemDriverSearch", "Failed", new { Exception = ex.ToString() });
                    StatusText.Text = (_schedule.Language == "no" ? "OEM-drivernsøk feilet: " : "OEM driver search failed: ") + ex.Message;
                }
            }
            ReplacePendingSnapshot(windows);
            _lastSuccessfulScan = DateTime.Now;
            // Keep the user's current results tab (All is the startup default).
            SetTab(_activeTab);
            SyncResultsExpandedForPending(windows.Count);
            int driverCount = windows.Count(e => e.Category == "Driver / firmware");
            int wuaDriverCount = windows.Count(e => e.Category == "Driver / firmware" && !e.OpensExternalUrl);
            try
            {
                var apps = await WingetReader.ScanAsync();
                WingetAvailableCache.Write(apps.Updates, apps.Raw);
                ApplyAppScanResult(apps.Updates, apps.Raw);
                ReconcileApplicationsPromptWithScan(apps.Updates);
                StatusText.Text = oemCount > 0
                    ? $"Windows: {windows.Count - driverCount}, drivers: {wuaDriverCount} WUA + {oemCount} OEM, applications: {apps.Updates.Count}."
                    : $"Windows: {windows.Count - driverCount}, drivers: {driverCount}, applications: {apps.Updates.Count}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "WinGet scan failed: " + ex.Message;
                StatusText.Text = $"Windows scan complete; WinGet failed: {ex.Message}";
            }
            RefreshHealthSummary();
            RefreshUpdateStatusPanel();
            RefreshResultsHeading();
            SyncResultsExpandedForPending(CountAvailableUpdates());
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

    private void StatusFixItCta_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Keep the Vedlikehold / Maintenance Expander from toggling when opening FIX IT from the header CTA.
        e.Handled = true;
        FixItOpen_Click(sender, e);
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
    internal void FixItProblemPcPipeline() => _ = RunProblemPcPipelineAsync();
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

    private async Task RunProblemPcPipelineAsync()
    {
        if (_busy) return;
        bool norwegian = _schedule.Language == "no";
        var confirm = AppDialog.Show(
            _fixItWindow as Window ?? this,
            norwegian
                ? "Problem-PC-løypen kjører dybdesjekk, deretter Middels/Dyp Windows Update-fiks ved behov, og til slutt et nytt søk.\n\nDette kan ta lang tid og krever administrator. Fortsette?"
                : "The Problem PC pipeline runs a deep check, then Medium/Deep Windows Update fix when needed, and finally retries the search.\n\nThis can take a long time and requires administrator. Continue?",
            norwegian ? "Problem-PC-løype" : "Problem PC pipeline",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (!ElevationHelper.IsElevated)
        {
            var elevate = AppDialog.Show(
                _fixItWindow as Window ?? this,
                norwegian
                    ? "Administratorrettigheter kreves. Starte Update Express på nytt som administrator?"
                    : "Administrator privileges are required. Restart Update Express as administrator?",
                norwegian ? "Problem-PC-løype" : "Problem PC pipeline",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (elevate != MessageBoxResult.Yes) return;
            try
            {
                ElevationHelper.RestartElevated("--installed");
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show(_fixItWindow as Window ?? this, ex.Message,
                    norwegian ? "Problem-PC-løype" : "Problem PC pipeline",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        _busy = true;
        ScanButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = norwegian ? "Problem-PC-løype kjører…" : "Problem PC pipeline running…";
        try
        {
            var progress = new Progress<string>(msg =>
            {
                try { StatusText.Text = msg; } catch { /* closing */ }
            });
            var report = await ProblemPcRepair.RunAsync(
                progress,
                IncludeOptionalUpdates.IsChecked == true,
                ShowAllDrivers.IsChecked == true).ConfigureAwait(true);

            await ReloadAfterWindowsUpdateFixAsync(replacedUpdateCache: report.FixCompleted, scanCompleted: false)
                .ConfigureAwait(true);
            try
            {
                if (File.Exists(ProblemPcRepair.LastTextPath))
                    Process.Start(new ProcessStartInfo(ProblemPcRepair.LastTextPath) { UseShellExecute = true });
            }
            catch { /* ignore */ }

            MessageBox.Show(
                _fixItWindow as Window ?? this,
                norwegian
                    ? $"Ferdig.\n\nDybdesjekk: FAIL={report.DeepCheckFail}, WARN={report.DeepCheckWarn}\nFiks: {report.FixLevel ?? "hoppet over"}\nVentende etter søk: {report.PendingAfter}\n\nRapport: {ProblemPcRepair.LastTextPath}"
                    : $"Finished.\n\nDeep check: FAIL={report.DeepCheckFail}, WARN={report.DeepCheckWarn}\nFix: {report.FixLevel ?? "skipped"}\nPending after search: {report.PendingAfter}\n\nReport: {ProblemPcRepair.LastTextPath}",
                norwegian ? "Problem-PC-løype" : "Problem PC pipeline",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            RefreshPatchConflictBanner();
            RmmStatusExport.WriteBestEffort("problem-pc-ui-complete", force: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(_fixItWindow as Window ?? this, ex.Message,
                norwegian ? "Problem-PC-løype" : "Problem PC pipeline",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = norwegian ? "Problem-PC-løype feilet." : "Problem PC pipeline failed.";
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
            UpdateSelectionControls();
        }
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
            if (scanCompleted)
                SyncResultsExpandedForPending(CountAvailableUpdates());
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
            _historySnapshot.RemoveAll(row => OutcomeIdentity.SameUpdate(row, entry));
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
        if (entry.OpensExternalUrl)
        {
            OpenOemDriverEntry(entry);
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
        MessageBox.Show(this, $"Update: {entry.Title}\nKB: {entry.KB}\nCategory: {entry.Category}\nPriority: {entry.PriorityDisplay}\nStatus: {HistoryStatusDisplay.Format(entry.Status)}\nDate: {entry.DateShown}\nError: {entry.ErrorCode}\n\n{note}{assessment}\n\nUninstall is not implemented.", "Update Details (read-only)", MessageBoxButton.OK, MessageBoxImage.Information);
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
        // Distinct by UpdateId — Search already collapses Title+Version lookalikes, but a stale
        // snapshot can still hold two selected rows for the same WUA package.
        var drivers = _pendingSnapshot
            .Where(x => x.IsSelected && x.Category == "Driver / firmware" && !string.IsNullOrWhiteSpace(x.UpdateId))
            .GroupBy(x => x.UpdateId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.DriverAssigned).First())
            .ToList();
        var apps = _appEntries.Where(x => x.IsSelected).ToList();
        // OEM catalog rows never go through WUA (empty UpdateId): each package is downloaded
        // and its own installer is run.
        var oem = _pendingSnapshot.Where(x => x.IsSelected && IsOemDownloadable(x)).ToList();
        if (software.Count + drivers.Count + apps.Count == 0)
        {
            // No restart-as-administrator step, like the per-row button: the installer is
            // shell-started, so Windows asks for elevation itself if this process lacks it.
            await InstallSelectedOemAsync(oem, confirm: true);
            return;
        }
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
        bool norwegian = _schedule.Language == "no";
        var queue = new List<InstallationQueueItem>();
        queue.AddRange(software.Select(x => new InstallationQueueItem("Windows Update", x.Title) { UpdateId = x.UpdateId }));
        queue.AddRange(drivers.Select(x => new InstallationQueueItem("Driver", x.Title) { UpdateId = x.UpdateId }));
        queue.AddRange(apps.Select(x => new InstallationQueueItem("WinGet", x.DisplayName)));
        var manager = new InstallationManagerWindow(queue, norwegian: norwegian) { Owner = this };
        string oemLines = oem.Count == 0 ? "" : norwegian
            ? $"\nOEM-pakker: {oem.Count}\n\nEtter de andre installasjonene lastes hver OEM-pakke ned, og installasjonsprogrammet kjøres, én om gangen. Pakker uten installasjonsprogram (f.eks. zip) blir bare lagret."
            : $"\nOEM packages: {oem.Count}\n\nAfter the other installations, each OEM package is downloaded and its installer is run, one at a time. Packages without an installer (e.g. zip) are only saved.";
        string restartLine = oem.Count == 0
            ? (norwegian ? "Ingen automatisk omstart." : "No automatic restart will be performed.")
            : (norwegian
                ? "Update Express starter ikke på nytt automatisk, men et OEM-installasjonsprogram kan selv kreve eller utføre omstart."
                : "Update Express does not restart automatically, but an OEM installer may require or perform a restart itself.");
        if (AppDialog.Show(this,
                norwegian
                    ? $"Installere {queue.Count + oem.Count} valgte oppdateringer fra alle faner?\n\nWindows-oppdateringer: {software.Count}\nDrivere: {drivers.Count}\nWinGet: {apps.Count}{oemLines}\n\n{restartLine}"
                    : $"Install {queue.Count + oem.Count} selected updates from all tabs?\n\nWindows Updates: {software.Count}\nDrivers: {drivers.Count}\nWinGet: {apps.Count}{oemLines}\n\n{restartLine}",
                norwegian ? "Bekreft kombinert installasjon" : "Confirm combined installation",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true;
        InstallButtonActive = true;
        InstallButton.Content = norwegian ? "Installerer oppdateringer…" : "Installing updates…";
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
                        string? tip = null;
                        if (state.StartsWith("Failed", StringComparison.Ordinal) && result is not null && !string.IsNullOrWhiteSpace(result.ErrorCode))
                        {
                            var info = WindowsUpdateErrorCatalog.Lookup(result.ErrorCode);
                            tip = string.IsNullOrWhiteSpace(info.Summary)
                                ? result.ErrorCode
                                : $"{info.Code} — {info.Name}. {info.Summary} {info.Guidance}".Trim();
                        }
                        manager.CompleteItem(start + i, state, state.StartsWith("Failed", StringComparison.Ordinal), tip);
                    }
                }
                catch (Exception ex) { for (int i = 0; i < software.Count; i++) manager.CompleteItem(start + i, "Failed", true); Log("WUA", "Failed", ex.ToString()); }
                cursor += software.Count;
            }
            foreach (var d in drivers)
            {
                int idx = cursor++;
                manager.SetStage((norwegian ? "Sjekker driveridentitet på nytt: " : "Rechecking driver identity: ") + d.Title + "…");
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
                    // Only hold after a successful install — failed/partial must stay retryable.
                    if (driverResult is not null &&
                        driverResult.Status == "Installert" &&
                        (driverResult.Source ?? "").Contains("Restart required", StringComparison.OrdinalIgnoreCase))
                    {
                        // Hold this UpdateId until Windows reboot indicators clear (avoids Realtek-style second install).
                        DriverRebootHoldStore.Remember(d, "Restart required after WUA driver install");
                    }
                    string driverState = driverResult == null ? notVerifiedLabel :
                        driverResult.Status is "Installert" or "Already up to date" ? verifyingLabel :
                        driverResult.Status is "Delvis fullført" or "Verification unavailable" ? notVerifiedLabel :
                        "Failed" + (string.IsNullOrWhiteSpace(driverResult.ErrorCode) ? "" : " " + driverResult.ErrorCode);
                    string? driverTip = null;
                    if (driverState.StartsWith("Failed", StringComparison.Ordinal) && driverResult is not null && !string.IsNullOrWhiteSpace(driverResult.ErrorCode))
                    {
                        var info = WindowsUpdateErrorCatalog.Lookup(driverResult.ErrorCode);
                        driverTip = string.IsNullOrWhiteSpace(info.Summary)
                            ? driverResult.ErrorCode
                            : $"{info.Code} — {info.Name}. {info.Summary} {info.Guidance}".Trim();
                    }
                    manager.CompleteItem(idx, driverState, driverState.StartsWith("Failed", StringComparison.Ordinal), driverTip);
                }
                catch (Exception ex)
                {
                    string hresult = ex.HResult < 0 ? $" 0x{unchecked((uint)ex.HResult):X8}" : "";
                    string? tip = null;
                    if (!string.IsNullOrWhiteSpace(hresult))
                    {
                        var info = WindowsUpdateErrorCatalog.Lookup(hresult.Trim());
                        tip = string.IsNullOrWhiteSpace(info.Summary)
                            ? hresult.Trim()
                            : $"{info.Code} — {info.Name}. {info.Summary} {info.Guidance}".Trim();
                    }
                    manager.CompleteItem(idx, "Failed" + hresult, true, tip);
                    Log("Driver", "Failed", new { d.Title, d.UpdateId, d.DriverHardwareId, Exception = ex.ToString(), InnerException = ex.InnerException?.ToString() });
                    manager.SetStage((norwegian ? "Driver feilet: " : "Driver failed: ") + ex.Message);
                }
            }
            foreach (var app in apps)
            {
                int idx = cursor++;
                manager.BeginItem(idx, norwegian ? ("Installerer " + app.DisplayName + "…") : ("Installing " + app.DisplayName + "…"));
                int code; string output;
                try { using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(20)); (code, output) = await WingetReader.UpgradeAsync(app.Id, timeout.Token); }
                catch (Exception ex) { code = -1; output = ex.ToString(); }
                Log("WinGet", code == 0 ? "Verification pending" : "Failed", new { app.Name, app.Id, app.Installed, app.Available, ExitCode = code, Output = output });
                if (code == 0)
                    manager.CompleteItem(idx, verifyingLabel, false);
                else
                {
                    var (state, tip, notice) = WingetReader.DescribeUpgradeFailure(code, app.DisplayName, norwegian);
                    manager.CompleteItem(idx, state, true, tip);
                    if (!string.IsNullOrWhiteSpace(notice))
                    {
                        manager.SetNotice(notice);
                        manager.SetStage(notice);
                    }
                }
            }
            // Terminal install outcomes are on screen — unlock Close before the long rescan.
            manager.AllowClose(norwegian);
            bool verifyTimedOut = false;
            bool stillOfferedAfterSuccess = false;
            string catalogLagGuidance = norwegian
                ? "Installasjonen lyktes, men Windows kan fortsatt vise oppdateringen til katalogen er oppdatert eller PC-en er startet på nytt."
                : "Install succeeded, but Windows may still offer the update until its catalog refreshes or the PC is restarted.";

            bool needWuaVerify = software.Count + drivers.Count > 0 &&
                queue.Take(software.Count + drivers.Count).Any(q =>
                    !q.State.StartsWith("Failed", StringComparison.OrdinalIgnoreCase) &&
                    !q.State.StartsWith("Feilet", StringComparison.OrdinalIgnoreCase));
            // WinGet often locks while Microsoft Store is installing the same package (tech-mismatch path).
            // Skip a blocking rescan when every WinGet row already failed — refresh in the background later.
            bool allWingetFailed = apps.Count > 0 && apps.Select((_, i) => queue[software.Count + drivers.Count + i])
                .All(q => q.State.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                       || q.State.StartsWith("Feilet", StringComparison.OrdinalIgnoreCase));
            bool needWingetVerify = apps.Count > 0 && !allWingetFailed;

            if (needWuaVerify || needWingetVerify)
                manager.SetStage(norwegian ? "Oppdaterer Windows Update- og WinGet-resultater…" : "Refreshing Windows Update and WinGet results…");
            else
                manager.SetStage(norwegian
                    ? "Hopper over WinGet-rescan (alle WinGet-rader feilet — Store/winget kan være opptatt)."
                    : "Skipping WinGet rescan (all WinGet rows failed — Store/winget may be busy).");

            if (needWuaVerify)
            {
                try
                {
                    var pendingTask = WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(true, true));
                    outstandingWork.Add(pendingTask);
                    var pending = await AwaitInstallationTaskAsync(
                        pendingTask,
                        manager,
                        softTimeout: TimeSpan.FromMinutes(3),
                        timeoutMessage: norwegian ? "Verifisering (WUA-rescan) tidsavbrutt." : "Verification (WUA rescan) timed out.",
                        abandonOnTimeout: true);
                    outstandingWork.Remove(pendingTask);
                    ReconcileWindowsPromptsWithScan(pending);
                    // Keep last search results for display; rescan is only for verification.
                    // Successfully installed rows are pruned below after queue states are resolved.
                    var historyTask = WindowsUpdateReader.RunStaAsync(WindowsUpdateReader.HistoryForDisplay);
                    outstandingWork.Add(historyTask);
                    _historySnapshot = await AwaitInstallationTaskAsync(
                        historyTask,
                        manager,
                        softTimeout: TimeSpan.FromMinutes(2),
                        timeoutMessage: norwegian ? "Verifisering (historikk) tidsavbrutt." : "Verification (history) timed out.",
                        abandonOnTimeout: true);
                    outstandingWork.Remove(historyTask);
                    SyncDefenderHeaderFromHistory();
                    HidePendingAlreadyInHistory(_historySnapshot);
                    _ = RefreshDefenderAsync(quiet: true);
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
                    PruneSuccessfullyInstalledFromPending(software, drivers, queue);
                    Log("WUA", "Rescan completed", new { Count = pending.Count, PendingKept = _pendingSnapshot.Count });
                }
                catch (TimeoutException tex)
                {
                    verifyTimedOut = true;
                    Log("WUA", "Rescan timed out", tex.Message);
                    manager.SetStage(tex.Message + " " + retryGuidance);
                    for (int i = 0; i < software.Count + drivers.Count; i++)
                        if (InstallationManagerWindow.IsVerifyingState(queue[i].State))
                            manager.CompleteItem(i, completedLabel, false);
                    PruneSuccessfullyInstalledFromPending(software, drivers, queue);
                }
                catch (Exception ex)
                {
                    Log("WUA", "Rescan failed", ex.Message);
                    manager.SetStage((norwegian ? "Ny skanning av Windows Update feilet: " : "Windows Update rescan failed: ") + ex.Message);
                    PruneSuccessfullyInstalledFromPending(software, drivers, queue);
                }
            }

            if (needWingetVerify)
            {
                try
                {
                    var scanTask = WingetReader.ScanAsync();
                    outstandingWork.Add(scanTask);
                    // Keep this short: Store installs often block winget for a long time.
                    var scan = await AwaitInstallationTaskAsync(
                        scanTask,
                        manager,
                        softTimeout: TimeSpan.FromSeconds(90),
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
                catch (Exception ex) { Log("WinGet", "Rescan failed", ex.Message); manager.SetStage((norwegian ? "Ny skanning av WinGet feilet: " : "WinGet rescan failed: ") + ex.Message); }
            }
            else if (apps.Count > 0)
            {
                // Deferred refresh so Store/winget contention cannot freeze the main window.
                ScheduleBackgroundWingetRefresh();
            }

            // Always prune completed WUA rows from the last search (even if verify was skipped).
            PruneSuccessfullyInstalledFromPending(software, drivers, queue);
            SetTab(_activeTab);
            RefreshHealthSummary();
            Log("InstallationManager", "Finished", new { Total = queue.Count, verifyTimedOut, stillOfferedAfterSuccess, needWuaVerify, needWingetVerify, allWingetFailed });
            string? guidance = queue.Any(q => InstallationManagerWindow.IsUnverifiedState(q.State))
                ? retryGuidance
                : stillOfferedAfterSuccess || verifyTimedOut
                    ? catalogLagGuidance
                    : null;
            var outcome = manager.Finish(norwegian, guidance, verifyTimedOut ? timedOutLabel : notVerifiedLabel);
            SetInstallOutcome(outcome.Completed, outcome.Failed, queue);
            StatusText.Text = norwegian
                ? "Installasjon ferdig. Installasjonsbehandling lukkes automatisk om 15 sekunder."
                : "Installation finished. Installation Manager closes automatically in 15 seconds.";
        }
        catch (Exception ex)
        {
            Log("InstallationManager", "Failed", ex.ToString());
            manager.SetStage((norwegian ? "Installasjon avbrutt: " : "Installation interrupted: ") + ex.Message);
            var outcome = manager.Finish(norwegian, retryGuidance, notVerifiedLabel);
            SetInstallOutcome(outcome.Completed, outcome.Failed, queue);
            MessageBox.Show(this, ex.Message, "Installation Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // Never wait forever — hung winget/WUA (e.g. Store install) used to keep _busy true.
            if (outstandingWork.Count > 0)
            {
                try
                {
                    await DrainOutstandingWithTimeoutAsync(
                        outstandingWork,
                        TimeSpan.FromSeconds(20),
                        manager,
                        norwegian
                            ? "Bakgrunnsjobb tidsavbrutt — UI låses opp. Kjør Check for Updates senere."
                            : "Background work timed out — unlocking UI. Run Check for Updates later.");
                }
                catch { /* never block unlock */ }
            }
            _busy = false;
            InstallButtonActive = false;
            ResetInstallButtonLabel();
            ScanButton.IsEnabled = AppsScanButton.IsEnabled = true;
            SetHistoryActionsEnabled(true);
            UpdateSelectionControls();
            RefreshRebootNeededBanner();
        }
        // Already confirmed in the combined dialog. Runs after the installs so OEM installer
        // windows (and any restart they trigger) do not interrupt Installation Manager.
        await InstallSelectedOemAsync(oem, confirm: false);
    }

    private void ScheduleBackgroundWingetRefresh()
    {
        _ = RefreshWingetInBackgroundAsync();
    }

    private async Task RefreshWingetInBackgroundAsync()
    {
        try
        {
            var scan = await WingetReader.ScanAsync().ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (_busy) return;
                    WingetAvailableCache.Write(scan.Updates, scan.Raw);
                    ApplyAppScanResult(scan.Updates, scan.Raw);
                    ReconcileApplicationsPromptWithScan(scan.Updates);
                    if (_activeTab is "all" or "apps") SetTab(_activeTab);
                    RefreshHealthSummary();
                }
                catch (Exception ex) { LogStatic("WinGet", "Background refresh apply failed", ex.Message); }
            });
        }
        catch (Exception ex) { LogStatic("WinGet", "Background refresh failed", ex.Message); }
    }

    private static async Task DrainOutstandingWithTimeoutAsync(
        IReadOnlyList<Task> tasks,
        TimeSpan limit,
        InstallationManagerWindow manager,
        string timeoutStage)
    {
        var drain = Task.WhenAll(tasks.Select(DrainTaskAsync));
        var winner = await Task.WhenAny(drain, Task.Delay(limit)).ConfigureAwait(true);
        if (winner != drain)
        {
            try { manager.SetStage(timeoutStage); } catch { /* window may be closed */ }
            LogStatic("InstallationManager", "Drain timed out", new { Seconds = limit.TotalSeconds, Count = tasks.Count });
        }
        else
        {
            try { await drain.ConfigureAwait(true); } catch { /* individual drains log */ }
        }
    }

    private static void LogStatic(string source, string status, object detail)
    {
        try { ActivityLog.TryWrite(source, status, detail); } catch { /* ignore */ }
    }

    /// <summary>
    /// Results stays open by default when updates are pending or a successful scan
    /// found none (header status is visible either way); never force-collapse.
    /// </summary>
    private void SyncResultsExpandedForPending(int pendingCount)
    {
        if (ResultsExpander is null) return;
        if (pendingCount >= 1 || IsFullyUpToDate())
            ResultsExpander.IsExpanded = true;
        ApplyResultsRowLayout();
    }

    private int CountAvailableUpdates()
    {
        int windows = _pendingSnapshot.Count(x => x.Category != "Driver / firmware" &&
            (!string.IsNullOrEmpty(x.UpdateId) || x.Source == FeatureUpdateOfferReader.Source));
        int drivers = _pendingSnapshot.Count(x => x.Category == "Driver / firmware" && !string.IsNullOrEmpty(x.UpdateId));
        return windows + drivers + _appEntries.Count;
    }

    private bool IsFullyUpToDate() =>
        _lastSuccessfulScan is not null && !_showingHistory && CountAvailableUpdates() == 0;

    private bool IsOutOfDateAfterScan() =>
        _lastSuccessfulScan is not null && !_showingHistory && CountAvailableUpdates() > 0;

    /// <summary>
    /// Header status: one PNG (up-to-date or not) + two-line text left of Check for updates.
    /// </summary>
    private void RefreshUpdateStatusPanel()
    {
        if (UpdateStatusPanel is null || UpdateStatusTitle is null || UpdateStatusChecked is null) return;
        bool upToDate = IsFullyUpToDate();
        bool notUpToDate = IsOutOfDateAfterScan();
        bool show = upToDate || notUpToDate;
        UpdateStatusPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (UpdateStatusIconUpToDate is not null)
            UpdateStatusIconUpToDate.Visibility = upToDate ? Visibility.Visible : Visibility.Collapsed;
        if (UpdateStatusIconNotUpToDate is not null)
            UpdateStatusIconNotUpToDate.Visibility = notUpToDate ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        bool norwegian = _schedule.Language == "no";
        var when = _lastSuccessfulScan!.Value;
        var local = when.Kind == DateTimeKind.Utc ? when.ToLocalTime() : when;
        bool today = local.Date == DateTime.Now.Date;
        if (upToDate)
        {
            UpdateStatusTitle.Text = norwegian ? "Du er oppdatert" : "You're up to date";
            UpdateStatusChecked.Text = norwegian
                ? (today
                    ? $"Sist sjekket i dag: {local:HH:mm}"
                    : $"Sist sjekket: {local:dd.MM.yyyy} {local:HH:mm}")
                : (today
                    ? $"Last checked today: {local:HH:mm}"
                    : $"Last checked: {local:dd.MM.yyyy} {local:HH:mm}");
        }
        else
        {
            UpdateStatusTitle.Text = norwegian ? "Du er ikke oppdatert" : "You're not up to date";
            UpdateStatusChecked.Text = norwegian
                ? $"Sist sjekket: {local:dd.MM.yyyy} {local:HH:mm}"
                : $"Last checked: {local:dd.MM.yyyy} {local:HH:mm}";
        }
    }

    /// <summary>
    /// Keep Results grid row Auto when collapsed (no tall empty star gap on first paint)
    /// and Star when expanded so the results table can stretch.
    /// </summary>
    private void ApplyResultsRowLayout()
    {
        if (ResultsRow is null || ResultsExpander is null) return;
        ResultsRow.Height = ResultsExpander.IsExpanded
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;
    }

    private void ResultsExpander_Expanded(object sender, RoutedEventArgs e) => ApplyResultsRowLayout();

    private void ResultsExpander_Collapsed(object sender, RoutedEventArgs e) => ApplyResultsRowLayout();

    private void ResultsExpander_SizeChanged(object sender, SizeChangedEventArgs e) => StretchResultsHeader();

    /// <summary>
    /// On expand, scroll MainScroll so «Vedlikehold og varsler» sits near the top of the viewport.
    /// Collapse leaves scroll position unchanged.
    /// </summary>
    private void MaintenanceExpander_Expanded(object sender, RoutedEventArgs e)
    {
        // Nested expanders (e.g. Advanced categories) also bubble Expanded here — ignore those.
        if (!ReferenceEquals(e.OriginalSource, MaintenanceExpander))
            return;

        // Wait until expanded content has measured/arranged, then scroll.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, ScrollMaintenanceSectionToTop);
        });
    }

    private void ScrollMaintenanceSectionToTop()
    {
        if (MainScroll is null || MaintenanceExpander is null || !MaintenanceExpander.IsExpanded)
            return;

        FrameworkElement target = MaintenanceSection ?? (FrameworkElement)MaintenanceExpander;
        target.UpdateLayout();
        MainScroll.UpdateLayout();

        if (!ReferenceEquals(target, MainScroll) && !IsAncestor(MainScroll, target))
            return;

        System.Windows.Point relative = target.TransformToAncestor(MainScroll).Transform(new System.Windows.Point(0, 0));
        double offset = MainScroll.VerticalOffset + relative.Y;
        if (offset < 0) offset = 0;
        MainScroll.ScrollToVerticalOffset(offset);
    }

    private static bool IsAncestor(DependencyObject ancestor, DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

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
                ReconcileWindowsPromptsWithScan(pending);

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

                var queue = matches.Select(entry => new InstallationQueueItem("Windows Update", entry.Title) { UpdateId = entry.UpdateId }).ToList();
                string subtitle = norwegian
                    ? "Oppdater til ferdig. Windows starter på nytt når en runde krever det, og søket kjøres på nytt etterpå."
                    : "Update until current. Windows restarts when a round requires it, then the search runs again.";
                var manager = new InstallationManagerWindow(queue, subtitle, norwegian) { Owner = this };
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
                    var outcome = manager.Finish(norwegian);
                    SetInstallOutcome(outcome.Completed, outcome.Failed, queue);
                }
                catch (Exception ex)
                {
                    try
                    {
                        var outcome = manager.Finish();
                        SetInstallOutcome(outcome.Completed, outcome.Failed, queue);
                        manager.SetStage(ex.Message);
                    }
                    catch { }
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
                : "Update Express continues the update cycle after restart.",
            showUi: true,
            owner: this);
    }

    private void StretchResultsHeader()
    {
        if (ResultsHeaderBar == null) return;
        // Do not force an explicit Width — that overlapped the fixed 28px chevron column
        // (gear appeared to push the Results chevron left). Let the header fill naturally.
        if (!double.IsNaN(ResultsHeaderBar.Width))
            ResultsHeaderBar.Width = double.NaN;
    }

    private void ToolsGear_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Keep the section Expander from toggling when opening the tools menu.
        e.Handled = true;
        ToolsGear_Click(sender, e);
    }

    private void ScanButton_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Keep the section Expander from toggling when checking for updates from the header.
        e.Handled = true;
        Scan_Click(sender, e);
    }

    private void ToolsGear_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not System.Windows.Controls.Button btn || btn.ContextMenu is null) return;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        btn.ContextMenu.IsOpen = true;
    }

    private void ThisPc_Click(object sender, RoutedEventArgs e)
    {
        // Keep the inline strip collapsed; open the detailed PC window instead.
        if (MachineIdentityPanel is not null)
            MachineIdentityPanel.Visibility = Visibility.Collapsed;

        bool norwegian = _schedule.Language == "no";
        RefreshUptime();
        var os = WindowsSupportReader.Read();
        string build = WindowsBuildNumber();
        var network = LocalNetworkReader.Read();
        var dlg = new PcInfoWindow(
            norwegian,
            _machineIdentity,
            os.Name,
            build,
            UptimeValue?.Text ?? "—",
            UptimeValue?.ToolTip as string ?? UptimeCopyText(),
            network)
        {
            Owner = this
        };
        dlg.ShowDialog();
        RefreshThisPcButton();
    }

    private static string WindowsBuildNumber()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            int build = Environment.OSVersion.Version.Build;
            string ubr = key?.GetValue("UBR")?.ToString()?.Trim() ?? "";
            string current = key?.GetValue("CurrentBuildNumber")?.ToString()?.Trim()
                             ?? key?.GetValue("CurrentBuild")?.ToString()?.Trim()
                             ?? build.ToString();
            return string.IsNullOrWhiteSpace(ubr) ? current : $"{current}.{ubr}";
        }
        catch
        {
            return Environment.OSVersion.Version.Build.ToString();
        }
    }

    private void RefreshThisPcButton()
    {
        if (ThisPcButton is null) return;
        bool no = _schedule.Language == "no";
        const string english = "This PC";
        string label = no && Norwegian.TryGetValue(english, out var translated) ? translated : english;
        if (ThisPcLabel is not null)
        {
            // Tag keeps the English key so Translate() resolves the same text.
            ThisPcLabel.Tag = english;
            ThisPcLabel.Text = label;
        }
        ThisPcButton.Tag = english;
        ThisPcButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(ThisPcButton, label);
    }

    private double _thisPcLabelWidth;
    private string _thisPcLabelMeasuredText = "";

    /// <summary>
    /// Header columns are brand (*) | status (Auto) | actions (Auto), so the brand is what clips
    /// when the row is too narrow. Drop the "This PC" label (the icon stays) whenever it would
    /// cut into the logo + "Update Express" title, e.g. near the minimum window width.
    /// The result does not depend on whether the label is currently shown, so it cannot oscillate.
    /// </summary>
    private void UpdateThisPcLabelVisibility()
    {
        if (ThisPcLabel is null || HeaderGrid is null || BrandLogo is null || BrandTitleRow is null) return;
        if (HeaderGrid.ActualWidth <= 0) return;
        bool shown = ThisPcLabel.Visibility == Visibility.Visible;
        if (shown)
        {
            if (ThisPcLabel.DesiredSize.Width <= 0) return; // not measured yet
            _thisPcLabelWidth = ThisPcLabel.DesiredSize.Width;
            _thisPcLabelMeasuredText = ThisPcLabel.Text;
        }
        else if (_thisPcLabelMeasuredText != ThisPcLabel.Text)
        {
            // Language changed while hidden: show once so the new text gets measured.
            ThisPcLabel.Visibility = Visibility.Visible;
            return;
        }

        var columns = HeaderGrid.ColumnDefinitions;
        double brandRoomWithLabel = HeaderGrid.ActualWidth - columns[1].ActualWidth - columns[2].ActualWidth
            - (shown ? 0 : _thisPcLabelWidth);
        double brandNeeds = BrandLogo.DesiredSize.Width + BrandTitleRow.DesiredSize.Width;
        // Half a pixel of hysteresis so rounding at the exact boundary cannot flip the label back and forth.
        bool fits = brandRoomWithLabel >= brandNeeds - (shown ? 0.5 : 0);
        if (fits != shown)
            ThisPcLabel.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
    }

    private ManufacturerSupportLinks.SupportLink? _manufacturerSupport;

    private void RefreshManufacturerSupportButton()
    {
        if (ManufacturerSupportButton is null) return;
        bool norwegian = _schedule.Language == "no";
        if (!ManufacturerSupportLinks.TryResolve(
                _machineIdentity.Manufacturer,
                _machineIdentity.Product,
                _machineIdentity.Model,
                _machineIdentity.Serial,
                out var link) || link is null)
        {
            _manufacturerSupport = null;
            ManufacturerSupportButton.Visibility = Visibility.Collapsed;
            return;
        }

        _manufacturerSupport = link;
        // Brand + "support" kept in both languages (matches SVHA wording / OEM site language).
        string label = $"{link.Brand} support";
        ManufacturerSupportButton.Content = label;
        ManufacturerSupportButton.Tag = label;
        ManufacturerSupportButton.ToolTip = norwegian
            ? $"Åpne {link.Brand}-støtte i nettleseren"
            : $"Open {link.Brand} support in your browser";
        System.Windows.Automation.AutomationProperties.SetName(ManufacturerSupportButton, label);
        ManufacturerSupportButton.Visibility = Visibility.Visible;
    }

    private void ManufacturerSupport_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var link = _manufacturerSupport;
        if (link is null &&
            !ManufacturerSupportLinks.TryResolve(
                _machineIdentity.Manufacturer,
                _machineIdentity.Product,
                _machineIdentity.Model,
                _machineIdentity.Serial,
                out link))
            return;
        if (link is null || string.IsNullOrWhiteSpace(link.Url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = link.Url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            bool norwegian = _schedule.Language == "no";
            MessageBox.Show(
                this,
                ex.Message,
                norwegian ? "Åpne støtte" : "Open support",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void BrandSiteLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://www.microsystems.one",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open website", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
