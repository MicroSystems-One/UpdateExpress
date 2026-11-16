using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UpdateExpress.Services;

namespace UpdateExpress;

/// <summary>
/// Agent + install/update diagnostic logging.
/// Install/upgrade/self-update/Defender steps always append to
/// %LOCALAPPDATA%\MicroSystems\Update Express\Logs\install-update.log
/// (and last-install.log for the newest session). Verbose mode also
/// mirrors general AgentLog lines into the same Logs folder.
/// </summary>
internal static class AgentLog
{
    internal static readonly string UserDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MicroSystems", "Update Express");

    internal static readonly string LogFile = Path.Combine(UserDataDir, "agent.log");
    internal static readonly string LogsDir = Path.Combine(UserDataDir, "Logs");
    internal static readonly string InstallUpdateLogFile = Path.Combine(LogsDir, "install-update.log");
    internal static readonly string LastInstallLogFile = Path.Combine(LogsDir, "last-install.log");
    internal static readonly string VerboseLogFile = Path.Combine(LogsDir, "verbose.log");

    private static readonly object Gate = new();
    private static StringBuilder? _sessionBuffer;
    private static string? _sessionId;

    internal static void Write(string message)
    {
        string line = FormatLine(message);
        Append(LogFile, line);
        if (IsVerboseEnabled())
            Append(VerboseLogFile, line);
    }

    /// <summary>Always-on install/upgrade/self-update/Defender pipeline log (SVHA upload target).</summary>
    internal static void WriteInstall(string message)
    {
        string line = FormatLine(message);
        Append(LogFile, line);
        Append(InstallUpdateLogFile, line);
        lock (Gate)
        {
            _sessionBuffer ??= new StringBuilder();
            _sessionBuffer.Append(line);
        }
        if (IsVerboseEnabled())
            Append(VerboseLogFile, line);
    }

    internal static void BeginInstallSession(string operation, string? detail = null)
    {
        _sessionId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{operation}";
        lock (Gate) { _sessionBuffer = new StringBuilder(); }
        WriteInstall($"======== BEGIN {operation.ToUpperInvariant()} session={_sessionId} pid={Environment.ProcessId} ========");
        if (!string.IsNullOrWhiteSpace(detail))
            WriteInstall(detail);
        WriteInstall($"User={Environment.UserName}; Elevated={ElevationHelper.IsElevated}; Process={Environment.ProcessPath}");
        WriteInstall($"UserInstallDir={InstallationManager.UserInstallDir}");
        WriteInstall($"MachineInstallDir={InstallationManager.MachineInstallDir}");
    }

    internal static void EndInstallSession(string operation, bool ok, string? summary = null)
    {
        if (!string.IsNullOrWhiteSpace(summary))
            WriteInstall(summary);
        WriteInstall($"======== END {operation.ToUpperInvariant()} ok={ok} session={_sessionId ?? "?"} ========");
        FlushLastInstallLog();
    }

    /// <summary>Record whether a path exists (size/mtime) — used to spot Defender deleting EXE/shortcuts after install.</summary>
    internal static void WritePathProbe(string label, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                WriteInstall($"{label}: EXISTS path={path}; size={info.Length}; mtime={info.LastWriteTime:O}");
            }
            else if (Directory.Exists(path))
            {
                WriteInstall($"{label}: EXISTS (dir) path={path}");
            }
            else
            {
                WriteInstall($"{label}: MISSING path={path}");
            }
        }
        catch (Exception ex)
        {
            WriteInstall($"{label}: PROBE_FAILED path={path}; error={ex.Message}");
        }
    }

    /// <summary>
    /// After install/upgrade: wait briefly then re-check EXE + shortcuts so SVHA can see
    /// if Defender (or another AV) removed them shortly after write.
    /// </summary>
    internal static void PostInstallPresenceCheck(string targetExe, params string?[] shortcutPaths)
    {
        WriteInstall("Post-install presence check: immediate");
        WritePathProbe("EXE", targetExe);
        foreach (var p in shortcutPaths)
        {
            if (!string.IsNullOrWhiteSpace(p))
                WritePathProbe("Shortcut", p!);
        }

        try { System.Threading.Thread.Sleep(2500); }
        catch { /* ignore */ }

        WriteInstall("Post-install presence check: +2.5s (Defender/AV delete window)");
        WritePathProbe("EXE", targetExe);
        foreach (var p in shortcutPaths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            WritePathProbe("Shortcut", p!);
        }
    }

    internal static bool IsVerboseEnabled()
    {
        try { return ReminderSchedule.Load().VerboseInstallLogging; }
        catch { return false; }
    }

    internal static void OpenLogsFolder()
    {
        Directory.CreateDirectory(LogsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogsDir}\"") { UseShellExecute = true });
    }

    internal static void OpenLastInstallLog()
    {
        Directory.CreateDirectory(LogsDir);
        string path = File.Exists(LastInstallLogFile) ? LastInstallLogFile
            : File.Exists(InstallUpdateLogFile) ? InstallUpdateLogFile
            : LogFile;
        if (!File.Exists(path))
        {
            Append(InstallUpdateLogFile, FormatLine("Log file created — run Install / Repair / Update once to populate."));
            path = InstallUpdateLogFile;
        }
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Copy last-install.log (or install-update.log) to a user-chosen path.</summary>
    internal static string? ExportLastInstallLog(string destinationPath)
    {
        Directory.CreateDirectory(LogsDir);
        string source = File.Exists(LastInstallLogFile) ? LastInstallLogFile
            : File.Exists(InstallUpdateLogFile) ? InstallUpdateLogFile
            : null!;
        if (source is null || !File.Exists(source))
            return null;
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(source, destinationPath, overwrite: true);
        return destinationPath;
    }

    private static void FlushLastInstallLog()
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            string text;
            lock (Gate)
            {
                text = _sessionBuffer?.ToString() ?? "";
            }
            if (string.IsNullOrEmpty(text) && File.Exists(InstallUpdateLogFile))
            {
                // Fallback: last 200 KB of rolling log.
                var bytes = File.ReadAllBytes(InstallUpdateLogFile);
                int take = Math.Min(bytes.Length, 200_000);
                text = Encoding.UTF8.GetString(bytes, bytes.Length - take, take);
            }
            File.WriteAllText(LastInstallLogFile, text, Encoding.UTF8);
        }
        catch { /* never fail install on log flush */ }
    }

    private static string FormatLine(string message) =>
        $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";

    private static void Append(string path, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            lock (Gate)
            {
                File.AppendAllText(path, line);
            }
        }
        catch { /* Logging must never prevent startup/install. */ }
    }
}
