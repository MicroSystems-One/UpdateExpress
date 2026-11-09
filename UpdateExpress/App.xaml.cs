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
        if (args.Contains("--apply-critical-install", StringComparer.OrdinalIgnoreCase))
        {
            // Elevated helper for Category 2: install queued Critical/Important updates only.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CriticalAutomation.ApplyQueuedInstall();
            Shutdown();
            return;
        }
        if (args.Contains("--apply-general-install", StringComparer.OrdinalIgnoreCase))
        {
            // Elevated helper for Category 3: install queued general software updates only.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            GeneralAutomation.ApplyQueuedInstall();
            Shutdown();
            return;
        }
        if (args.Contains("--agent", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                _agentMutex = new System.Threading.Mutex(true, @"Local\MicroSystems.UpdateExpress.TrayAgent", out bool createdNew);
                _ownsAgentMutex = createdNew;
                if (!createdNew)
                {
                    AgentLog.Write("Agent already running; second instance exits.");
                    Shutdown();
                    return;
                }
                _agent = new TrayAgent();
                try { System.IO.File.WriteAllText(InstallationManager.AgentPidFile, Environment.ProcessId.ToString()); }
                catch (Exception ex) { AgentLog.Write("Agent PID registration failed: " + ex.Message); }
                AgentLog.Write("Agent started.");
            }
            catch (Exception ex)
            {
                AgentLog.Write("Agent startup failed: " + ex);
                Shutdown();
            }
            return;
        }
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            InstallationManager.Uninstall();
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
        System.Windows.Window window = args.Contains("--maintenance", StringComparer.OrdinalIgnoreCase) ||
            (!args.Contains("--installed", StringComparer.OrdinalIgnoreCase) && !InstallationManager.RunningInstalledCopy)
            ? new LauncherWindow() : new MainWindow();
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
                        AgentLog.Write("Closing Update Express UI after upgrade confirmation.");
                        Shutdown();
                    }));
                }
                catch (Exception ex) { AgentLog.Write("Could not schedule UI shutdown for upgrade: " + ex.Message); }
            });
        }
        if (args.Contains("--proxy-settings", StringComparer.OrdinalIgnoreCase) && window is MainWindow main)
            main.Dispatcher.BeginInvoke(new Action(main.ShowProxySettings));
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
