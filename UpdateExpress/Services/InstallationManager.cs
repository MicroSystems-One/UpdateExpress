using System;
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
    private const string DesktopIconName = "UpdateExpress-U-v0.7.2.6.35.ico";
    private const string DesktopIconResource = "UpdateExpress.Assets.UpdateExpress.ico";
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

    private static void LaunchMaintenance(string operation, bool allUsers, bool desktop, bool startMenu, bool agent)
    {
        string source = Environment.ProcessPath ?? throw new InvalidOperationException("Fant ikke programfilen.");
        // Stage the helper outside InstallDir so the target file can be replaced safely.
        string helperDir = Path.Combine(Path.GetTempPath(), "MicroSystems-UpdateExpress-Maintenance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDir);
        string helper = Path.Combine(helperDir, "UpdateExpress-Maintenance.exe");
        File.Copy(source, helper);
        int parentPid = Environment.ProcessId;
        string arguments = $"--apply-maintenance {operation} {parentPid} \"{source}\" {(allUsers ? "machine" : "user")} {(desktop ? 1 : 0)} {(startMenu ? 1 : 0)} {(agent ? 1 : 0)}";
        AgentLog.Write($"Launching detached maintenance: {operation} (parent {parentPid})");
        using var maintenanceProcess = Process.Start(new ProcessStartInfo(helper, arguments) { UseShellExecute = true, WorkingDirectory = helperDir, Verb = allUsers ? "runas" : "open" })
            ?? throw new InvalidOperationException("Kunne ikke starte vedlikeholdsprosessen.");
        // Release any locks held by this process before the helper touches installed files.
        System.Windows.Application.Current.Shutdown();
    }

    internal static void ApplyMaintenance(string[] args)
    {
        // Arguments: --apply-maintenance <install|repair|uninstall> <parentPID> <sourceEXE>
        if (args.Length != 8 || !new[] { "install", "repair", "uninstall" }.Contains(args[1], StringComparer.OrdinalIgnoreCase)
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
            try { using var parent = Process.GetProcessById(parentPid); parent.WaitForExit(15000); }
            catch (ArgumentException) { /* Parent has exited already. */ }
            if (operation == "uninstall" || operation == "install" || operation == "repair")
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
                using (var key = hive.CreateSubKey(UninstallKey))
                {
                    key.SetValue("DisplayName", "Update Express – MicroSystems");
                    key.SetValue("DisplayVersion", "0.7.0.0");
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
                AgentLog.Write($"Install/repair completed: scope={(machine ? "machine" : "user")}, agent={agent}, by={System.Security.Principal.WindowsIdentity.GetCurrent().Name}.");
                string completedAction = operation == "repair" ? "repaired" : operation == "upgrade" ? "updated" : "installed";
                string scopeText = machine ? "all users" : "the current user";
                MessageBox.Show($"Update Express was {completedAction} successfully for {scopeText}.\n\n"
                    + (agent ? "Background monitoring is enabled for this user." : "Background monitoring is disabled.")
                    + "\n\nYou can pin Update Express to the taskbar from the Start menu.",
                    "Update Express", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write("Maintenance FAILED: " + ex);
            MessageBox.Show("Vedlikehold mislyktes: " + ex.Message + "\n\nSe agent.log for detaljer.",
                "Update Express", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void StopInstalledAgent(string targetExe)
    {
        foreach (var proc in Process.GetProcessesByName("UpdateExpress"))
        {
            using (proc)
            {
                try
                {
                    // Never terminate unrelated processes or the detached helper.
                    if (proc.Id == Environment.ProcessId) continue;
                    string? path = proc.MainModule?.FileName;
                    if (!string.Equals(path, targetExe, StringComparison.OrdinalIgnoreCase)) continue;
                    // Only stop --agent processes. If the user has the main UI open, show a clear error.
                    bool isAgent = File.Exists(AgentPidFile) && int.TryParse(File.ReadAllText(AgentPidFile), out int agentPid) && agentPid == proc.Id;
                    if (!isAgent)
                        throw new IOException("Lukk det installerte Update Express-vinduet før oppgradering eller avinstallasjon.");
                    proc.Kill();
                    if (!proc.WaitForExit(10000)) throw new IOException("Bakgrunnsagenten kunne ikke avsluttes.");
                    AgentLog.Write("Stopped installed agent PID " + proc.Id);
                }
                catch (System.ComponentModel.Win32Exception ex)
                { throw new IOException("Kunne ikke kontrollere/stoppe Update Express-prosessen: " + ex.Message, ex); }
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
