using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

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
    private static readonly string Shortcut = Path.Combine(StartMenuDir, "Update Express.lnk");
    private const string DesktopIconName = "UpdateExpress-U-v0.7.2.6.60.ico";
    private const string DesktopIconResource = "UpdateExpress.Assets.UpdateExpress.ico";
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
        if (repair && IsInstalled && dialog.AllUsers != (RunningMachineCopy || (!File.Exists(UserExe) && File.Exists(MachineExe))))
            throw new InvalidOperationException("Repair must use the existing installation scope. Use Install to change scope.");
        LaunchMaintenance(repair ? "repair" : "install", dialog.AllUsers, dialog.DesktopShortcut, dialog.StartMenuShortcut, dialog.BackgroundAgent);
    }

    internal static void Uninstall()
    {
        if (MessageBox.Show("Avinstallere Update Express?\n\nOppdateringshistorikk og innstillinger beholdes.",
            "Update Express", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        LaunchMaintenance("uninstall", RunningMachineCopy || (!File.Exists(UserExe) && File.Exists(MachineExe)), false, false, false);
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
        string menuShortcut = Path.Combine(menuDir, "Update Express.lnk");
        string desktopShortcut = Path.Combine(
            Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory),
            "Update Express.lnk");
        bool desktop = File.Exists(desktopShortcut);
        bool startMenu = File.Exists(menuShortcut);
        bool agent = false;
        if (!machine)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            agent = key?.GetValue(RunName) is string;
        }
        AgentLog.Write($"Launching self-update: package={verifiedPackagePath}; scope={(machine ? "machine" : "user")}; agent={agent}");
        LaunchMaintenance("upgrade", machine, desktop, startMenu, agent, verifiedPackagePath);
    }

    private static void LaunchMaintenance(string operation, bool allUsers, bool desktop, bool startMenu, bool agent, string? packageSource = null)
    {
        string running = Environment.ProcessPath ?? throw new InvalidOperationException("Fant ikke programfilen.");
        string source = packageSource ?? running;
        // Stage a sibling helper (different file name) so UpdateExpress.exe can be replaced safely.
        // Prefer the install directory over %TEMP% — App Control frequently blocks Temp launches.
        string helper = StageMaintenanceHelper(running, allUsers);
        string helperDir = Path.GetDirectoryName(helper) ?? UserInstallDir;
        int parentPid = Environment.ProcessId;
        string arguments = $"--apply-maintenance {operation} {parentPid} \"{source}\" {(allUsers ? "machine" : "user")} {(desktop ? 1 : 0)} {(startMenu ? 1 : 0)} {(agent ? 1 : 0)}";
        AgentLog.Write($"Launching detached maintenance: {operation} (parent {parentPid}); helper={helper}");
        try
        {
            using var maintenanceProcess = Process.Start(new ProcessStartInfo(helper, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = helperDir,
                Verb = allUsers ? "runas" : "open"
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
            AgentLog.Write("Dispatcher shutdown failed; forcing process exit: " + ex.Message);
            Environment.Exit(0);
        }
    }

    internal static void ApplyMaintenance(string[] args)
    {
        // Arguments: --apply-maintenance <install|repair|uninstall|upgrade> <parentPID> <sourceEXE>
        if (args.Length != 8 || !new[] { "install", "repair", "uninstall", "upgrade" }.Contains(args[1], StringComparer.OrdinalIgnoreCase)
            || !int.TryParse(args[2], out int parentPid))
            throw new ArgumentException("Ugyldige parametere for vedlikehold.");
        string operation = args[1].ToLowerInvariant();
        string source = Path.GetFullPath(args[3]);
        bool machine = args[4] == "machine";
        bool desktop = args[5] == "1", startMenu = args[6] == "1", agent = args[7] == "1";
        // Machine-wide auto-start needs a separate per-user registration flow; not supported in this preview.
        if (machine) agent = false;
        string targetDir = machine ? MachineInstallDir : UserInstallDir;
        string targetExe = Path.Combine(targetDir, "UpdateExpress.exe");
        string menuDir = machine ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "MicroSystems") : StartMenuDir;
        string menuShortcut = Path.Combine(menuDir, "Update Express.lnk");
        string desktopShortcut = Path.Combine(Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory), "Update Express.lnk");
        var hive = machine ? Registry.LocalMachine : Registry.CurrentUser;
        string iconPath = Path.Combine(targetDir, DesktopIconName);
        if (!File.Exists(source) && operation != "uninstall")
            throw new FileNotFoundException("Fant ikke installasjonsfilen.", source);
        AgentLog.Write($"Maintenance helper started: {operation}, source={source}");
        try
        {
            // Wait for the launcher that started us to close.
            try { using var parent = Process.GetProcessById(parentPid); parent.WaitForExit(30000); }
            catch (ArgumentException) { /* Parent has exited already. */ }
            if (operation == "upgrade")
                StopInstalledProcesses(targetExe);
            else if (operation == "uninstall" || operation == "install" || operation == "repair")
                StopInstalledAgent(targetExe);
            if (operation == "uninstall")
            {
                if (!machine) using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true)) key?.DeleteValue(RunName, false);
                hive.DeleteSubKeyTree(UninstallKey, false);
                if (File.Exists(menuShortcut)) File.Delete(menuShortcut);
                if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);
                if (File.Exists(targetExe)) RetryFileOperation(() => File.Delete(targetExe));
                if (File.Exists(iconPath)) RetryFileOperation(() => File.Delete(iconPath));
                AgentLog.Write("Uninstall completed.");
                MessageBox.Show("Update Express er avinstallert. Historikk og innstillinger er beholdt.", "Update Express");
            }
            else
            {
                Directory.CreateDirectory(targetDir);
                // The staged helper must not be copied over itself; source can be an installed or published EXE.
                // For repair from installed EXE, existing program file is left unchanged.
                if (!string.Equals(source, targetExe, StringComparison.OrdinalIgnoreCase))
                    RetryFileOperation(() => File.Copy(source, targetExe, true));
                else if (!File.Exists(targetExe))
                    throw new FileNotFoundException("Programfilen mangler. Kjør Installer fra den nye Publisert-mappen.");
                WriteInstalledIcon(iconPath);
                if (startMenu) { Directory.CreateDirectory(menuDir); CreateShortcut(menuShortcut, targetExe, "--installed", targetDir, iconPath); }
                else if (File.Exists(menuShortcut)) File.Delete(menuShortcut);
                if (desktop) CreateShortcut(desktopShortcut, targetExe, "--installed", targetDir, iconPath);
                else if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);
                string displayVersion = ReadDisplayVersion(targetExe);
                using (var key = hive.CreateSubKey(UninstallKey))
                {
                    key.SetValue("DisplayName", "Update Express – MicroSystems");
                    key.SetValue("DisplayVersion", displayVersion);
                    key.SetValue("Publisher", "MicroSystems");
                    key.SetValue("InstallLocation", targetDir);
                    key.SetValue("DisplayIcon", targetExe);
                    key.SetValue("UninstallString", $"\"{targetExe}\" --uninstall");
                    key.SetValue("NoModify", 0, RegistryValueKind.DWord);
                }
                if (!machine) using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (agent) key.SetValue(RunName, $"\"{targetExe}\" --agent");
                    else key.DeleteValue(RunName, false);
                }
                if (agent) Process.Start(new ProcessStartInfo(targetExe, "--agent")
                    { UseShellExecute = true, WorkingDirectory = targetDir });
                if (operation == "upgrade")
                    Process.Start(new ProcessStartInfo(targetExe, "--installed")
                    { UseShellExecute = true, WorkingDirectory = targetDir });
                AgentLog.Write($"{operation} completed: scope={(machine ? "machine" : "user")}, agent={agent}, version={displayVersion}, by={System.Security.Principal.WindowsIdentity.GetCurrent().Name}.");
                string completedAction = operation == "repair" ? "repaired" : operation == "upgrade" ? "updated" : "installed";
                string scopeText = machine ? "all users" : "the current user";
                MessageBox.Show($"Update Express was {completedAction} successfully for {scopeText}.\n\n"
                    + $"Version: {displayVersion}\n"
                    + (agent ? "Background monitoring is enabled for this user." : "Background monitoring is disabled.")
                    + (operation == "upgrade" ? "\n\nUpdate Express will open now." : "\n\nYou can pin Update Express to the taskbar from the Start menu."),
                    "Update Express", MessageBoxButton.OK, MessageBoxImage.Information);
                TryDeleteTree(Path.GetDirectoryName(source));
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write("Maintenance FAILED: " + ex);
            string detail = IsAppControlBlock(ex)
                ? FormatMaintenanceLaunchFailure(Environment.ProcessPath ?? MaintenanceHelperFileName, ex)
                : ex.Message;
            MessageBox.Show("Vedlikehold mislyktes: " + detail + "\n\nSe agent.log for detaljer.",
                "Update Express", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        catch (Exception ex) { AgentLog.Write("Could not delete staged update package folder: " + ex.Message); }
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
                            AgentLog.Write("Closed Update Express UI PID " + proc.Id + " for upgrade.");
                            continue;
                        }
                    }

                    proc.Kill(entireProcessTree: true);
                    if (!proc.WaitForExit(10000))
                        throw new IOException("Update Express could not be stopped for upgrade.");
                    AgentLog.Write("Stopped Update Express process PID " + proc.Id + " for upgrade.");
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
                    AgentLog.Write("Stopped installed agent PID " + proc.Id);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    throw new IOException("Kunne ikke kontrollere/stoppe Update Express-prosessen: " + ex.Message, ex);
                }
            }
        }
    }

    internal static readonly string AgentPidFile = Path.Combine(UserInstallDir, "agent.pid");

    private static void RetryFileOperation(Action action)
    {
        IOException? last = null;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            try { action(); return; }
            catch (IOException ex) { last = ex; System.Threading.Thread.Sleep(500); }
        }
        throw new IOException("Programfilen er fortsatt låst. Lukk Update Express og prøv igjen.", last);
    }

    private static void WriteInstalledIcon(string destination)
    {
        using var resource = typeof(InstallationManager).Assembly.GetManifestResourceStream(DesktopIconResource)
            ?? throw new InvalidOperationException("Embedded U icon resource was not found: " + DesktopIconResource);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        resource.CopyTo(output);
    }

    private static void CreateShortcut(string shortcutPath, string target, string arguments, string workingDirectory, string iconPath)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host mangler.");
        object shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })!;
            shortcut.TargetPath = target;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = iconPath + ",0";
            shortcut.Description = "Update Express fra MicroSystems";
            shortcut.Save();
        }
        finally { if (System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
    }
}
