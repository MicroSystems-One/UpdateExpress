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

// User-session tray agent. Monitoring is read-only: never downloads or installs updates.
internal sealed class TrayAgent : IDisposable
{
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _scanNow;
    private readonly System.Threading.Timer _timer;
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
        _scanNow = new ToolStripMenuItem("Sjekk etter oppdateringer nå", null, (_, _) => StartScan());
        menu.Items.Add(_scanNow);
        _status = new ToolStripMenuItem("Overvåking: venter på første kontroll") { Enabled = false };
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Innstillinger (kommer senere)", null, (_, _) => Open());
        menu.Items.Add("Proxy Settings...", null, (_, _) => Open("--proxy-settings"));
        menu.Items.Add("Vedlikehold", null, (_, _) => Open("--maintenance"));
        menu.Items.Add("Avslutt bakgrunnsagent", null, (_, _) => _dispatcher.BeginInvoke(new Action(() => System.Windows.Application.Current.Shutdown())));
        var path = Environment.ProcessPath ?? "";
        Icon icon;
        try { icon = Icon.ExtractAssociatedIcon(path) ?? SystemIcons.Application; }
        catch { icon = SystemIcons.Application; }
        _tray = new NotifyIcon { Text = "Update Express – MicroSystems", Icon = icon, Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Open();
        };
        // Avoid an expensive WUA scan during Windows sign-in. Scan after 5 minutes,
        // then at most every six hours. Manual scans use the same overlap guard.
        _timer = new System.Threading.Timer(_ => { _nextScheduled = DateTime.Now.Add(_interval); StartScan(); }, null, TimeSpan.FromMinutes(5), _interval);
        WriteMonitorStatus("waiting", 0, 0, null, null);
        AgentLog.Write("Tray agent started; read-only WUA monitoring every 6h (first scan after 5m).");
    }

    private void StartScan()
    {
        if (_disposed || Interlocked.CompareExchange(ref _scanRunning, 1, 0) != 0) return;
        var scanStarted = DateTime.Now;
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
        AgentLog.Write("Agent stopped.");
        _tray.Visible = false;
        _tray.Dispose();
    }
}
