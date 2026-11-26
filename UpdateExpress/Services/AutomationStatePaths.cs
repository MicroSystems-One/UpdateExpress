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
/// The state lives in its own <c>State</c> subfolder, which every user may modify. The folder
/// above it holds fleet-policy.json and stays under the administrator's control.
/// </summary>
internal static class AutomationStatePaths
{
    private static string? _overrideDirectory;

    /// <summary>
    /// %ProgramData%\MicroSystems\Update Express: fleet-policy.json, the policy mirror and the
    /// RMM status files. Automation state lived here up to 0.7.2.61 and is moved out of it by
    /// <see cref="MigrateLegacyState"/>. Update Express grants nobody rights on this folder.
    /// </summary>
    internal static string LegacySharedDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MicroSystems", "Update Express");

    /// <summary>Machine-wide automation state (any interactive user / elevated helper).</summary>
    internal static string SharedDirectory => Path.Combine(LegacySharedDirectory, "State");

    internal static string Directory
    {
        get
        {
            // First, so that no process reads state before what an older version left in the
            // folder above has been brought here.
            MaintainSharedState();
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
        MaintainSharedState();
        string shared = SharedDirectory;
        string userDir = InstallationManager.InstallDir;
        try
        {
            EnsureSharedDirectory(shared);
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
        MaintainSharedState();
        try
        {
            EnsureSharedDirectory(SharedDirectory);
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
    /// For the session-0 service: bring over state an older version left in the folder above,
    /// and grant on the shared folder if it exists, so the files the service creates there as
    /// SYSTEM stay writable for the signed-in user's agent.
    /// </summary>
    internal static void EnsureSharedDirectoryAccess()
    {
        MaintainSharedState(force: true);
        try
        {
            if (System.IO.Directory.Exists(SharedDirectory))
                TryGrantUsersModify(SharedDirectory);
        }
        catch { /* best effort */ }
    }

    /// <summary>Every place that creates the shared folder also grants Users modify on it.</summary>
    private static void EnsureSharedDirectory(string shared)
    {
        System.IO.Directory.CreateDirectory(shared);
        TryGrantUsersModify(shared);
    }

    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(60);
    private static readonly object MaintenanceLock = new();
    private static bool _maintaining;
    private static bool _maintainedOnce;
    private static long _maintainedTicks;
    private static bool _legacyGrantHandled;

    /// <summary>
    /// Set by the unit test assembly: a test run must never move the state of the Update
    /// Express that is installed on the PC it runs on.
    /// </summary>
    internal static bool SharedStateMaintenanceDisabled { get; set; }

    /// <summary>
    /// Housekeeping every process does before it reads state, at most once a minute: bring the
    /// state files an older version left in <see cref="LegacySharedDirectory"/> into
    /// <see cref="SharedDirectory"/>, and once per process take back the Users modify grant
    /// that builds after 0.7.2.61 put on that folder. The lock makes a second thread wait for
    /// the first pass to finish.
    /// </summary>
    internal static void MaintainSharedState(bool force = false)
    {
        if (SharedStateMaintenanceDisabled) return;
        lock (MaintenanceLock)
        {
            if (_maintaining) return;
            long now = Environment.TickCount64;
            if (!force && _maintainedOnce && now - _maintainedTicks < (long)MaintenanceInterval.TotalMilliseconds)
                return;
            _maintaining = true;
            try
            {
                MigrateLegacyState(LegacySharedDirectory, SharedDirectory);
                if (!_legacyGrantHandled)
                {
                    // An identity that may not change the rights now cannot later either; the
                    // service and every elevated helper are processes that can.
                    _legacyGrantHandled = true;
                    TryRevokeUsersModify(LegacySharedDirectory, SharedDirectory);
                }
            }
            catch { /* best effort */ }
            finally
            {
                _maintainedOnce = true;
                _maintainedTicks = now;
                _maintaining = false;
            }
        }
    }

    private const string ClaimSuffix = ".migrating";
    private const string ImportedSuffix = ".imported";
    private const string MigrationLockName = ".migration.lock";

    /// <summary>
    /// Bring every state file from <paramref name="legacy"/> into <paramref name="shared"/>
    /// and return how many arrived. Other files (fleet-policy.json, update-policy.json,
    /// rmm-status.*) stay where they are.
    /// <para>
    /// A file this identity may remove is moved: renamed in place first, then copied and
    /// deleted. A file it may only read (written by an elevated helper or the service, in a
    /// folder this user does not own) is copied, and a marker beside the copy records which
    /// version of the old file that was. The marker keeps the old file from being brought in a
    /// second time, so an install or restart that was cleared afterwards does not come back;
    /// whoever can remove the old file later (the service, an elevated helper) removes it
    /// together with the marker.
    /// </para>
    /// <para>
    /// One process does this at a time (a lock file in the shared folder), so a file that is
    /// being moved is never taken for one a crashed process left behind, and no process
    /// publishes a copy that another has published and the user has cleared since.
    /// </para>
    /// The copy is a new file, so it takes the rights of the shared folder instead of keeping
    /// those of the old one. When both folders hold the file, the newer one wins. Both folders
    /// are places where users may plant links and this also runs as SYSTEM, so files are read
    /// and written through <see cref="LinkSafeFile"/>.
    /// </summary>
    internal static int MigrateLegacyState(string legacy, string shared)
    {
        int arrived = 0;
        try
        {
            if (!System.IO.Directory.Exists(legacy)) return 0;
            if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(shared), StringComparison.OrdinalIgnoreCase))
                return 0;
            if (StateFolderAcl.IsLink(legacy) ||
                StateFolderAcl.IsLink(shared) ||
                StateFolderAcl.IsLink(Path.GetDirectoryName(Path.GetFullPath(legacy)) ?? legacy))
                return 0;
            if (LegacyStateNames(legacy).Count == 0) return 0;

            EnsureSharedDirectory(shared);
            if (StateFolderAcl.IsLink(shared)) return 0;
            using var pass = LinkSafeFile.TryLock(Path.Combine(shared, MigrationLockName));
            if (pass is null) return 0;

            foreach (string name in LegacyStateNames(legacy))
            {
                string source = Path.Combine(legacy, name);
                string claim = source + ClaimSuffix;
                try
                {
                    // A claim found here was left by a process that died between the rename
                    // and the delete: the lock says nobody is working on it now.
                    if (File.Exists(claim) && MoveClaimed(claim, shared, name)) arrived++;
                    if (!File.Exists(source)) continue;

                    if (TryClaim(source, claim) ? MoveClaimed(claim, shared, name) : CopyReadOnly(source, shared, name))
                        arrived++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use right now, or not readable for this identity: the next pass retries.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // This identity cannot create the shared folder; one that can will do the move.
        }
        catch (Exception ex)
        {
            AgentLog.Write("Moving automation state out of " + legacy + " failed: " + ex.Message);
        }

        if (arrived > 0)
        {
            ForgetWritableProbe();
            AgentLog.Write($"Brought {arrived} automation state file(s) from {legacy} to {shared}.");
        }
        return arrived;
    }

    private static System.Collections.Generic.List<string> LegacyStateNames(string legacy) =>
        System.IO.Directory.EnumerateFiles(legacy, "*" + ClaimSuffix)
            .Select(path => Path.GetFileName(path)[..^ClaimSuffix.Length])
            .Where(IsStateFileName)
            .Concat(EnumerateStateFiles(legacy).Select(path => Path.GetFileName(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Rename the file in place. False when this identity may not remove it; throws when
    /// another process still has it open after a short wait.
    /// </summary>
    private static bool TryClaim(string source, string claim)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, claim);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException) when (attempt < 2 && File.Exists(source))
            {
                System.Threading.Thread.Sleep(40);
            }
        }
    }

    /// <summary>Copy a claimed file into the shared folder, then remove the claim.</summary>
    private static bool MoveClaimed(string claim, string shared, string name)
    {
        string marker = Path.Combine(shared, name + ImportedSuffix);
        bool copied;
        try
        {
            copied = !AlreadyImported(marker, claim) && CopyIfNewer(claim, shared, name);
        }
        catch
        {
            // Put the original back under its own name; the next pass tries again.
            try { File.Move(claim, claim[..^ClaimSuffix.Length]); } catch { /* stays claimed */ }
            throw;
        }

        try
        {
            try { LinkSafeFile.ClearReadOnly(claim); } catch { /* best effort */ }
            File.Delete(claim);
        }
        catch
        {
            // Never leave a claim that a later pass would copy again.
            try { File.Move(claim, claim + ".done", overwrite: true); } catch { /* best effort */ }
        }
        try
        {
            // A claim that could be neither deleted nor set aside is seen again on the next
            // pass; the marker then says it has been brought in already.
            if (File.Exists(claim)) WriteMarker(marker, Stamp(claim));
            else if (File.Exists(marker)) File.Delete(marker);
        }
        catch { /* best effort */ }
        return copied;
    }

    /// <summary>Copy a file this identity cannot remove, once per version of that file.</summary>
    private static bool CopyReadOnly(string source, string shared, string name)
    {
        string marker = Path.Combine(shared, name + ImportedSuffix);
        if (AlreadyImported(marker, source)) return false;
        string stamp = Stamp(source);
        bool copied = CopyIfNewer(source, shared, name);
        WriteMarker(marker, stamp);
        return copied;
    }

    private static bool CopyIfNewer(string source, string shared, string name)
    {
        string target = Path.Combine(shared, name);
        using FileStream input = LinkSafeFile.OpenRead(source);
        DateTime writtenUtc = File.GetLastWriteTimeUtc(input.SafeFileHandle);
        if (File.Exists(target) && writtenUtc <= File.GetLastWriteTimeUtc(target))
            return false;
        LinkSafeFile.Replace(target, input.CopyTo, writtenUtc);
        return true;
    }

    private static string Stamp(string path)
    {
        using FileStream file = LinkSafeFile.OpenRead(path);
        return File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
               file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteMarker(string marker, string stamp) =>
        LinkSafeFile.Replace(marker, output => output.Write(System.Text.Encoding.ASCII.GetBytes(stamp)));

    private static bool AlreadyImported(string marker, string source)
    {
        try
        {
            if (!File.Exists(marker)) return false;
            using FileStream file = LinkSafeFile.OpenRead(marker);
            if (file.Length > 64) return false;
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            return string.Equals(System.Text.Encoding.ASCII.GetString(bytes).Trim(), Stamp(source), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }


    private static bool IsStateFileName(string name) =>
        name.EndsWith("-prompt.json", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("-install-queue.json", StringComparison.OrdinalIgnoreCase) ||
        FixedStateFileNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Take back the Users modify grant that builds after 0.7.2.61 put on the folder holding
    /// fleet-policy.json, before the state got its own subfolder. Only that exact grant is
    /// changed, on the folder and on what is in it: an explicit allow rule for BUILTIN\Users
    /// with modify becomes read and execute. Users can still read the policy, as before the
    /// grant, and no longer change it. Every other rule stays. <paramref name="keep"/> (the
    /// State subfolder) is left out; it has its own grant. Returns true when no such grant is
    /// left; only an administrator, SYSTEM or the folder's owner can change it.
    /// </summary>
    internal static bool TryRevokeUsersModify(string dir, string? keep = null)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !System.IO.Directory.Exists(dir)) return true;
            if (keep is not null && System.IO.Directory.Exists(keep))
                TryGrantUsersModify(keep);

            bool complete = StateFolderAcl.TryRevokeUsersModify(dir, TrustedRootFor(dir), keep, out int changed, out string failure);
            if (changed > 0)
                AgentLog.Write($"Took back the Users modify grant on {dir} ({changed} item(s)); automation state has its own subfolder now.");
            if (!complete && failure != "access denied")
                AgentLog.Write("Could not take back the Users modify grant on " + dir + ": " + failure);
            return complete;
        }
        catch (Exception ex)
        {
            AgentLog.Write("Could not take back the Users modify grant on " + dir + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The folder below which a user could have planted a link. In ProgramData that is
    /// ProgramData itself, which only administrators can change.
    /// </summary>
    private static string TrustedRootFor(string dir)
    {
        string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (programData.Length > 0 &&
            full.StartsWith(programData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return programData;
        return Path.GetDirectoryName(full) ?? full;
    }

    private static readonly TimeSpan GrantRetryAfterFailure = TimeSpan.FromMinutes(10);
    private static readonly object GrantLock = new();
    private static string? _grantedDir;
    private static DateTime _grantedDirCreatedUtc;
    private static string? _grantFailedDir;
    private static long _grantFailedTicks;
    private static bool _grantFailureLogged;

    /// <summary>
    /// Best-effort ACL so interactive users can save prompts/queues after an elevated
    /// helper or the service creates the shared state folder (avoids read-only shared state).
    /// The grant is inherited by the files in the folder and by files created later, so one
    /// success per process and folder is enough. Succeeds only for an administrator, SYSTEM
    /// or the folder's owner, and never on or through a link: see <see cref="StateFolderAcl"/>.
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
                // An identity that may not change the ACL will not be able to a minute later either.
                if (string.Equals(_grantFailedDir, dir, StringComparison.OrdinalIgnoreCase) &&
                    Environment.TickCount64 - _grantFailedTicks < (long)GrantRetryAfterFailure.TotalMilliseconds)
                    return false;
            }

            if (!StateFolderAcl.TryGrantUsersModify(dir, TrustedRootFor(dir), out string failure))
            {
                LogGrantFailure(dir, failure);
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
            _grantFailedDir = dir;
            _grantFailedTicks = Environment.TickCount64;
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
        MaintainSharedState();
        string shared = SharedDirectory;
        foreach (string dir in new[] { shared, PublicNoticeDirectory, InstallationManager.InstallDir })
        {
            try
            {
                if (ReferenceEquals(dir, shared)) EnsureSharedDirectory(dir);
                else System.IO.Directory.CreateDirectory(dir);
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
        MaintainSharedState();
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
        MaintainSharedState();
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
        // The elevated helper can remove what an older version left in the folder above, also
        // the files the signed-in user's agent could only copy.
        MaintainSharedState(force: true);
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

            fullPath = RedirectLegacyShared(
                Path.GetFullPath(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                LegacySharedDirectory,
                SharedDirectory);
            bool isShared = string.Equals(fullPath, Path.GetFullPath(SharedDirectory), StringComparison.OrdinalIgnoreCase);
            if (!isShared)
            {
                string name = Path.GetFileName(fullPath);
                string parent = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? "");
                if (!string.Equals(parent, "MicroSystems", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(name, "Update Express", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "must be ...\\MicroSystems\\Update Express";
                    return false;
                }
            }

            if (createIfShared && isShared)
            {
                EnsureSharedDirectory(fullPath);
            }
            else if (!System.IO.Directory.Exists(fullPath))
            {
                reason = "directory does not exist";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// An agent started before the update still passes the folder that held the state up to
    /// 0.7.2.61. State is never kept there again, so that path means the State subfolder.
    /// </summary>
    internal static string RedirectLegacyShared(string fullPath, string legacy, string shared) =>
        string.Equals(
            fullPath.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(shared)
            : fullPath;

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
        if (!System.IO.Directory.Exists(userDir) || StateFolderAcl.IsLink(shared)) return;

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
                // The elevated helper runs this on a folder users can write to: never through a
                // link, neither when reading the user's file nor when writing the shared one.
                using (FileStream input = LinkSafeFile.OpenRead(src))
                    LinkSafeFile.Replace(dst, input.CopyTo, File.GetLastWriteTimeUtc(input.SafeFileHandle));
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
