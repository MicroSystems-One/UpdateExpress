using System;
using System.Linq;
using System.Threading;
using System.Windows;
using UpdateExpress.Services;
namespace UpdateExpress;
public partial class App : System.Windows.Application
{
    private TrayAgent? _agent;
    private System.Threading.Mutex? _agentMutex;
    private bool _ownsAgentMutex;
    private CancellationTokenSource? _upgradeShutdownCts;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        if (args.Contains("--apply-maintenance", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            InstallationManager.ApplyMaintenance(args);
            Shutdown();
            return;
        }
        if (args.Contains("--apply-defender-exclusions", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--remove-defender-exclusions", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DefenderExclusions.ApplyFromArgs(args);
            Shutdown();
            return;
        }
        if (args.Contains("--apply-critical-install", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--apply-general-install", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--apply-drivers-install", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--apply-applications-install", StringComparer.OrdinalIgnoreCase))
        {
            // Elevated helpers must read the tray user's prompt/queue, not another admin's LocalAppData.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AutomationStatePaths.TryApplyFromArgs(args);
            if (args.Contains("--apply-critical-install", StringComparer.OrdinalIgnoreCase))
                CriticalAutomation.ApplyQueuedInstall();
            else if (args.Contains("--apply-general-install", StringComparer.OrdinalIgnoreCase))
                GeneralAutomation.ApplyQueuedInstall();
            else if (args.Contains("--apply-drivers-install", StringComparer.OrdinalIgnoreCase))
                DriverAutomation.ApplyQueuedInstall();
            else
                ApplicationAutomation.ApplyQueuedInstall();
            Shutdown();
            return;
        }
        if (args.Contains("--agent", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                AgentLog.WriteStartupDiagnostics("agent");
                _agentMutex = new System.Threading.Mutex(true, @"Local\MicroSystems.UpdateExpress.TrayAgent", out bool createdNew);
                _ownsAgentMutex = createdNew;
                if (!createdNew)
                {
                    AgentLog.WriteInstall("Agent already running; second instance exits.");
                    Shutdown();
                    return;
                }
                _agent = new TrayAgent();
                try { System.IO.File.WriteAllText(InstallationManager.AgentPidFile, Environment.ProcessId.ToString()); }
                catch (Exception ex) { AgentLog.WriteInstall("Agent PID registration failed: " + ex.Message); }
                AgentLog.WriteInstall("Agent started (tray visible).");
            }
            catch (Exception ex)
            {
                AgentLog.WriteInstall("Agent startup failed: " + ex);
                Shutdown();
            }
            return;
        }
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool quiet = args.Contains("--silent", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--quiet", StringComparer.OrdinalIgnoreCase)
                || string.Equals(Environment.GetEnvironmentVariable("UE_SILENT"), "1", StringComparison.OrdinalIgnoreCase);
            InstallationManager.Uninstall(silent: quiet);
            Shutdown();
            return;
        }
        if (args.Contains("--finalize-shortcuts", StringComparer.OrdinalIgnoreCase))
        {
            // RMM: after silent install + policy seed — create deferred shortcuts and optionally start tray.
            // Args: --finalize-shortcuts <user|machine> <desktop> <startMenu> <agent> <taskbar> [--silent]
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                FinalizeShortcutsFromArgs(args);
            }
            catch (Exception ex)
            {
                AgentLog.WriteInstall("Finalize shortcuts failed: " + ex);
                Environment.ExitCode = 1;
            }
            Shutdown();
            return;
        }
        // The normal interactive application needs an elevated token for WUA installation.
        // Elevate before scanning or showing the main window. Background agent, uninstall,
        // maintenance and launcher modes above intentionally remain unchanged.
        bool openMainWindow = args.Contains("--installed", StringComparer.OrdinalIgnoreCase)
            || (!args.Contains("--maintenance", StringComparer.OrdinalIgnoreCase)
                && InstallationManager.RunningInstalledCopy);
        if (openMainWindow && !ElevationHelper.IsElevated)
        {
            try
            {
                ElevationHelper.RestartElevated(args.Contains("--proxy-settings", StringComparer.OrdinalIgnoreCase)
                    ? "--installed --proxy-settings" : "--installed");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // UAC cancelled: do not open a non-elevated update manager.
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not request administrator privileges: " + ex.Message,
                    "Update Express", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown();
            return;
        }
        bool launcher = args.Contains("--maintenance", StringComparer.OrdinalIgnoreCase) ||
            (!args.Contains("--installed", StringComparer.OrdinalIgnoreCase) && !InstallationManager.RunningInstalledCopy);
        if (!launcher)
        {
            // Setup must complete before the main UI (and before tray is required for a fresh install).
            try { RunSetupWizardIfNeeded(); }
            catch (Exception ex) { AgentLog.WriteInstall("Startup setup wizard failed: " + ex.Message); }
        }
        System.Windows.Window window = launcher ? new LauncherWindow() : new MainWindow();
        string mode = window is MainWindow ? "main-ui" : "launcher";
        AgentLog.WriteStartupDiagnostics(mode);
        // Self-update helper may have stalled on Defender UAC before restarting tray — heal here.
        try { InstallationManager.EnsureBackgroundAgentRunning(); }
        catch (Exception ex) { AgentLog.WriteInstall("Startup EnsureBackgroundAgentRunning failed: " + ex.Message); }
        MainWindow = window;
        window.Show();
        if (window is MainWindow)
        {
            // Tray self-update (medium IL) signals this elevated UI to close on confirm.
            _upgradeShutdownCts = UpgradeShutdown.Listen(() =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AgentLog.WriteInstall("Closing Update Express UI after upgrade confirmation.");
                        Shutdown();
                    }));
                }
                catch (Exception ex) { AgentLog.WriteInstall("Could not schedule UI shutdown for upgrade: " + ex.Message); }
            });
        }
        if (args.Contains("--proxy-settings", StringComparer.OrdinalIgnoreCase) && window is MainWindow main)
            main.Dispatcher.BeginInvoke(new Action(main.ShowProxySettings));
    }

    private static void RunSetupWizardIfNeeded()
    {
        var policy = UpdatePolicy.Load();
        policy.Normalize();
        if (policy.SetupCompleted) return;
        var app = System.Windows.Application.Current;
        // Wizard is the only window until MainWindow is created — keep the process
        // alive when the dialog closes (default OnLastWindowClose would shut us down).
        var previous = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var wizard = new SetupWizardWindow(owner: null);
            wizard.ShowDialog();
        }
        finally
        {
            app.ShutdownMode = previous;
        }
        try { InstallationManager.EnsureBackgroundAgentRunning(); }
        catch (Exception ex) { AgentLog.WriteInstall("Post-setup agent ensure failed: " + ex.Message); }
    }

    private static void FinalizeShortcutsFromArgs(string[] args)
    {
        // --finalize-shortcuts <scope> <desktop> <startMenu> <agent> <taskbar>
        int idx = Array.FindIndex(args, a => string.Equals(a, "--finalize-shortcuts", StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || args.Length < idx + 5)
            throw new ArgumentException("Usage: --finalize-shortcuts <user|machine> <desktop 0|1> <startMenu 0|1> <agent 0|1> <taskbar 0|1>");
        bool machine = string.Equals(args[idx + 1], "machine", StringComparison.OrdinalIgnoreCase);
        bool desktop = args[idx + 2] == "1";
        bool startMenu = args[idx + 3] == "1";
        bool agent = args[idx + 4] == "1";
        bool taskbar = args.Length > idx + 5 && args[idx + 5] == "1";
        InstallationManager.ApplyDeferredShortcuts(desktop, startMenu, taskbar, machine);
        if (agent && !machine)
        {
            string exe = machine ? InstallationManager.MachineExe : InstallationManager.UserExe;
            if (!machine)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                key?.SetValue("MicroSystems Update Express Agent", $"\"{exe}\" --agent");
            }
            InstallationManager.EnsureBackgroundAgentRunning(exe, waitForPid: true);
        }
        AgentLog.WriteInstall($"Finalize shortcuts done: desktop={desktop}; startMenu={startMenu}; agent={agent}; taskbar={taskbar}");
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try { _upgradeShutdownCts?.Cancel(); } catch { }
        _upgradeShutdownCts?.Dispose();
        _upgradeShutdownCts = null;
        _agent?.Dispose();
        if (_agent is not null)
        {
            try
            {
                if (System.IO.File.Exists(InstallationManager.AgentPidFile) &&
                    System.IO.File.ReadAllText(InstallationManager.AgentPidFile) == Environment.ProcessId.ToString())
                    System.IO.File.Delete(InstallationManager.AgentPidFile);
            }
            catch { }
        }
        if (_ownsAgentMutex) { try { _agentMutex?.ReleaseMutex(); } catch (System.ApplicationException) { } }
        _agentMutex?.Dispose();
        base.OnExit(e);
    }
}
