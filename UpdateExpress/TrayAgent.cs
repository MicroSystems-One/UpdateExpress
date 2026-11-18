using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows.Forms;
using UpdateExpress.Services;
namespace UpdateExpress;

// User-session tray agent. WUA monitoring is read-only. App self-update can download+replace after confirm.
internal sealed class TrayAgent : IDisposable
{
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _scanNow;
    private readonly ToolStripMenuItem _checkAppUpdate;
    private readonly ToolStripMenuItem _settingsItem;
    private SettingsWindow? _settings;
    private readonly System.Threading.Timer _timer;
    private readonly System.Threading.Timer _defenderTimer;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly string _baselinePath = Path.Combine(InstallationManager.InstallDir, "monitor-initialized.flag");
    private readonly string _statePath = Path.Combine(InstallationManager.InstallDir, "monitor-state.txt");
    private readonly TimeSpan _interval = TimeSpan.FromHours(6);
    private readonly string _statusPath = Path.Combine(InstallationManager.InstallDir, "monitor-status.json");
    private DateTime _nextScheduled = DateTime.Now.AddMinutes(5);
    private bool _disposed;
    private int _scanRunning;

    public TrayAgent()
    {
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Åpne Update Express", null, (_, _) => Open());
        _scanNow = new ToolStripMenuItem("Sjekk Windows-oppdateringer", null, (_, _) => StartScan());
        menu.Items.Add(_scanNow);
        _checkAppUpdate = new ToolStripMenuItem("Sjekk Update Express-oppdatering", null, (_, _) => StartAppUpdateCheck(automatic: false));
        menu.Items.Add(_checkAppUpdate);
        _status = new ToolStripMenuItem("Overvåking: venter på første kontroll") { Enabled = false };
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        _settingsItem = new ToolStripMenuItem(ReminderSchedule.Load().Language == "no" ? "Innstillinger" : "Settings", null, (_, _) => ShowSettings());
        menu.Items.Add(_settingsItem);
        menu.Items.Add("Proxy Settings...", null, (_, _) => Open("--proxy-settings"));
        menu.Items.Add("Vedlikehold", null, (_, _) => Open("--maintenance"));
        menu.Items.Add("Avslutt bakgrunnsagent", null, (_, _) => _dispatcher.BeginInvoke(new Action(() => System.Windows.Application.Current.Shutdown())));
        _tray = new NotifyIcon
        {
            Text = "Update Express – MicroSystems",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Open();
        };
        // Avoid an expensive WUA scan during Windows sign-in. Scan after 5 minutes,
        // then at most every six hours. Manual scans use the same overlap guard.
        _timer = new System.Threading.Timer(_ => { _nextScheduled = DateTime.Now.Add(_interval); StartScan(); }, null, TimeSpan.FromMinutes(5), _interval);
        // Categories 1–3 plus optional drivers/apps: poll often; each job decides whether it is due.
        Action<string, string> balloon = (title, text) => ShowAppBalloon(title, text, ToolTipIcon.Info);
        CriticalAutomation.SetBalloon(balloon);
        DriverAutomation.SetBalloon(balloon);
        ApplicationAutomation.SetBalloon(balloon);
        _defenderTimer = new System.Threading.Timer(_ =>
        {
            _ = DefenderAutomation.RunDueJobAsync();
            _ = CriticalAutomation.RunDueJobAsync(_dispatcher);
            _ = GeneralAutomation.RunDueJobAsync(_dispatcher);
            _ = DriverAutomation.RunDueJobAsync(_dispatcher);
            _ = ApplicationAutomation.RunDueJobAsync(_dispatcher);
            try { _dispatcher.BeginInvoke(new Action(() => RebootCoordinator.RunDueJob(allowUiPrompt: true))); }
            catch (Exception ex) { AgentLog.Write("Deferred reboot poll failed: " + ex.Message); }
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
        WriteMonitorStatus("waiting", 0, 0, null, null);
        AgentLog.Write("Tray agent started; WUA monitor every 6h; Defender+Critical+General+Drivers+Apps+reboot poll every 5m.");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45));
                if (_disposed) return;
                _ = _dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_disposed) return;
                        if (UpdateUntilCurrent.TryLaunchResume())
                            ShowAppBalloon(
                                "Update until current",
                                "The cycle continues after the restart. Approve the administrator prompt.",
                                ToolTipIcon.Info);
                    }
                    catch (Exception ex) { AgentLog.Write("Update until current resume failed: " + ex.Message); }
                }));
            }
            catch (Exception ex) { AgentLog.Write("Update until current resume failed: " + ex.Message); }
        });
        // After sign-in / agent start: quiet Update Express self-update check (prompt only if newer).
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                if (!_disposed) StartAppUpdateCheck(automatic: true);
            }
            catch (Exception ex) { AgentLog.Write("Automatic app update check schedule failed: " + ex.Message); }
        });
    }

    private void StartScan()
    {
        if (_disposed || Interlocked.CompareExchange(ref _scanRunning, 1, 0) != 0) return;
        SetStatus("Overvåking: søker …", false);
        WriteMonitorStatus("scanning", 0, 0, null, null);
        _ = Task.Run(async () =>
        {
            try
            {
                // COM WUA is always used on its own STA thread.
                var pending = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending());
                var ids = pending.Select(x => string.IsNullOrWhiteSpace(x.UpdateId)
                    ? x.KB + "|" + x.Title : x.UpdateId).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool hasBaseline = File.Exists(_baselinePath);
                var previous = ReadState();
                var fresh = ids.Except(previous, StringComparer.OrdinalIgnoreCase).Count();
                SaveState(ids);
                try { File.WriteAllText(_baselinePath, "initialized"); } catch (Exception ex) { AgentLog.Write("Cannot persist monitoring baseline: " + ex.Message); }
                var completed = DateTime.Now;
                SetStatus($"Sist sjekket: {completed:HH:mm} · {pending.Count} tilgjengelige", true);
                WriteMonitorStatus("completed", pending.Count, hasBaseline ? fresh : 0, completed, null,
                    pending.Select(x => $"{x.KB} {x.Title}".Trim()).Take(20).ToArray());
                AgentLog.Write($"Background scan completed: {pending.Count} pending, {fresh} new compared with previous scan.");
                // The first scan establishes a baseline; no surprise balloon on first run.
                if (hasBaseline && fresh > 0)
                    ShowNotification(fresh, pending.Count);
            }
            catch (Exception ex)
            {
                AgentLog.Write("Background WUA scan failed: " + ex);
                SetStatus("Overvåking: søk feilet (se agent.log)", true);
                WriteMonitorStatus("failed", 0, 0, null, ex.Message);
            }
            finally { Interlocked.Exchange(ref _scanRunning, 0); }
        });
    }

    private void StartAppUpdateCheck(bool automatic)
    {
        if (_disposed) return;
        SetAppUpdateMenuEnabled(false);
        AppUpdatePrompt.StartCheck(
            automatic,
            onUpdateInstallRequested: StartAppUpdateInstall,
            balloon: (text, icon) => ShowAppBalloon("Update Express", text, icon));
        // Re-enable menu shortly when not installing; install path keeps it disabled until failure/shutdown.
        if (!automatic)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                if (!_disposed) SetAppUpdateMenuEnabled(true);
            });
        }
        else
            SetAppUpdateMenuEnabled(true);
    }

    private void StartAppUpdateInstall(AppUpdateCheckResult offer)
    {
        SetAppUpdateMenuEnabled(false);
        // Stages are logged only. A balloon per stage is queued by Windows and keeps
        // appearing after the replacement has already started the new version.
        _ = Task.Run(async () =>
        {
            try
            {
                await AppUpdateInstaller.PrepareAndLaunchAsync(offer, stage =>
                {
                    AgentLog.Write("App update: " + stage);
                }).ConfigureAwait(false);
                // LaunchSelfUpdate shuts down this process on success.
            }
            catch (Exception ex)
            {
                AgentLog.Write("App update install failed: " + ex);
                AppUpdatePrompt.ReleaseCheckLock();
                try
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        ShowAppBalloon("Update Express", "Update installation failed.", ToolTipIcon.Error);
                        System.Windows.Forms.MessageBox.Show(
                            "Could not install the Update Express update.\n\n" + ex.Message +
                            "\n\nThe installed version was not changed.",
                            "Update Express",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    });
                }
                catch (Exception uiEx) { AgentLog.Write("Cannot show update failure UI: " + uiEx.Message); }
                finally
                {
                    SetAppUpdateMenuEnabled(true);
                }
            }
        });
    }

    private static Icon LoadTrayIcon()
    {
        // Prefer the embedded U icon so toast/tray notifications do not reuse a cached M icon.
        try
        {
            var embedded = AppIcon.TryLoadDrawingIcon();
            if (embedded is not null) return embedded;
        }
        catch (Exception ex) { AgentLog.Write("Embedded tray icon load failed: " + ex.Message); }
        try
        {
            string path = Environment.ProcessPath ?? "";
            return Icon.ExtractAssociatedIcon(path) ?? SystemIcons.Application;
        }
        catch { return SystemIcons.Application; }
    }

    private void ShowAppBalloon(string title, string text, ToolTipIcon icon)
    {
        try
        {
            if (_disposed) return;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(8000);
        }
        catch (Exception ex) { AgentLog.Write("Cannot show app-update balloon: " + ex.Message); }
    }

    private void SetAppUpdateMenuEnabled(bool enabled)
    {
        try
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;
                _checkAppUpdate.Enabled = enabled;
            }));
        }
        catch (Exception ex) { AgentLog.Write("Cannot update app-update menu state: " + ex.Message); }
    }

    private void WriteMonitorStatus(string state, int pending, int fresh, DateTime? completed, string? error, string[]? updates = null)
    {
        try
        {
            Directory.CreateDirectory(InstallationManager.InstallDir);
            // Keep last completed scan while another scan is running.
            MonitorStatus? previous = MonitorStatus.Read();
            var data = new MonitorStatus
            {
                State = state, Pending = state == "completed" ? pending : previous?.Pending ?? 0,
                New = state == "completed" ? fresh : previous?.New ?? 0,
                LastCompleted = completed ?? previous?.LastCompleted,
                Started = state == "scanning" ? DateTime.Now : null,
                NextScheduled = _nextScheduled,
                Error = error,
                Updates = updates ?? previous?.Updates ?? Array.Empty<string>()
            };
            string temp = _statusPath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            File.Move(temp, _statusPath, true);
        }
        catch (Exception ex) { AgentLog.Write("Monitor status write failed: " + ex.Message); }
    }

    private System.Collections.Generic.HashSet<string> ReadState()
    {
        try { return File.Exists(_statePath)
            ? File.ReadAllLines(_statePath).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) { AgentLog.Write("Cannot read monitor state: " + ex.Message); return new(StringComparer.OrdinalIgnoreCase); }
    }
    private void SaveState(System.Collections.Generic.HashSet<string> ids)
    {
        try
        {
            Directory.CreateDirectory(InstallationManager.InstallDir);
            string temp = _statePath + ".tmp";
            File.WriteAllLines(temp, ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            File.Move(temp, _statePath, true);
        }
        catch (Exception ex) { AgentLog.Write("Cannot save monitor state: " + ex.Message); }
    }
    private void SetStatus(string message, bool allowScan)
    {
        try { _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            _status.Text = message;
            _scanNow.Enabled = allowScan;
        })); }
        catch (Exception ex) { AgentLog.Write("Cannot update tray status: " + ex.Message); }
    }
    private void ShowNotification(int fresh, int total)
    {
        try { _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            _tray.BalloonTipTitle = "Update Express – nye oppdateringer";
            _tray.BalloonTipText = $"{fresh} nye oppdateringer registrert ({total} totalt). Åpne Update Express for å vurdere dem.";
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(8000);
        })); }
        catch (Exception ex) { AgentLog.Write("Cannot show update notification: " + ex.Message); }
    }
    private void ShowSettings()
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (_settings is { IsVisible: true })
            {
                _settings.Activate();
                return;
            }
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) =>
            {
                _settings = null;
                if (_disposed) return;
                _settingsItem.Text = ReminderSchedule.Load().Language == "no" ? "Innstillinger" : "Settings";
            };
            _settings.Show();
            _settings.Activate();
        }));
    }
    private void Open(string args = "--installed")
    {
        string path = InstallationManager.IsInstalled ? InstallationManager.InstalledExe : (Environment.ProcessPath ?? "");
        try
        {
            // Activate an existing UI instead of spawning multiple windows.
            var current = Process.GetCurrentProcess();
            var existing = Process.GetProcessesByName("UpdateExpress")
                .FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);
            if (existing is not null && args == "--installed")
            {
                ShowWindow(existing.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(existing.MainWindowHandle);
                existing.Dispose();
                return;
            }
            existing?.Dispose();
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = true });
            else AgentLog.Write("Cannot open UI; executable missing: " + path);
        }
        catch (Exception ex) { AgentLog.Write("Cannot open UI: " + ex); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _defenderTimer.Dispose();
        AgentLog.Write("Agent stopped.");
        _tray.Visible = false;
        _tray.Dispose();
    }
}
