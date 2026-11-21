using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using UpdateExpress;

namespace UpdateExpress.Services;

/// <summary>
/// Registers Microsoft Defender path/process exclusions for Update Express
/// (NetLock-style: install folders + EXE processes). Defender preferences are
/// machine-wide and require elevation. Operations are idempotent and never
/// fail the install if Defender is managed/unavailable — they log and continue.
/// </summary>
internal static class DefenderExclusions
{
    private const string ManifestFileName = "defender-exclusions.json";

    internal static string ProgramDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UpdateExpress");

    internal static string ManifestPath =>
        Path.Combine(InstallationManager.UserInstallDir, ManifestFileName);

    internal sealed class Manifest
    {
        public List<string> Paths { get; set; } = new();
        public List<string> Processes { get; set; } = new();
    }

    /// <summary>
    /// Folder + process set for the current install scope. Always includes the
    /// live user install dir and ProgramData audit folder; machine scope also
    /// includes Program Files. Process exclusions use full EXE paths when known,
    /// plus the bare UpdateExpress.exe / maintenance helper names.
    /// </summary>
    internal static Manifest BuildDesired(string targetDir, string targetExe, bool machine)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPath(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try { paths.Add(Path.GetFullPath(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))); }
            catch { paths.Add(p); }
        }

        void AddProcess(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            processes.Add(p.Trim());
        }

        AddPath(InstallationManager.UserInstallDir);
        AddPath(Path.Combine(InstallationManager.UserInstallDir, "Logs"));
        AddPath(ProgramDataDir);
        AddPath(targetDir);
        if (machine)
            AddPath(InstallationManager.MachineInstallDir);

        // Staged update/maintenance helpers live under %TEMP% — exclude the roots we create.
        AddPath(Path.Combine(Path.GetTempPath(), "MicroSystems-UpdateExpress-Update"));
        AddPath(Path.Combine(Path.GetTempPath(), "MicroSystems-UpdateExpress-Maintenance"));

        AddProcess(targetExe);
        AddProcess(InstallationManager.UserExe);
        if (machine || File.Exists(InstallationManager.MachineExe))
            AddProcess(InstallationManager.MachineExe);
        AddProcess("UpdateExpress.exe");
        AddProcess("UpdateExpress-Maintenance.exe");

        return new Manifest
        {
            Paths = paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            Processes = processes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    /// <summary>
    /// After install/repair/upgrade: ensure exclusions match the install.
    /// Elevates once when needed; on UAC cancel or Defender lock, logs and returns.
    /// </summary>
    internal static void EnsureRegistered(string targetDir, string targetExe, bool machine, bool silent)
    {
        var desired = BuildDesired(targetDir, targetExe, machine);
        SaveManifest(desired);
        Apply(desired, remove: false, silent);
    }

    /// <summary>
    /// Uninstall: remove only exclusions recorded in our manifest (plus current desired set).
    /// </summary>
    internal static void RemoveRegistered(string targetDir, string targetExe, bool machine, bool silent)
    {
        var desired = BuildDesired(targetDir, targetExe, machine);
        var recorded = LoadManifest() ?? desired;
        var union = new Manifest
        {
            Paths = desired.Paths.Union(recorded.Paths, StringComparer.OrdinalIgnoreCase).ToList(),
            Processes = desired.Processes.Union(recorded.Processes, StringComparer.OrdinalIgnoreCase).ToList()
        };
        Apply(union, remove: true, silent);
        TryDeleteManifest();
    }

    private static void Apply(Manifest manifest, bool remove, bool silent)
    {
        if (manifest.Paths.Count == 0 && manifest.Processes.Count == 0)
            return;

        if (!ElevationHelper.IsElevated)
        {
            if (silent)
            {
                // RMM/SYSTEM or non-interactive: never prompt UAC — install must not hang.
                AgentLog.WriteInstall(remove
                    ? "Defender exclusions: silent skip removal (not elevated)."
                    : "Defender exclusions: silent skip registration (not elevated).");
                return;
            }
            if (!TryElevateAndApply(manifest, remove, silent))
            {
                AgentLog.WriteInstall(remove
                    ? "Defender exclusions: skipped removal (not elevated / UAC cancelled)."
                    : "Defender exclusions: skipped registration (not elevated / UAC cancelled). Install continues; add exclusions manually if needed.");
            }
            return;
        }

        ApplyElevated(manifest, remove);
    }

    /// <summary>Entry for <c>--apply-defender-exclusions</c> / <c>--remove-defender-exclusions</c>.</summary>
    internal static void ApplyFromArgs(string[] args)
    {
        bool remove = args.Any(a => string.Equals(a, "--remove-defender-exclusions", StringComparison.OrdinalIgnoreCase));
        var manifest = LoadManifest();
        if (manifest is null || (manifest.Paths.Count == 0 && manifest.Processes.Count == 0))
        {
            // Fall back to defaults for current machine/user layout.
            bool machine = File.Exists(InstallationManager.MachineExe) && !File.Exists(InstallationManager.UserExe);
            string dir = machine ? InstallationManager.MachineInstallDir : InstallationManager.UserInstallDir;
            string exe = Path.Combine(dir, "UpdateExpress.exe");
            manifest = BuildDesired(dir, exe, machine);
        }
        if (!ElevationHelper.IsElevated)
        {
            AgentLog.WriteInstall("Defender exclusions helper started without elevation; exiting.");
            Environment.ExitCode = 2;
            return;
        }
        ApplyElevated(manifest, remove);
    }

    private static bool TryElevateAndApply(Manifest manifest, bool remove, bool silent)
    {
        try
        {
            SaveManifest(manifest);
            string exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine executable path.");
            string flag = remove ? "--remove-defender-exclusions" : "--apply-defender-exclusions";
            var info = new ProcessStartInfo(exe)
            {
                Arguments = flag,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };
            using var process = Process.Start(info);
            if (process is null) return false;
            // Bound wait so silent RMM installs do not hang forever on a stuck UAC prompt.
            if (!process.WaitForExit(silent ? 45_000 : 180_000))
            {
                AgentLog.WriteInstall("Defender exclusions: elevated helper timed out.");
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }
            AgentLog.WriteInstall($"Defender exclusions: elevated helper exit={process.ExitCode} remove={remove}");
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AgentLog.WriteInstall("Defender exclusions: UAC cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            AgentLog.WriteInstall("Defender exclusions: elevate failed: " + ex.Message);
            return false;
        }
    }

    private static void ApplyElevated(Manifest manifest, bool remove)
    {
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Continue'");
        script.AppendLine("$ok = $true");
        foreach (var path in manifest.Paths)
        {
            string lit = PsSingleQuote(path);
            if (remove)
                script.AppendLine($"try {{ Remove-MpPreference -ExclusionPath {lit} -ErrorAction Stop; Write-Output ('REMOVED_PATH ' + {lit}) }} catch {{ Write-Output ('SKIP_PATH ' + {lit} + ' :: ' + $_.Exception.Message); }}");
            else
                script.AppendLine($"try {{ $cur = @((Get-MpPreference -ErrorAction Stop).ExclusionPath); if ($cur -notcontains {lit}) {{ Add-MpPreference -ExclusionPath {lit} -ErrorAction Stop; Write-Output ('ADDED_PATH ' + {lit}) }} else {{ Write-Output ('HAVE_PATH ' + {lit}) }} }} catch {{ $ok = $false; Write-Output ('FAIL_PATH ' + {lit} + ' :: ' + $_.Exception.Message) }}");
        }
        foreach (var proc in manifest.Processes)
        {
            string lit = PsSingleQuote(proc);
            if (remove)
                script.AppendLine($"try {{ Remove-MpPreference -ExclusionProcess {lit} -ErrorAction Stop; Write-Output ('REMOVED_PROC ' + {lit}) }} catch {{ Write-Output ('SKIP_PROC ' + {lit} + ' :: ' + $_.Exception.Message); }}");
            else
                script.AppendLine($"try {{ $cur = @((Get-MpPreference -ErrorAction Stop).ExclusionProcess); if ($cur -notcontains {lit}) {{ Add-MpPreference -ExclusionProcess {lit} -ErrorAction Stop; Write-Output ('ADDED_PROC ' + {lit}) }} else {{ Write-Output ('HAVE_PROC ' + {lit}) }} }} catch {{ $ok = $false; Write-Output ('FAIL_PROC ' + {lit} + ' :: ' + $_.Exception.Message) }}");
        }
        script.AppendLine("if (-not $ok) { exit 1 } else { exit 0 }");

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell for Defender exclusions.");
            // ReadToEnd() alone can block forever if MpCmdlets hang — bound the whole helper.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            bool exited = p.WaitForExit(90_000);
            if (!exited)
            {
                AgentLog.WriteInstall("Defender exclusions: PowerShell timed out after 90s — killing helper (install continues).");
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { p.WaitForExit(5_000); } catch { /* ignore */ }
            }
            string stdout = stdoutTask.Wait(2_000) ? stdoutTask.Result : "";
            string stderr = stderrTask.Wait(2_000) ? stderrTask.Result : "";
            foreach (var line in (stdout + "\n" + stderr).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                AgentLog.WriteInstall("Defender exclusions: " + line);
            if (!exited)
                AgentLog.WriteInstall("Defender exclusions: skipped after timeout (install continues).");
            else if (p.ExitCode != 0)
                AgentLog.WriteInstall($"Defender exclusions: PowerShell exit={p.ExitCode} (install continues).");
            else
                AgentLog.WriteInstall(remove
                    ? $"Defender exclusions removed ({manifest.Paths.Count} paths, {manifest.Processes.Count} processes)."
                    : $"Defender exclusions ensured ({manifest.Paths.Count} paths, {manifest.Processes.Count} processes).");
        }
        catch (Exception ex)
        {
            AgentLog.WriteInstall("Defender exclusions: PowerShell failed: " + ex.Message);
        }
    }

    private static string PsSingleQuote(string value) =>
        "'" + (value ?? "").Replace("'", "''") + "'";

    private static void SaveManifest(Manifest manifest)
    {
        try
        {
            Directory.CreateDirectory(InstallationManager.UserInstallDir);
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ManifestPath, json);
        }
        catch (Exception ex)
        {
            AgentLog.WriteInstall("Defender exclusions: could not write manifest: " + ex.Message);
        }
    }

    private static Manifest? LoadManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return null;
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath));
        }
        catch (Exception ex)
        {
            AgentLog.WriteInstall("Defender exclusions: could not read manifest: " + ex.Message);
            return null;
        }
    }

    private static void TryDeleteManifest()
    {
        try { if (File.Exists(ManifestPath)) File.Delete(ManifestPath); }
        catch (Exception ex) { AgentLog.WriteInstall("Defender exclusions: could not delete manifest: " + ex.Message); }
    }
}
