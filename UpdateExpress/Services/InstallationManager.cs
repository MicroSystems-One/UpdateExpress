using System;
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

    // Maintenance is always performed by a temporary, detached copy of this executable.
    // The installed EXE is never asked to overwrite or delete itself.
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
        string menuShortcut = Path.Combine(menuDir, ShortcutFileName);
        string desktopShortcut = Path.Combine(
            Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory),
            ShortcutFileName);
        bool desktop = HasAppShortcut(EnumerateDesktopFolders()) || File.Exists(desktopShortcut);
        bool startMenu = HasAppShortcut(EnumerateStartMenuFolders()) || File.Exists(menuShortcut);
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
        // Stage the helper outside InstallDir so the target file can be replaced safely.
        // Helper must be the currently running build (contains this maintenance code).
        string helperDir = Path.Combine(Path.GetTempPath(), "MicroSystems-UpdateExpress-Maintenance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDir);
        string helper = Path.Combine(helperDir, "UpdateExpress-Maintenance.exe");
        File.Copy(running, helper);
        int parentPid = Environment.ProcessId;
        string arguments = $"--apply-maintenance {operation} {parentPid} \"{source}\" {(allUsers ? "machine" : "user")} {(desktop ? 1 : 0)} {(startMenu ? 1 : 0)} {(agent ? 1 : 0)}";
        AgentLog.Write($"Launching detached maintenance: {operation} (parent {parentPid})");
        using var maintenanceProcess = Process.Start(new ProcessStartInfo(helper, arguments) { UseShellExecute = true, WorkingDirectory = helperDir, Verb = allUsers ? "runas" : "open" })
            ?? throw new InvalidOperationException("Kunne ikke starte vedlikeholdsprosessen.");
        // Release locks before the helper replaces files. Shutdown must run on the WPF UI thread
        // (tray self-update downloads on a background thread).
        ShutdownCurrentApplication();
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
        string menuShortcut = Path.Combine(menuDir, ShortcutFileName);
        string desktopShortcut = Path.Combine(Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory), ShortcutFileName);
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
                RemoveAppShortcuts(EnumerateDesktopFolders(), keepPath: null);
                RemoveAppShortcuts(EnumerateStartMenuFolders(), keepPath: null);
                if (File.Exists(targetExe)) RetryFileOperation(() => File.Delete(targetExe), targetExe);
                if (File.Exists(iconPath)) RetryFileOperation(() => File.Delete(iconPath), iconPath);
                TryDeleteLegacyVersionedIcons(targetDir);
                AgentLog.Write("Uninstall completed.");
                MessageBox.Show("Update Express er avinstallert. Historikk og innstillinger er beholdt.", "Update Express");
            }
            else
            {
                Directory.CreateDirectory(targetDir);
                // The staged helper must not be copied over itself; source can be an installed or published EXE.
                // For repair from installed EXE, existing program file is left unchanged.
                if (!string.Equals(source, targetExe, StringComparison.OrdinalIgnoreCase))
                    RetryFileOperation(() => File.Copy(source, targetExe, true), targetExe);
                else if (!File.Exists(targetExe))
                    throw new FileNotFoundException("Programfilen mangler. Kjør Installer fra den nye Publisert-mappen.");
                RetryFileOperation(() => WriteInstalledIcon(iconPath), iconPath);
                TryDeleteLegacyVersionedIcons(targetDir);
                // Always strip duplicate/orphan Update Express shortcuts first so upgrades leave one icon.
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
                AutoCloseInfoDialog.Show(
                    $"Update Express was {completedAction} successfully for {scopeText}.\n\n"
                    + $"Version: {displayVersion}\n"
                    + (agent ? "Background monitoring is enabled for this user." : "Background monitoring is disabled.")
                    + (operation == "upgrade" ? "\n\nUpdate Express will open now." : "\n\nYou can pin Update Express to the taskbar from the Start menu."),
                    "Update Express",
                    autoCloseSeconds: 10);
                TryDeleteTree(Path.GetDirectoryName(source));
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write("Maintenance FAILED: " + ex);
            MessageBox.Show("Vedlikehold mislyktes:\n\n" + FormatMaintenanceError(ex) + "\n\nSe agent.log for detaljer.",
                "Update Express", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
                catch (Exception ex) { AgentLog.Write("Could not delete legacy icon " + file + ": " + ex.Message); }
            }
        }
        catch (Exception ex) { AgentLog.Write("Legacy icon cleanup skipped: " + ex.Message); }
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
                AgentLog.Write("Removed duplicate/orphan shortcut: " + full);
            }
            catch (Exception ex)
            {
                AgentLog.Write("Could not remove shortcut " + path + ": " + ex.Message);
            }
        }
    }

    private static void CreateShortcut(string shortcutPath, string target, string arguments, string workingDirectory, string iconPath)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host mangler.");
        object shell = Activator.CreateInstance(shellType)!;
        try
        {
            // Rewrite the same path in place; callers remove numbered duplicates first.
            if (File.Exists(shortcutPath))
            {
                try { File.Delete(shortcutPath); }
                catch (Exception ex) { AgentLog.Write("Could not replace existing shortcut before recreate: " + ex.Message); }
            }
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
