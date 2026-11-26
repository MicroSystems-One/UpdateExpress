using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UpdateExpress.Services;

/// <summary>
/// Shared prompt/queue/status directory for automation helpers.
/// Elevating with a different admin identity must keep reading the tray user's files,
/// so elevated launches pass --automation-state-dir explicitly.
/// Prefer ProgramData (machine-wide) when available so any user session can see
/// scheduled Install-at / unattended notices and complete the same queues.
/// </summary>
internal static class AutomationStatePaths
{
    private static string? _overrideDirectory;

    /// <summary>Machine-wide automation state (any interactive user / elevated helper).</summary>
    internal static string SharedDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MicroSystems", "Update Express");

    internal static string Directory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_overrideDirectory))
                return _overrideDirectory!;
            // Prefer shared machine state only when the current identity can write it.
            // Read-only ProgramData (created by another admin) must not break tray Save().
            try
            {
                if (System.IO.Directory.Exists(SharedDirectory) &&
                    HasAutomationFiles(SharedDirectory) &&
                    IsDirectoryWritable(SharedDirectory))
                    return SharedDirectory;
            }
            catch { /* ignore */ }
            return InstallationManager.InstallDir;
        }
    }

    /// <summary>Probe create/delete — existence alone is not enough for Users without modify ACL.</summary>
    internal static bool IsDirectoryWritable(string dir)
    {
        try
        {
            if (!System.IO.Directory.Exists(dir)) return false;
            string probe = Path.Combine(dir, ".ue-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string ElevateArguments(string elevateArg)
    {
        // Always point the elevated helper at the machine shared dir. The helper
        // creates it (admin) and migrates prompt/queue files from this user's dir.
        // Also pass the source user dir so migration works across admin identities.
        string shared = SharedDirectory;
        string userDir = InstallationManager.InstallDir;
        try
        {
            System.IO.Directory.CreateDirectory(shared);
            TryMigrateUserStateIntoShared(shared);
        }
        catch { /* helper creates when elevated */ }
        return $"{elevateArg} --automation-state-dir=\"{shared}\" --automation-state-source=\"{userDir}\"";
    }

    /// <summary>
    /// Create ProgramData state dir when possible and migrate from the current user dir
    /// so scheduled queues survive user switch. Falls back to the current Directory.
    /// </summary>
    internal static string EnsureSharedDirectoryForElevate()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SharedDirectory);
            TryGrantUsersModify(SharedDirectory);
            TryMigrateUserStateIntoShared();
            _overrideDirectory = SharedDirectory;
            return SharedDirectory;
        }
        catch (Exception ex)
        {
            AgentLog.Write("Could not prefer shared automation state dir: " + ex.Message);
            return Directory;
        }
    }

    /// <summary>
    /// Best-effort ACL so interactive users can save prompts/queues after an elevated
    /// helper creates the ProgramData folder (avoids read-only shared state).
    /// Uses icacls to avoid an extra AccessControl package dependency.
    /// </summary>
    internal static void TryGrantUsersModify(string dir)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = $"\"{dir}\" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(8000);
        }
        catch (Exception ex)
        {
            AgentLog.Write("Could not grant Users modify on shared state dir: " + ex.Message);
        }
    }

    /// <summary>
    /// Public fallback when ProgramData is not writable (user-scope install).
    /// Readable by every interactive user on the PC.
    /// </summary>
    internal static string PublicNoticeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
        "MicroSystems", "Update Express");

    /// <summary>Best-effort publish so any tray can toast about Install at HH:mm.</summary>
    internal static void PublishSharedScheduleNotice(DateTime scheduledAt, string category, string detail)
    {
        var notice = new SharedScheduleNotice
        {
            ScheduledAt = scheduledAt,
            Category = category,
            Detail = detail,
            PublishedAt = DateTime.Now,
            PublishedBy = Environment.UserName
        };
        string json = JsonSerializer.Serialize(notice, new JsonSerializerOptions { WriteIndented = true });
        bool wrote = false;
        foreach (string dir in new[] { SharedDirectory, PublicNoticeDirectory, InstallationManager.InstallDir })
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "shared-schedule-notice.json"), json);
                wrote = true;
            }
            catch { /* try next */ }
        }
        if (!wrote)
            AgentLog.Write("Shared schedule notice publish failed on all candidate directories.");
    }

    internal static SharedScheduleNotice? LoadSharedScheduleNotice()
    {
        SharedScheduleNotice? best = null;
        foreach (string dir in new[] { SharedDirectory, PublicNoticeDirectory, InstallationManager.InstallDir })
        {
            try
            {
                string path = Path.Combine(dir, "shared-schedule-notice.json");
                if (!File.Exists(path)) continue;
                var notice = JsonSerializer.Deserialize<SharedScheduleNotice>(File.ReadAllText(path));
                if (notice is null) continue;
                if (best is null || notice.PublishedAt > best.PublishedAt)
                    best = notice;
            }
            catch { /* try next */ }
        }
        return best;
    }

    internal static void ClearSharedScheduleNotice()
    {
        foreach (string dir in new[] { SharedDirectory, PublicNoticeDirectory, InstallationManager.InstallDir })
        {
            try
            {
                string path = Path.Combine(dir, "shared-schedule-notice.json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Apply --automation-state-dir from elevated helper argv. Fail closed on invalid paths.
    /// Creates the shared ProgramData folder when that path is requested.
    /// </summary>
    internal static bool TryApplyFromArgs(string[] args)
    {
        string? raw = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--automation-state-dir=", StringComparison.OrdinalIgnoreCase))
            {
                raw = a["--automation-state-dir=".Length..].Trim().Trim('"');
                break;
            }
            if (string.Equals(a, "--automation-state-dir", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length)
            {
                raw = args[i + 1].Trim().Trim('"');
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!TryValidateStateDirectory(raw, out string full, out string reason, createIfShared: true))
        {
            AgentLog.Write("Rejected automation-state-dir: " + reason + " (" + raw + ")");
            return false;
        }

        _overrideDirectory = full;
        string? source = TryReadArgValue(args, "--automation-state-source");
        if (!string.IsNullOrWhiteSpace(source) &&
            TryValidateStateDirectory(source, out string sourceFull, out _, createIfShared: false))
        {
            TryMigrateFrom(sourceFull, full);
        }
        else
        {
            TryMigrateUserStateIntoShared(full);
        }
        AgentLog.Write("Automation state directory override: " + full);
        return true;
    }

    private static string? TryReadArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return a[(name.Length + 1)..].Trim().Trim('"');
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1].Trim().Trim('"');
        }
        return null;
    }

    internal static bool TryValidateStateDirectory(string candidate, out string fullPath, out string reason, bool createIfShared = false)
    {
        fullPath = "";
        reason = "";
        try
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                reason = "empty";
                return false;
            }
            if (candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                reason = "invalid characters";
                return false;
            }
            if (!Path.IsPathRooted(candidate))
            {
                reason = "not rooted";
                return false;
            }
            if (candidate.Contains("..", StringComparison.Ordinal))
            {
                reason = "path traversal";
                return false;
            }

            fullPath = Path.GetFullPath(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string name = Path.GetFileName(fullPath);
            string parent = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? "");
            if (!string.Equals(parent, "MicroSystems", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(name, "Update Express", StringComparison.OrdinalIgnoreCase))
            {
                reason = "must be ...\\MicroSystems\\Update Express";
                return false;
            }

            bool isShared = string.Equals(fullPath, Path.GetFullPath(SharedDirectory), StringComparison.OrdinalIgnoreCase);
            if (!System.IO.Directory.Exists(fullPath))
            {
                if (createIfShared && isShared)
                {
                    System.IO.Directory.CreateDirectory(fullPath);
                    TryGrantUsersModify(fullPath);
                }
                else
                {
                    reason = "directory does not exist";
                    return false;
                }
            }
            else if (createIfShared && isShared)
            {
                TryGrantUsersModify(fullPath);
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool HasAutomationFiles(string dir)
    {
        try
        {
            // pending-reboot.json counts on its own: a scheduled restart outlives the
            // prompt/queue files, and the tray agent and the service must still find it.
            return System.IO.Directory.EnumerateFiles(dir, "*-prompt.json").Any()
                   || File.Exists(Path.Combine(dir, "unattended-until-clear.json"))
                   || File.Exists(Path.Combine(dir, "pending-reboot.json"))
                   || File.Exists(Path.Combine(dir, "shared-schedule-notice.json"))
                   || System.IO.Directory.EnumerateFiles(dir, "*-install-queue.json").Any();
        }
        catch
        {
            return false;
        }
    }

    private static void TryMigrateUserStateIntoShared(string? shared = null) =>
        TryMigrateFrom(InstallationManager.InstallDir, shared ?? SharedDirectory);

    private static void TryMigrateFrom(string userDir, string shared)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(shared), Path.GetFullPath(userDir), StringComparison.OrdinalIgnoreCase))
                return;
        }
        catch { return; }
        if (!System.IO.Directory.Exists(userDir)) return;

        string[] names =
        {
            "unattended-until-clear.json",
            "pending-reboot.json",
            "critical-prompt.json", "critical-install-queue.json",
            "general-prompt.json", "general-install-queue.json",
            "applications-prompt.json", "applications-install-queue.json",
            "drivers-prompt.json", "drivers-install-queue.json",
            "windows-automation-status.json",
            "shared-schedule-notice.json"
        };
        foreach (string name in names)
        {
            try
            {
                string src = Path.Combine(userDir, name);
                string dst = Path.Combine(shared, name);
                if (!File.Exists(src)) continue;
                if (File.Exists(dst))
                {
                    // Prefer newer shared copy; still retire source so ClearPending
                    // cannot be undone by a later elevate+migrate.
                    if (File.GetLastWriteTimeUtc(src) <= File.GetLastWriteTimeUtc(dst))
                    {
                        try { File.Delete(src); } catch { /* best effort */ }
                        continue;
                    }
                }
                File.Copy(src, dst, overwrite: true);
                try { File.Delete(src); } catch { /* best effort */ }
            }
            catch { /* best effort */ }
        }
    }
}

internal sealed class SharedScheduleNotice
{
    public DateTime ScheduledAt { get; set; }
    public string Category { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public string PublishedBy { get; set; } = "";
}
