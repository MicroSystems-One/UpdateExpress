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
            return ResolveDirectory(SharedDirectory, InstallationManager.InstallDir);
        }
    }

    /// <summary>
    /// The shared folder when it already holds automation files and the current identity can
    /// change them; otherwise <paramref name="fallback"/>. A shared folder this identity can
    /// only read is left to the processes that can write it (elevated helper, service).
    /// </summary>
    internal static string ResolveDirectory(string shared, string fallback)
    {
        try
        {
            if (System.IO.Directory.Exists(shared) &&
                HasAutomationFiles(shared) &&
                IsDirectoryWritableCached(shared))
                return shared;
        }
        catch { /* ignore */ }
        return fallback;
    }

    // Directory is read on every state access (the tray polls pending-reboot.json every 5 s),
    // so the probe result is kept for a while. A "no" is kept only briefly: it turns into a
    // "yes" as soon as a helper or the service grants Users modify.
    private static readonly TimeSpan WritableYesTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WritableNoTtl = TimeSpan.FromSeconds(5);
    private static readonly object WritableLock = new();
    private static string? _writableDir;
    private static bool _writable;
    private static long _writableCheckedTicks;

    private static bool IsDirectoryWritableCached(string dir)
    {
        long now = Environment.TickCount64;
        lock (WritableLock)
        {
            if (string.Equals(_writableDir, dir, StringComparison.OrdinalIgnoreCase) &&
                now - _writableCheckedTicks < (long)(_writable ? WritableYesTtl : WritableNoTtl).TotalMilliseconds)
                return _writable;
        }

        bool writable = IsDirectoryWritable(dir);
        lock (WritableLock)
        {
            _writableDir = dir;
            _writable = writable;
            _writableCheckedTicks = now;
        }
        return writable;
    }

    private static void ForgetWritableProbe()
    {
        lock (WritableLock) _writableDir = null;
    }

    /// <summary>
    /// True when the current identity can create files in <paramref name="dir"/> and change
    /// every state file already there. Creating a file is not enough on its own: under the
    /// default ProgramData ACL every user may add files, but a state file written by an
    /// elevated helper, the service or another user is read-only to everyone else.
    /// </summary>
    internal static bool IsDirectoryWritable(string dir)
    {
        try
        {
            if (!System.IO.Directory.Exists(dir)) return false;
            string probe = Path.Combine(dir, ".ue-write-probe-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            foreach (string file in EnumerateStateFiles(dir))
            {
                if (!CanOpenForWrite(file)) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanOpenForWrite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            // Gone meanwhile, or another process is writing it right now: not a rights problem.
            return true;
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
            TryGrantUsersModify(shared);
            // Move state over only where this user can keep writing it. Otherwise it stays
            // here, where Directory still finds it, and the helper migrates it from the source dir.
            if (IsDirectoryWritable(shared))
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
            if (!IsDirectoryWritable(SharedDirectory))
            {
                AgentLog.Write("Shared automation state dir is not writable for this user; state stays in " + Directory);
                return Directory;
            }
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
    /// Grant on the shared folder if it exists. For the session-0 service: the files it
    /// creates there as SYSTEM must stay writable for the signed-in user's agent.
    /// </summary>
    internal static void EnsureSharedDirectoryAccess()
    {
        try
        {
            if (System.IO.Directory.Exists(SharedDirectory))
                TryGrantUsersModify(SharedDirectory);
        }
        catch { /* best effort */ }
    }

    private static readonly object GrantLock = new();
    private static string? _grantedDir;
    private static DateTime _grantedDirCreatedUtc;
    private static bool _grantFailureLogged;

    /// <summary>
    /// Best-effort ACL so interactive users can save prompts/queues after an elevated
    /// helper creates the ProgramData folder (avoids read-only shared state).
    /// Uses icacls to avoid an extra AccessControl package dependency. The grant is
    /// inherited by files created later, so one success per process and folder is enough.
    /// Succeeds only for an administrator, SYSTEM or the folder's owner.
    /// </summary>
    internal static bool TryGrantUsersModify(string dir)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !System.IO.Directory.Exists(dir)) return false;
            DateTime createdUtc = System.IO.Directory.GetCreationTimeUtc(dir);
            lock (GrantLock)
            {
                if (string.Equals(_grantedDir, dir, StringComparison.OrdinalIgnoreCase) &&
                    _grantedDirCreatedUtc == createdUtc)
                    return true;
            }

            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
                Arguments = $"\"{dir}\" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc is null) return false;
            if (!proc.WaitForExit(8000))
            {
                LogGrantFailure(dir, "icacls did not finish in time");
                return false;
            }
            if (proc.ExitCode != 0)
            {
                LogGrantFailure(dir, "icacls exit code " + proc.ExitCode);
                return false;
            }

            lock (GrantLock)
            {
                _grantedDir = dir;
                _grantedDirCreatedUtc = createdUtc;
            }
            ForgetWritableProbe();
            return true;
        }
        catch (Exception ex)
        {
            LogGrantFailure(dir, ex.Message);
            return false;
        }
    }

    private static void LogGrantFailure(string dir, string reason)
    {
        lock (GrantLock)
        {
            if (_grantFailureLogged) return;
            _grantFailureLogged = true;
        }
        AgentLog.Write($"Could not grant Users modify on {dir}: {reason}");
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

    private static readonly string[] FixedStateFileNames =
    {
        "unattended-until-clear.json",
        "pending-reboot.json",
        "windows-automation-status.json",
        "winget-available.json",
        "shared-schedule-notice.json"
    };

    /// <summary>The state files processes read and write through <see cref="Directory"/>.</summary>
    private static System.Collections.Generic.IEnumerable<string> EnumerateStateFiles(string dir) =>
        System.IO.Directory.EnumerateFiles(dir, "*-prompt.json")
            .Concat(System.IO.Directory.EnumerateFiles(dir, "*-install-queue.json"))
            .Concat(FixedStateFileNames.Select(name => Path.Combine(dir, name)).Where(File.Exists));

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
