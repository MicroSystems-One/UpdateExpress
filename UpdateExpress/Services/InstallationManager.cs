using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using UpdateExpress;

namespace UpdateExpress.Services;

internal static class InstallationManager
{
    internal const string AppName = "Update Express";
    internal static readonly string UserInstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicroSystems", "Update Express");
    internal static readonly string MachineInstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MicroSystems", "Update Express");
    internal static string InstallDir => UserInstallDir; // writable per-user agent state, never Program Files
    internal static bool RunningMachineCopy => string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(Path.Combine(MachineInstallDir, "UpdateExpress.exe")), StringComparison.OrdinalIgnoreCase);
    internal static string InstalledExe => RunningMachineCopy ? MachineExe : UserExe;
    internal static string MachineExe => Path.Combine(MachineInstallDir, "UpdateExpress.exe");
    internal static string UserExe => Path.Combine(UserInstallDir, "UpdateExpress.exe");
    private static readonly string StartMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MicroSystems");
    private const string DesktopIconName = "UpdateExpress-U.ico";
    private const string DesktopIconResource = "UpdateExpress.Assets.UpdateExpress.ico";
    private const string ShortcutFileName = "Update Express.lnk";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MicroSystems.UpdateExpress";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "MicroSystems Update Express Agent";

    internal static bool IsInstalled => File.Exists(UserExe) || File.Exists(MachineExe);
    internal static bool RunningInstalledCopy => string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(UserExe), StringComparison.OrdinalIgnoreCase) || RunningMachineCopy;

    // Maintenance is always performed by a detached copy of this executable (different file name).
    // The installed UpdateExpress.exe is never asked to overwrite or delete itself.
    // Prefer staging under the install directory — Microsoft App Control / WDAC often blocks
    // unsigned helpers launched from %TEMP%.
    private const string MaintenanceHelperFileName = "UpdateExpress-Maintenance.exe";
    // ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION — "A Windows App Control policy has blocked this file."
    private const int NativeErrorAppControlBlocked = 4551;
    // ERROR_ACCESS_DISABLED_BY_POLICY — broader policy block (sometimes used instead of 4551).
    private const int NativeErrorAccessDisabledByPolicy = 1260;

    internal static void InstallOrRepair(bool repair)
    {
        var dialog = new InstallationOptionsWindow(repair);
        if (dialog.ShowDialog() != true) return;
        bool allUsers = dialog.AllUsers || dialog.InstallWindowsService;
        if (repair && IsInstalled && allUsers != (RunningMachineCopy || (!File.Exists(UserExe) && File.Exists(MachineExe))))
            throw new InvalidOperationException("Repair must use the existing installation scope. Use Install to change scope.");

        // Persist profile/hours/install window now so SetupCompleted is set before first UI launch
        // (avoids a second Setup wizard that re-asks the same choices).
        try
        {
            MaintenanceSetup.ApplyInstallDialogChoices(
                dialog.SetupProfile,
                dialog.ActiveHoursStart,
                dialog.ActiveHoursEnd,
                dialog.InstallDaysMask,
                dialog.InstallTime,
                dialog.RebootChoice,
                mirrorMachine: allUsers || dialog.InstallWindowsService);
            AgentLog.WriteInstall(
                $"Install options saved: profile={dialog.SetupProfile}; hours={dialog.ActiveHoursStart}-{dialog.ActiveHoursEnd}; " +
                $"days={dialog.InstallDaysMask}; time={dialog.InstallTime}; reboot={dialog.RebootChoice}; service={dialog.InstallWindowsService}; SetupCompleted=true");
        }
        catch (Exception ex)
        {
            AgentLog.WriteInstall("Could not persist install-dialog setup choices: " + ex.Message);
            throw;
        }

        LaunchMaintenance(
            repair ? "repair" : "install",
            allUsers,
            dialog.DesktopShortcut,
            dialog.StartMenuShortcut,
            dialog.BackgroundAgent,
            dialog.TaskbarPin,
            windowsService: dialog.InstallWindowsService);
    }

    internal static void Uninstall(bool silent = false)
    {
        if (!silent &&
            MessageBox.Show("Avinstallere Update Express?\n\nOppdateringshistorikk og innstillinger beholdes.",
                "Update Express", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (silent)
            Environment.SetEnvironmentVariable("UE_SILENT", "1");
        // Quiet uninstall always elevates when a machine copy or Windows service may be present.
        bool machine = RunningMachineCopy || (!File.Exists(UserExe) && File.Exists(MachineExe))
            || PmeServiceControl.ReadState(WindowsServiceHost.ServiceName).Exists;
        LaunchMaintenance("uninstall", machine, false, false, false, windowsService: false);
    }

    /// <summary>
    /// Phase 2a self-update: replace the installed EXE with a verified package from temp.
    /// History/settings are preserved. The helper is a copy of the currently running build.
    /// </summary>
    internal static void LaunchSelfUpdate(string verifiedPackagePath)
    {
        if (string.IsNullOrWhiteSpace(verifiedPackagePath) || !File.Exists(verifiedPackagePath))
            throw new FileNotFoundException("Verified update package was not found.", verifiedPackagePath);
        bool machine = RunningMachineCopy || (!File.Exists(UserExe) && File.Exists(MachineExe));
        string targetDir = machine ? MachineInstallDir : UserInstallDir;
        string menuDir = machine
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "MicroSystems")
            : StartMenuDir;
        string menuShortcut = Path.Combine(menuDir, ShortcutFileName);
        string desktopShortcut = Path.Combine(
            Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory),
            ShortcutFileName);
        bool desktop = HasAppShortcut(EnumerateDesktopFolders()) || File.Exists(desktopShortcut);
        bool startMenu = HasAppShortcut(EnumerateStartMenuFolders()) || File.Exists(menuShortcut);
        bool taskbar = HasAppShortcut(EnumerateTaskbarFolders());
        bool agent = false;
        if (!machine)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            agent = key?.GetValue(RunName) is string;
        }
        AgentLog.WriteInstall($"Launching self-update: package={verifiedPackagePath}; scope={(machine ? "machine" : "user")}; agent={agent}; taskbar={taskbar}");
        bool hadService = PmeServiceControl.ReadState(WindowsServiceHost.ServiceName).Exists;
        LaunchMaintenance("upgrade", machine, desktop, startMenu, agent, taskbar, windowsService: hadService, packageSource: verifiedPackagePath);
    }

    private static void LaunchMaintenance(
        string operation,
        bool allUsers,
        bool desktop,
        bool startMenu,
        bool agent,
        bool taskbar = false,
        bool windowsService = false,
        string? packageSource = null)
    {
        string running = Environment.ProcessPath ?? throw new InvalidOperationException("Fant ikke programfilen.");
        string source = packageSource ?? running;
        // Stage a sibling helper (different file name) so UpdateExpress.exe can be replaced safely.
        // Prefer the install directory over %TEMP% — App Control frequently blocks Temp launches.
        string helper = StageMaintenanceHelper(running, allUsers || windowsService);
        string helperDir = Path.GetDirectoryName(helper) ?? UserInstallDir;
        int parentPid = Environment.ProcessId;
        string silentFlag = string.Equals(Environment.GetEnvironmentVariable("UE_SILENT"), "1", StringComparison.OrdinalIgnoreCase)
            ? " --silent"
            : "";
        string serviceFlag = windowsService ? " --windows-service" : "";
        // Service install always elevates (machine scope).
        bool elevate = allUsers || windowsService || string.Equals(operation, "uninstall", StringComparison.OrdinalIgnoreCase)
            && PmeServiceControl.ReadState(WindowsServiceHost.ServiceName).Exists;
        string arguments =
            $"--apply-maintenance {operation} {parentPid} \"{source}\" {(allUsers || windowsService ? "machine" : "user")} " +
            $"{(desktop ? 1 : 0)} {(startMenu ? 1 : 0)} {(agent ? 1 : 0)} {(taskbar ? 1 : 0)}{serviceFlag}{silentFlag}";
        AgentLog.WriteInstall($"Launching detached maintenance: {operation} (parent {parentPid}; service={windowsService}; elevate={elevate}); helper={helper}");
        try
        {
            using var maintenanceProcess = Process.Start(new ProcessStartInfo(helper, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = helperDir,
                Verb = elevate ? "runas" : "open"
            }) ?? throw new InvalidOperationException("Kunne ikke starte vedlikeholdsprosessen.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(FormatMaintenanceLaunchFailure(helper, ex), ex);
        }
        // Release locks before the helper replaces files. Shutdown must run on the WPF UI thread
        // (tray self-update downloads on a background thread).
        ShutdownCurrentApplication();
    }

    /// <summary>
    /// Copy the running build to UpdateExpress-Maintenance.exe under the install dir when writable.
    /// Falls back to a Temp staging folder only when the install dir cannot be written
    /// (e.g. machine scope without elevation to Program Files).
    /// </summary>
    private static string StageMaintenanceHelper(string runningExe, bool allUsers)
    {
        foreach (string dir in PreferredMaintenanceHelperDirs(allUsers))
        {
            try
            {
                Directory.CreateDirectory(dir);
                string helper = Path.Combine(dir, MaintenanceHelperFileName);
                try
                {
                    if (File.Exists(helper))
                        File.Delete(helper);
                }
                catch (IOException)
                {
                    // Previous helper still running; try a unique sibling name in the same allowlisted folder.
                    helper = Path.Combine(dir, "UpdateExpress-Maintenance-" + Guid.NewGuid().ToString("N") + ".exe");
                }
                File.Copy(runningExe, helper, overwrite: true);
                AgentLog.Write("Staged maintenance helper at " + helper);
                return helper;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                AgentLog.Write($"Could not stage maintenance helper under {dir}: {ex.Message}");
            }
        }

        string fallbackDir = Path.Combine(Path.GetTempPath(), "MicroSystems-UpdateExpress-Maintenance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fallbackDir);
        string fallbackHelper = Path.Combine(fallbackDir, MaintenanceHelperFileName);
        File.Copy(runningExe, fallbackHelper, overwrite: true);
        AgentLog.Write("WARNING: staged maintenance helper under Temp (App Control may block): " + fallbackHelper);
        return fallbackHelper;
    }

    private static IEnumerable<string> PreferredMaintenanceHelperDirs(bool allUsers)
    {
        // Machine installs: prefer Program Files when writable (elevated UI / already machine copy).
        if (allUsers)
            yield return MachineInstallDir;
        // Always prefer the per-user install root over Temp — same folder admins already allowlist.
        yield return UserInstallDir;
    }

    internal static bool IsAppControlBlock(Exception? ex)
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is System.ComponentModel.Win32Exception win32 &&
                (win32.NativeErrorCode == NativeErrorAppControlBlocked ||
                 win32.NativeErrorCode == NativeErrorAccessDisabledByPolicy))
                return true;

            string msg = cur.Message ?? "";
            if (msg.Contains("programkontroll", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("App Control", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("system integrity policy", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Windows Code Integrity", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static string FormatMaintenanceLaunchFailure(string helperPath, Exception ex)
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        bool fromTemp = Path.GetFullPath(helperPath).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || helperPath.Contains("MicroSystems-UpdateExpress-Maintenance", StringComparison.OrdinalIgnoreCase);

        if (IsAppControlBlock(ex) || MessageLooksLikeAppControl(ex.Message))
        {
            return
                "Microsoft App Control (programkontroll) blocked starting the Update Express maintenance helper.\n\n" +
                $"Blocked file:\n{helperPath}\n\n" +
                "Self-update prefers launching the helper from the install folder (Program Files or LocalAppData), " +
                "not from %TEMP%. If this still appears, ask your administrator to allow Update Express under App Control " +
                "(path rule for the install folder, or a publisher rule once the EXE is Authenticode-signed).\n\n" +
                "Admin guide: docs/app-control-updateexpress.md in the UpdateExpress repository.\n\n" +
                "Details: " + ex.Message;
        }

        if (fromTemp)
        {
            return
                "Could not start the Update Express maintenance helper from a temporary folder.\n\n" +
                $"Helper:\n{helperPath}\n\n" +
                "On PCs with Microsoft App Control / WDAC, unsigned helpers under %TEMP% are often blocked " +
                "(Norwegian Windows: «En programkontrollpolicy har blokkert denne filen.»). " +
                "Retry after an admin allowlists the install folder, or install the all-users (Program Files) copy.\n\n" +
                "Details: " + ex.Message;
        }

        return "Kunne ikke starte vedlikeholdsprosessen.\n\n" + ex.Message;
    }

    private static bool MessageLooksLikeAppControl(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return message.Contains("programkontroll", StringComparison.OrdinalIgnoreCase)
            || message.Contains("App Control", StringComparison.OrdinalIgnoreCase)
            || message.Contains("system integrity policy", StringComparison.OrdinalIgnoreCase);
    }

    private static void ShutdownCurrentApplication()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            Environment.Exit(0);
            return;
        }
        if (app.Dispatcher.CheckAccess())
        {
            app.Shutdown();
            return;
        }
        try
        {
            app.Dispatcher.Invoke(() => app.Shutdown());
        }
        catch (Exception ex)
        {
            AgentLog.WriteInstall("Dispatcher shutdown failed; forcing process exit: " + ex.Message);
            Environment.Exit(0);
        }
    }

    internal static void ApplyMaintenance(string[] args)
    {
        // Arguments: --apply-maintenance <op> <parentPID> <sourceEXE> <scope> <desktop> <startMenu> <agent> [taskbar]
        //            [--windows-service] [--silent]
        bool silent = args.Any(a =>
                string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase))
            || string.Equals(Environment.GetEnvironmentVariable("UE_SILENT"), "1", StringComparison.OrdinalIgnoreCase);
        bool windowsService = args.Any(a =>
            string.Equals(a, "--windows-service", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--service-install", StringComparison.OrdinalIgnoreCase));
        string[] positional = args
            .Where(a => !string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(a, "--windows-service", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(a, "--service-install", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if ((positional.Length != 8 && positional.Length != 9) || !new[] { "install", "repair", "uninstall", "upgrade" }.Contains(positional[1], StringComparer.OrdinalIgnoreCase)
            || !int.TryParse(positional[2], out int parentPid))
            throw new ArgumentException("Ugyldige parametere for vedlikehold.");
        string operation = positional[1].ToLowerInvariant();
        string source = Path.GetFullPath(positional[3]);
        bool machine = positional[4] == "machine" || windowsService;
        bool desktop = positional[5] == "1", startMenu = positional[6] == "1", agent = positional[7] == "1";
        bool taskbar = positional.Length >= 9 && positional[8] == "1";
        // Machine-wide Run key is not used; keep per-user agent when Windows service is installed
        // so prompts can still surface in the interactive session.
        if (machine && !windowsService) agent = false;
        string targetDir = machine ? MachineInstallDir : UserInstallDir;
        string targetExe = Path.Combine(targetDir, "UpdateExpress.exe");
        string menuDir = machine ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "MicroSystems") : StartMenuDir;
        string menuShortcut = Path.Combine(menuDir, ShortcutFileName);
        string desktopShortcut = Path.Combine(Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory), ShortcutFileName);
        string taskbarDir = TaskbarPinFolder;
        string taskbarShortcut = Path.Combine(taskbarDir, ShortcutFileName);
        var hive = machine ? Registry.LocalMachine : Registry.CurrentUser;
        string iconPath = Path.Combine(targetDir, DesktopIconName);
        if (!File.Exists(source) && operation != "uninstall")
            throw new FileNotFoundException("Fant ikke installasjonsfilen.", source);
        AgentLog.BeginInstallSession(operation,
            $"source={source}; scope={(machine ? "machine" : "user")}; desktop={desktop}; startMenu={startMenu}; agent={agent}; taskbar={taskbar}; windowsService={windowsService}; silent={silent}; parentPid={parentPid}");
        AgentLog.WritePathProbe("Source EXE", source);
        AgentLog.WritePathProbe("Target EXE (before)", targetExe);
        try
        {
            // Wait for the launcher that started us to close (parentPid 0 = script/RMM, skip wait).
            if (parentPid > 0)
            {
                try { using var parent = Process.GetProcessById(parentPid); parent.WaitForExit(30000); }
                catch (ArgumentException) { /* Parent has exited already. */ }
            }
            // Always stop every installed UpdateExpress process before replace/uninstall.
            // Starting shortcuts/tray mid-install previously locked the EXE and hung RMM scripts.
            if (operation is "upgrade" or "uninstall" or "install" or "repair")
            {
                bool servicePresent = PmeServiceControl.ReadState(WindowsServiceHost.ServiceName).Exists;
                if (operation == "uninstall" || windowsService || servicePresent)
                {
                    try { WindowsServiceInstaller.StopAndDelete(); }
                    catch (Exception ex) { AgentLog.WriteInstall("Windows service stop/delete before replace: " + ex.Message); }
                    // Re-create after files land when this install asked for a service, or when upgrading a prior service install.
                    if (operation != "uninstall" && (windowsService || servicePresent))
                        windowsService = true;
                }
                StopInstalledProcesses(targetExe);
            }
            if (operation == "uninstall")
            {
                try { WindowsServiceInstaller.StopAndDelete(); }
                catch (Exception ex) { AgentLog.WriteInstall("Windows service uninstall step failed: " + ex.Message); }
                try { DefenderExclusions.RemoveRegistered(targetDir, targetExe, machine, silent); }
                catch (Exception ex) { AgentLog.WriteInstall("Defender exclusions uninstall step failed: " + ex.Message); }
                // Clear Run key for the installing user even on machine uninstall (prompt agent).
                try { using var key = Registry.CurrentUser.OpenSubKey(RunKey, true); key?.DeleteValue(RunName, false); } catch { /* ignore */ }
                hive.DeleteSubKeyTree(UninstallKey, false);
                // Also clear the other hive in case a prior scope mismatch left an Apps entry behind.
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { /* ignore */ }
                if (machine) { try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { /* ignore */ } }
                RemoveAppShortcuts(EnumerateDesktopFolders(), keepPath: null);
                RemoveAppShortcuts(EnumerateStartMenuFolders(), keepPath: null);
                RemoveAppShortcuts(EnumerateTaskbarFolders(), keepPath: null);
                if (File.Exists(targetExe)) RetryFileOperation(() => File.Delete(targetExe), targetExe);
                if (File.Exists(iconPath)) RetryFileOperation(() => File.Delete(iconPath), iconPath);
                TryDeleteLegacyVersionedIcons(targetDir);
                TryDeleteFile(Path.Combine(targetDir, "UpdateExpress.exe.new"));
                TryDeleteFile(Path.Combine(targetDir, "agent.pid"));
                TryDeleteFile(Path.Combine(targetDir, "defender-exclusions.json"));
                AgentLog.WriteInstall("Uninstall completed.");
                AgentLog.EndInstallSession(operation, ok: true, summary: "Uninstall finished; history/settings retained.");
                if (!silent)
                    MessageBox.Show("Update Express er avinstallert. Historikk og innstillinger er beholdt.", "Update Express");
            }
            else
            {
                Directory.CreateDirectory(targetDir);
                // The staged helper must not be copied over itself; source can be an installed or published EXE.
                // For repair from installed EXE, existing program file is left unchanged.
                if (!string.Equals(source, targetExe, StringComparison.OrdinalIgnoreCase))
                {
                    AgentLog.WriteInstall($"Copy/replace EXE (staged): {source} → {targetExe}");
                    ReplaceExeStaged(source, targetExe);
                    AgentLog.WritePathProbe("Target EXE (after copy)", targetExe);
                }
                else if (!File.Exists(targetExe))
                    throw new FileNotFoundException("Programfilen mangler. Kjør Installer fra den nye Publisert-mappen.");
                // Always rewrite the on-disk U .ico so upgrade refreshes desktop/Start/taskbar IconLocation.
                AgentLog.WriteInstall("Writing installed icon: " + iconPath);
                RetryFileOperation(() => WriteInstalledIcon(iconPath), iconPath);
                TryDeleteLegacyVersionedIcons(targetDir);
                try
                {
                    // Drop any leftover orange UpdateExpress.ico beside the install if present.
                    string legacyOrange = Path.Combine(targetDir, "UpdateExpress.ico");
                    if (File.Exists(legacyOrange)
                        && !string.Equals(legacyOrange, iconPath, StringComparison.OrdinalIgnoreCase))
                        RetryFileOperation(() => File.Delete(legacyOrange), legacyOrange);
                }
                catch (Exception ex) { AgentLog.WriteInstall("Legacy UpdateExpress.ico cleanup: " + ex.Message); }

                string displayVersion = ReadDisplayVersion(targetExe);
                string uninstallCmd = $"\"{targetExe}\" --uninstall";
                string quietUninstallCmd = $"\"{targetExe}\" --uninstall --silent";
                using (var key = hive.CreateSubKey(UninstallKey))
                {
                    key.SetValue("DisplayName", "Update Express – MicroSystems");
                    key.SetValue("DisplayVersion", displayVersion);
                    key.SetValue("Publisher", "MicroSystems");
                    key.SetValue("InstallLocation", targetDir);
                    key.SetValue("DisplayIcon", targetExe);
                    key.SetValue("UninstallString", uninstallCmd);
                    key.SetValue("QuietUninstallString", quietUninstallCmd);
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
                // Per-user Run key for prompt agent (also when machine service is installed).
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (agent || windowsService)
                        key.SetValue(RunName, $"\"{targetExe}\" --agent");
                    else
                        key.DeleteValue(RunName, false);
                }

                // Mirror SetupCompleted policy into ProgramData for LocalSystem service reads.
                try
                {
                    var policy = UpdatePolicy.Load();
                    if (policy.SetupCompleted)
                        policy.SaveMachineMirror();
                }
                catch (Exception ex) { AgentLog.WriteInstall("Machine policy mirror failed: " + ex.Message); }

                if (windowsService)
                {
                    try { WindowsServiceInstaller.InstallOrUpdate(targetExe); }
                    catch (Exception ex)
                    {
                        AgentLog.WriteInstall("Windows service install failed: " + ex);
                        throw;
                    }
                }

                // Defender before shortcuts/tray so a long MpPreference call cannot race a user-launched UI.
                AgentLog.WriteInstall($"{operation} files ready: scope={(machine ? "machine" : "user")}, agent={agent}, windowsService={windowsService}, silent={silent}, version={displayVersion}, path={targetDir}, by={System.Security.Principal.WindowsIdentity.GetCurrent().Name}.");
                try { DefenderExclusions.EnsureRegistered(targetDir, targetExe, machine, silent); }
                catch (Exception ex) { AgentLog.WriteInstall("Defender exclusions register step failed: " + ex.Message); }

                // Silent RMM: defer shortcuts + agent so the script can seed SetupCompleted first.
                // Interactive: shortcuts after Defender; agent Run key is set but tray starts only after setup
                // (or immediately on upgrade/repair when setup was already completed).
                bool deferLaunch = silent;
                if (!deferLaunch)
                {
                    ApplyShortcutFlags(desktop, startMenu, taskbar, menuDir, menuShortcut, desktopShortcut, taskbarDir, taskbarShortcut, targetExe, targetDir, iconPath);
                    bool setupDone = IsSetupCompleted();
                    bool startAgent = agent || windowsService;
                    if (startAgent && (operation != "install" || setupDone))
                    {
                        AgentLog.WriteInstall("Starting background agent: " + targetExe);
                        try
                        {
                            Process.Start(new ProcessStartInfo(targetExe, "--agent")
                            { UseShellExecute = true, WorkingDirectory = targetDir });
                        }
                        catch (Exception ex) { AgentLog.WriteInstall("Failed to start background agent: " + ex.Message); }
                        try { EnsureBackgroundAgentRunning(targetExe, waitForPid: true); }
                        catch (Exception ex) { AgentLog.WriteInstall("Post-install agent ensure failed: " + ex.Message); }
                    }
                    else if (startAgent && operation == "install" && !setupDone)
                    {
                        AgentLog.WriteInstall("Deferring tray agent until setup wizard completes.");
                    }
                    if (operation == "upgrade")
                    {
                        AgentLog.WriteInstall("Starting UI after upgrade: " + targetExe);
                        try
                        {
                            Process.Start(new ProcessStartInfo(targetExe, "--installed")
                            { UseShellExecute = true, WorkingDirectory = targetDir });
                        }
                        catch (Exception ex) { AgentLog.WriteInstall("Failed to start UI after upgrade: " + ex.Message); }
                    }
                }
                else
                {
                    AgentLog.WriteInstall("Silent mode: shortcuts/tray deferred for RMM policy seeding.");
                    // Still drop stale shortcuts when flags are off so scope changes stay clean.
                    if (!startMenu) RemoveAppShortcuts(EnumerateStartMenuFolders(), keepPath: null);
                    if (!desktop) RemoveAppShortcuts(EnumerateDesktopFolders(), keepPath: null);
                    if (!taskbar) RemoveAppShortcuts(EnumerateTaskbarFolders(), keepPath: null);
                }

                AgentLog.PostInstallPresenceCheck(
                    targetExe,
                    (!deferLaunch && desktop) ? desktopShortcut : null,
                    (!deferLaunch && startMenu) ? menuShortcut : null,
                    (!deferLaunch && (taskbar || File.Exists(taskbarShortcut))) ? taskbarShortcut : null);
                AgentLog.EndInstallSession(operation, ok: true,
                    summary: $"Final path={targetDir}; version={displayVersion}; Logs={AgentLog.LogsDir}; deferred={deferLaunch}");
                string completedAction = operation == "repair" ? "repaired" : operation == "upgrade" ? "updated" : "installed";
                string scopeText = machine ? "all users" : "the current user";
                if (!silent)
                {
                    AutoCloseInfoDialog.Show(
                        $"Update Express was {completedAction} successfully for {scopeText}.\n\n"
                        + $"Version: {displayVersion}\n"
                        + (windowsService
                            ? $"Windows service “{WindowsServiceHost.DisplayName}” is installed (LocalSystem; internal name {WindowsServiceHost.ServiceName}). Tray agent handles prompts in the user session.\n"
                            : "")
                        + (agent || windowsService
                            ? "Background monitoring is enabled for this user."
                            : "Background monitoring is disabled.")
                        + (operation == "upgrade" ? "\n\nUpdate Express will open now." : "\n\nYou can pin Update Express to the taskbar from the Start menu."),
                        "Update Express",
                        autoCloseSeconds: 10);
                }
                TryDeleteTree(Path.GetDirectoryName(source));
            }
        }
        catch (Exception ex)
        {
            AgentLog.WriteInstall("Maintenance FAILED: " + ex);
            AgentLog.EndInstallSession(operation, ok: false, summary: ex.Message);
            bool silentFail = args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase))
                || string.Equals(Environment.GetEnvironmentVariable("UE_SILENT"), "1", StringComparison.OrdinalIgnoreCase);
            if (!silentFail)
            {
                string detail = IsAppControlBlock(ex)
                    ? FormatMaintenanceLaunchFailure(Environment.ProcessPath ?? MaintenanceHelperFileName, ex)
                    : FormatMaintenanceError(ex);
                MessageBox.Show("Vedlikehold mislyktes:\n\n" + detail
                    + "\n\nSe Logs\\install-update.log (og agent.log) for detaljer.",
                    "Update Express", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                // Never rethrow: an unhandled WPF exception dialog hangs RMM Start-Process -Wait.
                Environment.ExitCode = 1;
            }
        }
    }

    /// <summary>
    /// Create desktop / Start Menu / taskbar shortcuts after silent policy seeding (RMM) or when finishing setup.
    /// </summary>
    internal static void ApplyDeferredShortcuts(bool desktop, bool startMenu, bool taskbar, bool machine)
    {
        string targetDir = machine ? MachineInstallDir : UserInstallDir;
        string targetExe = Path.Combine(targetDir, "UpdateExpress.exe");
        if (!File.Exists(targetExe))
            throw new FileNotFoundException("Installed EXE missing; cannot create shortcuts.", targetExe);
        string iconPath = Path.Combine(targetDir, DesktopIconName);
        if (!File.Exists(iconPath))
            RetryFileOperation(() => WriteInstalledIcon(iconPath), iconPath);
        string menuDir = machine
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "MicroSystems")
            : StartMenuDir;
        string menuShortcut = Path.Combine(menuDir, ShortcutFileName);
        string desktopShortcut = Path.Combine(
            Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory),
            ShortcutFileName);
        string taskbarDir = TaskbarPinFolder;
        string taskbarShortcut = Path.Combine(taskbarDir, ShortcutFileName);
        ApplyShortcutFlags(desktop, startMenu, taskbar, menuDir, menuShortcut, desktopShortcut, taskbarDir, taskbarShortcut, targetExe, targetDir, iconPath);
    }

    private static void ApplyShortcutFlags(
        bool desktop, bool startMenu, bool taskbar,
        string menuDir, string menuShortcut, string desktopShortcut, string taskbarDir, string taskbarShortcut,
        string targetExe, string targetDir, string iconPath)
    {
        if (startMenu)
        {
            Directory.CreateDirectory(menuDir);
            RemoveAppShortcuts(EnumerateStartMenuFolders(), keepPath: menuShortcut);
            CreateShortcut(menuShortcut, targetExe, "--installed", targetDir, iconPath);
        }
        else
            RemoveAppShortcuts(EnumerateStartMenuFolders(), keepPath: null);
        if (desktop)
        {
            RemoveAppShortcuts(EnumerateDesktopFolders(), keepPath: desktopShortcut);
            CreateShortcut(desktopShortcut, targetExe, "--installed", targetDir, iconPath);
        }
        else
            RemoveAppShortcuts(EnumerateDesktopFolders(), keepPath: null);
        bool hadTaskbarPin = HasAppShortcut(EnumerateTaskbarFolders()) || File.Exists(taskbarShortcut);
        if (taskbar || hadTaskbarPin)
        {
            Directory.CreateDirectory(taskbarDir);
            RemoveAppShortcuts(EnumerateTaskbarFolders(), keepPath: taskbarShortcut);
            CreateShortcut(taskbarShortcut, targetExe, "--installed", targetDir, iconPath);
            AgentLog.WriteInstall(taskbar
                ? "Taskbar pin created/updated: " + taskbarShortcut
                : "Existing taskbar pin refreshed: " + taskbarShortcut);
        }
        else
            RemoveAppShortcuts(EnumerateTaskbarFolders(), keepPath: null);
    }

    private static bool IsSetupCompleted()
    {
        try { return UpdatePolicy.Load().SetupCompleted; }
        catch { return false; }
    }

    /// <summary>
    /// Copy to .new then replace the live EXE so a failed/locked write cannot wipe the installed app.
    /// </summary>
    private static void ReplaceExeStaged(string source, string targetExe)
    {
        string staged = targetExe + ".new";
        TryDeleteFile(staged);
        RetryFileOperation(() => File.Copy(source, staged, true), staged);
        if (!File.Exists(staged) || new FileInfo(staged).Length < 1024)
            throw new IOException("Staged EXE is missing or too small after copy.");
        if (File.Exists(targetExe))
        {
            try
            {
                RetryFileOperation(() => File.Copy(staged, targetExe, true), targetExe);
            }
            catch (IOException)
            {
                // Last resort: delete+move only after staged file is known-good; restore from staged if move fails mid-way.
                try
                {
                    RetryFileOperation(() => File.Delete(targetExe), targetExe);
                    RetryFileOperation(() => File.Move(staged, targetExe), targetExe);
                    staged = ""; // moved
                }
                catch (Exception ex)
                {
                    if (!string.IsNullOrEmpty(staged) && File.Exists(staged) && !File.Exists(targetExe))
                    {
                        try { File.Move(staged, targetExe); staged = ""; }
                        catch (Exception restoreEx)
                        {
                            throw new IOException(
                                "Could not replace UpdateExpress.exe and restore failed. Staged copy left at " + targetExe + ".new",
                                restoreEx);
                        }
                    }
                    throw new IOException("Could not replace UpdateExpress.exe (file locked). Close Update Express and retry.", ex);
                }
            }
        }
        else
        {
            RetryFileOperation(() => File.Move(staged, targetExe), targetExe);
            staged = "";
        }
        if (!string.IsNullOrEmpty(staged))
            TryDeleteFile(staged);
        if (!File.Exists(targetExe))
            throw new IOException("Installed EXE missing after staged replace.");
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AgentLog.WriteInstall("Could not delete " + path + ": " + ex.Message); }
    }

    private static string FormatMaintenanceError(Exception ex)
    {
        // RetryFileOperation already emits a path-specific antivirus/quarantine message.
        if (ex.Message.Contains("Bitdefender", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("karantene", StringComparison.OrdinalIgnoreCase))
            return ex.Message;

        if (!IsAccessDenied(ex))
            return ex.Message;

        return "Tilgang nektet til installasjonsfilen. Dette skjer ofte når antivirus "
            + "(f.eks. Bitdefender Advanced Threat Control) har karantenesatt eller låst "
            + "UpdateExpress.exe midt i en kjøring.\n\n"
            + "1) Sjekk Managed Antivirus / karantene og gjenopprett UpdateExpress.exe om den ligger der.\n"
            + "2) Legg inn mappeunntak for:\n"
            + @"   %LOCALAPPDATA%\MicroSystems\Update Express\" + "\n"
            + @"   C:\Program Files\MicroSystems\Update Express\" + " (ved alle-brukere-installasjon)\n"
            + "3) Bruk full sti / mappe / prosess-unntak — ikke bare filnavnet UpdateExpress.exe.\n"
            + "4) Lukk Update Express og prøv Vedlikehold / oppdatering på nytt.\n\n"
            + "Teknisk: " + ex.Message;
    }

    private static bool IsAccessDenied(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
                return true;
            if (current is IOException && (current.HResult & 0xFFFF) == 5)
                return true;
            string message = current.Message ?? "";
            if (message.Contains("access denied", StringComparison.OrdinalIgnoreCase))
                return true;
            if (message.Contains("Access to the path", StringComparison.OrdinalIgnoreCase)
                && message.Contains("denied", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ReadDisplayVersion(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            string version = (info.ProductVersion ?? info.FileVersion ?? "").Split('+')[0].Trim();
            return string.IsNullOrWhiteSpace(version) ? ProductVersion.Display : version;
        }
        catch { return ProductVersion.Display; }
    }

    private static void TryDeleteTree(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        // Only clean staged update packages under our temp update root.
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MicroSystems-UpdateExpress-Update"));
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        try { if (Directory.Exists(full)) Directory.Delete(full, true); }
        catch (Exception ex) { AgentLog.WriteInstall("Could not delete staged update package folder: " + ex.Message); }
    }

    private static void StopInstalledProcesses(string targetExe)
    {
        string targetFull = Path.GetFullPath(targetExe);
        foreach (var proc in Process.GetProcessesByName("UpdateExpress"))
        {
            using (proc)
            {
                try
                {
                    if (proc.Id == Environment.ProcessId) continue;
                    // Do not use Process.MainModule — it requires full access and fails with
                    // Access Denied against the elevated MainWindow from a medium-IL helper.
                    if (!UpgradeShutdown.TryGetProcessPath(proc.Id, out string? path) ||
                        !string.Equals(path, targetFull, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        proc.CloseMainWindow();
                        if (proc.WaitForExit(8000))
                        {
                            AgentLog.WriteInstall("Closed Update Express UI PID " + proc.Id + " for upgrade.");
                            continue;
                        }
                    }

                    proc.Kill(entireProcessTree: true);
                    if (!proc.WaitForExit(10000))
                        throw new IOException("Update Express could not be stopped for upgrade.");
                    AgentLog.WriteInstall("Stopped Update Express process PID " + proc.Id + " for upgrade.");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    throw new IOException(
                        "Could not stop Update Express for upgrade (access denied). Close the Update Express window and try again.",
                        ex);
                }
            }
        }
    }

    private static void StopInstalledAgent(string targetExe)
    {
        string targetFull = Path.GetFullPath(targetExe);
        foreach (var proc in Process.GetProcessesByName("UpdateExpress"))
        {
            using (proc)
            {
                try
                {
                    // Never terminate unrelated processes or the detached helper.
                    if (proc.Id == Environment.ProcessId) continue;
                    if (!UpgradeShutdown.TryGetProcessPath(proc.Id, out string? path) ||
                        !string.Equals(path, targetFull, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Only stop --agent processes. If the user has the main UI open, show a clear error.
                    bool isAgent = File.Exists(AgentPidFile) && int.TryParse(File.ReadAllText(AgentPidFile), out int agentPid) && agentPid == proc.Id;
                    if (!isAgent)
                        throw new IOException("Lukk det installerte Update Express-vinduet før oppgradering eller avinstallasjon.");
                    proc.Kill(entireProcessTree: true);
                    if (!proc.WaitForExit(10000)) throw new IOException("Bakgrunnsagenten kunne ikke avsluttes.");
                    AgentLog.WriteInstall("Stopped installed agent PID " + proc.Id);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    throw new IOException("Kunne ikke kontrollere/stoppe Update Express-prosessen: " + ex.Message, ex);
                }
            }
        }
    }

    internal static readonly string AgentPidFile = Path.Combine(UserInstallDir, "agent.pid");

    /// <summary>
    /// True when HKCU Run is configured to start the tray agent for this user.
    /// </summary>
    internal static bool IsBackgroundAgentEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(RunName) is string value &&
                   value.Contains("UpdateExpress", StringComparison.OrdinalIgnoreCase) &&
                   value.Contains("--agent", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns true when a live Update Express process matches agent.pid or
    /// is clearly the installed EXE running as tray (no main window) — best-effort.
    /// </summary>
    internal static bool IsBackgroundAgentAlive()
    {
        try
        {
            if (File.Exists(AgentPidFile) &&
                int.TryParse(File.ReadAllText(AgentPidFile).Trim(), out int pid) &&
                pid > 0)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    if (!proc.HasExited) return true;
                }
                catch (ArgumentException)
                {
                    try { File.Delete(AgentPidFile); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }
        return false;
    }

    /// <summary>
    /// If Run key says agent should run but it is not alive, start --agent.
    /// Safe to call from UI startup after self-update when the helper stalled on Defender.
    /// </summary>
    internal static bool EnsureBackgroundAgentRunning(string? exePath = null, bool waitForPid = false)
    {
        if (!IsBackgroundAgentEnabled())
        {
            AgentLog.WriteInstall("Ensure agent: Run key not set — tray not required.");
            return false;
        }
        if (IsBackgroundAgentAlive())
        {
            AgentLog.WriteInstall("Ensure agent: already alive (agent.pid).");
            return true;
        }

        string exe = string.IsNullOrWhiteSpace(exePath)
            ? (File.Exists(InstalledExe) ? InstalledExe : (Environment.ProcessPath ?? UserExe))
            : exePath!;
        if (!File.Exists(exe))
        {
            AgentLog.WriteInstall("Ensure agent: EXE missing at " + exe);
            return false;
        }

        AgentLog.WriteInstall("Ensure agent: starting tray — " + exe);
        Process.Start(new ProcessStartInfo(exe, "--agent")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? UserInstallDir
        });
        if (!waitForPid)
            return true;
        for (int i = 0; i < 20; i++)
        {
            System.Threading.Thread.Sleep(150);
            if (IsBackgroundAgentAlive()) return true;
        }
        AgentLog.WriteInstall("Ensure agent: started but agent.pid not confirmed yet.");
        return false;
    }

    private static void RetryFileOperation(Action action, string? pathHint = null)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                // ATC disinfection / quarantine can hold the file briefly; back off a bit longer than a normal lock.
                System.Threading.Thread.Sleep(attempt < 4 ? 500 : 1000);
            }
        }
        throw CreateLockedFileException(pathHint, last);
    }

    private static IOException CreateLockedFileException(string? pathHint, Exception? inner)
    {
        string subject = string.IsNullOrWhiteSpace(pathHint)
            ? "Programfilen"
            : "Filen «" + pathHint + "»";
        return new IOException(
            subject + " er fortsatt låst eller sperret (ofte antivirus/karantene, f.eks. Bitdefender ATC). "
            + "Gjenopprett filen fra karantene hvis den er der, legg inn mappe-/prosessunntak for "
            + @"%LOCALAPPDATA%\MicroSystems\Update Express\, lukk Update Express, og prøv igjen.",
            inner);
    }

    private static void WriteInstalledIcon(string destination)
    {
        using var resource = typeof(InstallationManager).Assembly.GetManifestResourceStream(DesktopIconResource)
            ?? throw new InvalidOperationException("Embedded U icon resource was not found: " + DesktopIconResource);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        resource.CopyTo(output);
    }

    private static void TryDeleteLegacyVersionedIcons(string targetDir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(targetDir, "UpdateExpress-U-v*.ico"))
            {
                try { File.Delete(file); }
                catch (Exception ex) { AgentLog.WriteInstall("Could not delete legacy icon " + file + ": " + ex.Message); }
            }
        }
        catch (Exception ex) { AgentLog.WriteInstall("Legacy icon cleanup skipped: " + ex.Message); }
    }

    private static IEnumerable<string> EnumerateDesktopFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static IEnumerable<string> EnumerateStartMenuFolders()
    {
        yield return StartMenuDir;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "MicroSystems");
    }

    private static string TaskbarPinFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

    private static IEnumerable<string> EnumerateTaskbarFolders()
    {
        yield return TaskbarPinFolder;
    }

    private static bool HasAppShortcut(IEnumerable<string> folders) =>
        EnumerateAppShortcuts(folders).Any();

    private static IEnumerable<string> EnumerateAppShortcuts(IEnumerable<string> folders)
    {
        foreach (string folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            string[] files;
            try { files = Directory.GetFiles(folder, "*.lnk"); }
            catch { continue; }
            foreach (string path in files)
            {
                if (IsAppShortcut(path))
                    yield return path;
            }
        }
    }

    private static bool IsAppShortcut(string shortcutPath)
    {
        string name = Path.GetFileNameWithoutExtension(shortcutPath) ?? "";
        bool nameLooksLikeOurs =
            name.Equals("Update Express", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Update Express (", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("UpdateExpress", StringComparison.OrdinalIgnoreCase);
        string? target = TryReadShortcutTarget(shortcutPath);
        if (!string.IsNullOrWhiteSpace(target))
        {
            string full = Path.GetFullPath(target);
            bool pointsAtApp =
                full.EndsWith("UpdateExpress.exe", StringComparison.OrdinalIgnoreCase) &&
                full.Contains(Path.Combine("MicroSystems", "Update Express"), StringComparison.OrdinalIgnoreCase);
            if (pointsAtApp) return true;
            // Name match alone is not enough if the .lnk points elsewhere.
            return false;
        }
        return nameLooksLikeOurs;
    }

    private static string? TryReadShortcutTarget(string shortcutPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            object shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })!;
                string? target = shortcut.TargetPath as string;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally
            {
                if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Removes Update Express desktop/Start Menu shortcuts, optionally keeping one canonical path.
    /// Prevents duplicate icons after repeated installs/upgrades (OneDrive/sync renames, scope changes).
    /// </summary>
    private static void RemoveAppShortcuts(IEnumerable<string> folders, string? keepPath)
    {
        string? keepFull = string.IsNullOrWhiteSpace(keepPath) ? null : Path.GetFullPath(keepPath);
        foreach (string path in EnumerateAppShortcuts(folders))
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (keepFull is not null && string.Equals(full, keepFull, StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Delete(full);
                AgentLog.WriteInstall("Removed duplicate/orphan shortcut: " + full);
            }
            catch (Exception ex)
            {
                AgentLog.WriteInstall("Could not remove shortcut " + path + ": " + ex.Message);
            }
        }
    }

    private static void CreateShortcut(string shortcutPath, string target, string arguments, string workingDirectory, string iconPath)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host mangler.");
        object shell = Activator.CreateInstance(shellType)!;
        try
        {
            // Always delete+recreate so Explorer cannot keep a cached IconLocation to an old orange U.
            if (File.Exists(shortcutPath))
            {
                try { File.Delete(shortcutPath); }
                catch (Exception ex) { AgentLog.WriteInstall("Could not replace existing shortcut before recreate: " + ex.Message); }
            }
            dynamic shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })!;
            shortcut.TargetPath = target;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = workingDirectory;
            // Prefer the freshly written multi-size U .ico next to the install; else EXE,0 (ApplicationIcon).
            string iconLocation = File.Exists(iconPath) ? iconPath + ",0" : target + ",0";
            shortcut.IconLocation = iconLocation;
            shortcut.Description = "Update Express fra MicroSystems";
            shortcut.Save();
            AgentLog.WriteInstall($"Shortcut written: {shortcutPath} → {target} (icon={iconLocation})");
            NotifyShellAssociationsChanged();
        }
        finally { if (System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void NotifyShellAssociationsChanged()
    {
        try
        {
            // SHCNE_ASSOCCHANGED | SHCNF_IDLIST — nudge Explorer to drop stale shortcut/EXE icons.
            const uint SHCNE_ASSOCCHANGED = 0x08000000;
            const uint SHCNF_IDLIST = 0x0000;
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { AgentLog.WriteInstall("SHChangeNotify skipped: " + ex.Message); }
    }
}
