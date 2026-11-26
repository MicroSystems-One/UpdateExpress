using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using FolderRule = System.Security.AccessControl.AccessRule<System.Security.AccessControl.FileSystemRights>;
using HandleSecurity = UpdateExpress.Services.StateFolderAcl.HandleSecurity;

namespace UpdateExpress.Services;

/// <summary>
/// Keeps %ProgramData%\MicroSystems\Update Express under the administrator's control, so that
/// only an administrator or SYSTEM can create or change fleet-policy.json there. Under the
/// default ProgramData rights every user may add files to that folder, and the user whose
/// process created the folder owns it.
/// <para>
/// A process running elevated or as SYSTEM (the service, the elevated helper, the installer)
/// makes Administrators the owner of the folder and replaces its rights with: SYSTEM and
/// Administrators full control, Users read. The State subfolder keeps its own Users modify
/// grant. update-policy.json and rmm-status.* are written by the signed-in user's agent, so
/// they stay writable for Users, each through a right on that one file.
/// </para>
/// <para>
/// Until such a process has run, and on a PC where none ever does, the folder is as before.
/// There the reader still refuses a fleet-policy.json that an ordinary user made: see
/// <see cref="TryReadAdministratorFile(string, out string, out string)"/>.
/// </para>
/// Nothing here follows a link: see <see cref="StateFolderAcl"/>.
/// </summary>
internal static class PolicyFolderGuard
{
    internal const string FleetPolicyFileName = "fleet-policy.json";
    internal const string PolicyMirrorFileName = "update-policy.json";
    internal const string RejectedSuffix = ".rejected";
    private const string StateFolderName = "State";

    /// <summary>Files in the policy folder that the signed-in user's agent writes.</summary>
    internal static readonly string[] UserWrittenFileNames = { PolicyMirrorFileName, "rmm-status.json", "rmm-status.txt" };

    private const FileSystemRights ReadRights = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
    private const FileSystemRights UserFileRights = FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Synchronize;
    private const InheritanceFlags ToChildren = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>Rights that let their holder move a folder away or rewrite its rights.</summary>
    private const FileSystemRights ControlRights =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const int GenericAll = 0x10000000;

    /// <summary>Who gets what. The real set is <see cref="Machine"/>; tests use their own.</summary>
    internal sealed class Identities
    {
        internal Identities(
            SecurityIdentifier owner,
            IReadOnlyList<SecurityIdentifier> fullControl,
            SecurityIdentifier readers,
            Func<SecurityIdentifier, bool> isAdministrator)
        {
            Owner = owner;
            FullControl = fullControl;
            Readers = readers;
            IsAdministrator = isAdministrator;
        }

        /// <summary>Owner of the folders and files afterwards.</summary>
        internal SecurityIdentifier Owner { get; }
        internal IReadOnlyList<SecurityIdentifier> FullControl { get; }
        internal SecurityIdentifier Readers { get; }

        /// <summary>Whether a file with this owner was made by an administrator.</summary>
        internal Func<SecurityIdentifier, bool> IsAdministrator { get; }

        internal bool IsTrusted(SecurityIdentifier sid) =>
            sid.Equals(Owner) || FullControl.Contains(sid) || LocalAdministrators.IsSystemOrGroup(sid);

        internal static Identities Machine
        {
            get
            {
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                return new Identities(
                    administrators,
                    new[] { system, administrators },
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    LocalAdministrators.IsAdministrator);
            }
        }
    }

    internal sealed class Outcome
    {
        /// <summary>True when the folder and everything in it is as it should be.</summary>
        internal bool Secured { get; set; }
        internal List<string> Changes { get; } = new();
        internal string Failure { get; set; } = "";

        /// <summary>rmm-status.* does not exist yet; a privileged process has to create it.</summary>
        internal bool StatusFilesMissing { get; set; }
    }

    /// <summary>Set by the unit test assembly: a test run must not touch the PC's own folder.</summary>
    internal static bool Disabled { get; set; }

    private static readonly Lazy<bool> Privileged = new(() =>
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    });

    private static readonly object RunLock = new();
    private static string _loggedFailure = "";

    private static string ProgramData =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>
    /// Secure the policy folder when this process is elevated or SYSTEM; otherwise do nothing.
    /// Cheap when the folder is already as it should be.
    /// </summary>
    internal static void RunIfPrivileged()
    {
        if (Disabled || !OperatingSystem.IsWindows() || !Privileged.Value) return;
        lock (RunLock)
        {
            try
            {
                Outcome outcome = Secure(AutomationStatePaths.LegacySharedDirectory, ProgramData, Identities.Machine);
                foreach (string change in outcome.Changes)
                    AgentLog.Write("Policy folder: " + change);
                if (!outcome.Secured && outcome.Failure != _loggedFailure)
                {
                    _loggedFailure = outcome.Failure;
                    AgentLog.Write("Policy folder is not secured: " + outcome.Failure);
                }
                // The user's agent can no longer create these; an empty file would mislead the RMM script.
                if (outcome.StatusFilesMissing)
                    RmmStatusExport.WriteProgramDataOnly("policy-folder-secured");
            }
            catch (Exception ex)
            {
                AgentLog.Write("Policy folder is not secured: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Write one of <see cref="UserWrittenFileNames"/>: over the existing file where it is, so
    /// it keeps the right that lets every user's agent write it. Never through a link. Where
    /// the file is missing or not writable for this user, a new one replaces it, which works
    /// for an administrator and in a folder that is not secured yet.
    /// </summary>
    internal static void WriteUserFile(string path, byte[] content)
    {
        try
        {
            using FileStream existing = LinkSafeFile.OpenWriteExisting(path);
            existing.Write(content);
            existing.SetLength(content.Length);
            return;
        }
        catch (FileNotFoundException) { }
        catch (UnauthorizedAccessException) { }

        LinkSafeFile.Replace(path, output => output.Write(content));
        if (Disabled || !Privileged.Value) return;
        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            if (string.Equals(folder, Path.GetFullPath(AutomationStatePaths.LegacySharedDirectory), StringComparison.OrdinalIgnoreCase))
                NormalizeFile(path, ProgramData, Identities.Machine, usersMayWrite: true, new Outcome());
        }
        catch { /* the next pass grants it */ }
    }

    /// <summary>
    /// Read fleet-policy.json as the signed-in user, and only when an administrator or SYSTEM
    /// made it. This holds also on a PC where no elevated process has secured the folder yet.
    /// </summary>
    internal static bool TryReadAdministratorFile(string path, out string content, out string refused) =>
        TryReadAdministratorFile(path, LocalAdministrators.IsAdministrator, out content, out refused);

    internal static bool TryReadAdministratorFile(
        string path,
        Func<SecurityIdentifier, bool> isAdministrator,
        out string content,
        out string refused)
    {
        content = "";
        using SafeFileHandle? handle = OpenAdministratorFile(
            path, LinkSafeFile.GenericRead, LinkSafeFile.ShareRead | LinkSafeFile.ShareDelete, isAdministrator, out refused, out _);
        if (handle is null) return false;
        // Read through the handle that was checked, not through the name again.
        using var stream = new FileStream(handle, FileAccess.Read);
        using var reader = new StreamReader(stream);
        content = reader.ReadToEnd();
        return true;
    }

    /// <summary>
    /// Open the file at the path itself when it is a regular file under this name only and its
    /// owner is an administrator; otherwise null and why. A file with a second name could be
    /// one that users may write, such as rmm-status.json. <paramref name="planted"/> is true
    /// when the file was examined and found wanting, false when it could not be examined.
    /// </summary>
    private static SafeFileHandle? OpenAdministratorFile(
        string path,
        uint access,
        uint share,
        Func<SecurityIdentifier, bool> isAdministrator,
        out string refused,
        out bool planted)
    {
        refused = "";
        planted = false;
        SafeFileHandle handle = LinkSafeFile.CreateFileW(
            path, access | LinkSafeFile.ReadControl | LinkSafeFile.FileReadAttributes, share, IntPtr.Zero,
            LinkSafeFile.OpenExisting, LinkSafeFile.FlagBackupSemantics | LinkSafeFile.FlagOpenReparsePoint, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid)
                refused = "cannot be opened (" + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")";
            else if ((File.GetAttributes(handle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                (refused, planted) = ("is not a regular file", true);
            else if (!GetFileInformationByHandle(handle, out FileInformation information))
                refused = "cannot be examined";
            else if (information.NumberOfLinks != 1)
                (refused, planted) = ("has a second name (hard link)", true);
            else if (new HandleSecurity(isContainer: false, handle, AccessControlSections.Owner)
                         .GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
                refused = "has no owner";
            else if (!isAdministrator(owner))
                (refused, planted) = ("was not made by an administrator (owner " + owner.Value + ")", true);
        }
        catch (Exception ex)
        {
            refused = ex.Message;
        }

        if (refused.Length == 0) return handle;
        handle.Dispose();
        return null;
    }

    /// <summary>Bring <paramref name="policyFolder"/> and what is in it into the secured form.</summary>
    internal static Outcome Secure(string policyFolder, string trustedRoot, Identities ids)
    {
        var outcome = new Outcome();
        try
        {
            string folder = Path.GetFullPath(policyFolder).TrimEnd(Path.DirectorySeparatorChar);
            string vendor = Path.GetDirectoryName(folder) ?? throw new IOException("No parent folder: " + folder);
            // For this pass only: the owner of a folder can have removed everyone else from it.
            using IDisposable overrideRights = TokenPrivilege.Hold(TokenPrivilege.OverrideRights);

            // The folder above first: whoever controls it can swap the policy folder for another.
            if (!PrepareFolder(vendor, ids, outcome)) return outcome;
            bool complete = SecureVendorFolder(vendor, trustedRoot, ids, outcome);
            if (!PrepareFolder(folder, ids, outcome) || !LockPolicyFolder(folder, trustedRoot, ids, outcome))
                return outcome;

            complete &= SecureStateFolder(Path.Combine(folder, StateFolderName), trustedRoot, ids, outcome);
            complete &= SecureFleetPolicy(Path.Combine(folder, FleetPolicyFileName), trustedRoot, ids, outcome);
            foreach (string name in UserWrittenFileNames)
            {
                byte[]? initial = name == PolicyMirrorFileName ? "{}"u8.ToArray() : null;
                complete &= SecureUserFile(Path.Combine(folder, name), initial, trustedRoot, ids, outcome);
            }
            outcome.Secured = complete;
        }
        catch (Exception ex)
        {
            outcome.Failure = ex.Message;
        }
        return outcome;
    }

    /// <summary>Make sure a real folder is at the path: a planted link is removed, a missing folder created.</summary>
    private static bool PrepareFolder(string path, Identities ids, Outcome outcome)
    {
        if (StateFolderAcl.IsLink(path) && !RemovePlantedLink(path, ids, outcome)) return false;
        if (File.Exists(path))
        {
            outcome.Failure = path + " is a file";
            return false;
        }
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            outcome.Changes.Add("created " + path);
        }
        return true;
    }

    private static bool RemovePlantedLink(string path, Identities ids, Outcome outcome)
    {
        SecurityIdentifier? owner = TryReadOwner(path);
        if (owner is not null && ids.IsAdministrator(owner))
        {
            outcome.Failure = path + " is a link an administrator made; left alone";
            return false;
        }
        RemoveLink(path);
        outcome.Changes.Add("removed a link at " + path);
        return true;
    }

    /// <summary>Removes the link itself; what it points to is not touched.</summary>
    private static void RemoveLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: false);
        else File.Delete(path);
    }

    /// <summary>
    /// The MicroSystems folder may hold other things and keeps its inherited rights. It only
    /// gets an administrator as owner, and no ordinary user keeps a right on the folder itself
    /// that would let them move the policy folder away or rewrite the rights.
    /// </summary>
    private static bool SecureVendorFolder(string vendor, string trustedRoot, Identities ids, Outcome outcome)
    {
        if (!ReadSecurity(vendor, trustedRoot, isFolder: true, ids, outcome, out SecurityIdentifier owner, out HandleSecurity? current))
            return false;
        bool ownerOk = ids.IsTrusted(owner);
        if (ownerOk && !ControlRules(current!, ids, inherited: true).Any()) return true;

        if (!ownerOk && !SetOwner(vendor, trustedRoot, isFolder: true, ids, outcome)) return false;
        using (SafeFileHandle? write = Open(vendor, trustedRoot, LinkSafeFile.ReadControl | LinkSafeFile.WriteDac, isFolder: true, outcome))
        {
            if (write is null) return false;
            var security = new HandleSecurity(isContainer: true, write);
            foreach (FolderRule rule in ControlRules(security, ids, inherited: false).ToList())
            {
                security.RemoveAccessRuleSpecific(rule);
                // What the rule gave on folders and files below stays as it was.
                if (rule.InheritanceFlags != InheritanceFlags.None)
                    security.AddAccessRule(new FolderRule(
                        rule.IdentityReference, rule.Rights, rule.InheritanceFlags,
                        rule.PropagationFlags | PropagationFlags.InheritOnly, AccessControlType.Allow));
                FileSystemRights rest = ((int)rule.Rights & GenericAll) != 0 ? ReadRights : rule.Rights & ~ControlRights;
                if ((rest & ~FileSystemRights.Synchronize) != 0)
                    security.AddAccessRule(new FolderRule(
                        rule.IdentityReference, rest, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            }
            // Saving makes Windows derive the inherited rules again. The one the folder's
            // creator had on it then goes to the new owner.
            if (!security.AreAccessRulesProtected) security.SetAccessRuleProtection(false, false);
            security.Save(write);
        }

        if (!ReadSecurity(vendor, trustedRoot, isFolder: true, ids, outcome, out _, out HandleSecurity? after)) return false;
        FolderRule? left = ControlRules(after!, ids, inherited: true).FirstOrDefault();
        if (left is not null)
        {
            outcome.Failure = $"{left.IdentityReference.Value} still controls {vendor}";
            return false;
        }
        outcome.Changes.Add(ownerOk
            ? "ended ordinary accounts' control of " + vendor
            : "took control of " + vendor + " from " + owner.Value);
        return true;
    }

    private static IEnumerable<FolderRule> ControlRules(HandleSecurity security, Identities ids, bool inherited) =>
        security.GetAccessRules(true, inherited, typeof(SecurityIdentifier))
            .Cast<FolderRule>()
            .Where(rule =>
                rule.AccessControlType == AccessControlType.Allow &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                !ids.IsTrusted((SecurityIdentifier)rule.IdentityReference) &&
                ((rule.Rights & ControlRights) != 0 || ((int)rule.Rights & GenericAll) != 0));

    /// <summary>Owner Administrators; rights no longer inherited: SYSTEM and Administrators full control, Users read.</summary>
    private static bool LockPolicyFolder(string folder, string trustedRoot, Identities ids, Outcome outcome)
    {
        var wanted = ids.FullControl
            .Select(sid => new FolderRule(sid, FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Allow))
            .Append(new FolderRule(ids.Readers, ReadRights, ToChildren, PropagationFlags.None, AccessControlType.Allow))
            .ToList();

        if (!ReadSecurity(folder, trustedRoot, isFolder: true, ids, outcome, out SecurityIdentifier owner, out HandleSecurity? current))
            return false;
        bool ownerOk = ids.IsTrusted(owner);
        if (ownerOk && current!.AreAccessRulesProtected && SameRules(current.GetAccessRules(true, true, typeof(SecurityIdentifier)), wanted))
            return true;

        if (!ownerOk && !SetOwner(folder, trustedRoot, isFolder: true, ids, outcome)) return false;
        using SafeFileHandle? write = Open(folder, trustedRoot, LinkSafeFile.ReadControl | LinkSafeFile.WriteDac, isFolder: true, outcome);
        if (write is null) return false;
        var security = new HandleSecurity(isContainer: true, write);
        security.SetAccessRuleProtection(true, false);
        foreach (FolderRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FolderRule>().ToList())
            security.RemoveAccessRuleSpecific(rule);
        foreach (FolderRule rule in wanted)
            security.AddAccessRule(rule);
        security.Save(write);
        outcome.Changes.Add(ownerOk
            ? "set the rights on " + folder + " (administrators full control, users read)"
            : "took " + folder + " from " + owner.Value + " (administrators full control, users read)");
        return true;
    }

    private static bool SecureStateFolder(string state, string trustedRoot, Identities ids, Outcome outcome)
    {
        if (!PrepareFolder(state, ids, outcome)) return false;
        if (StateFolderAcl.TryGrantUsersModify(state, trustedRoot, out string failure)) return true;
        outcome.Failure = state + ": " + failure;
        return false;
    }

    /// <summary>
    /// A fleet-policy.json that an ordinary user made is not a policy: it is set aside as
    /// fleet-policy.json.rejected, which nothing reads. One an administrator made keeps its
    /// content and gets the folder's rights and nothing else.
    /// </summary>
    private static bool SecureFleetPolicy(string path, string trustedRoot, Identities ids, Outcome outcome)
    {
        if (!TryGetAttributes(path, out FileAttributes attributes)) return true;
        string refused;
        bool planted;
        using (SafeFileHandle? made = OpenAdministratorFile(path, 0, LinkSafeFile.ShareAll, ids.IsAdministrator, out refused, out planted))
        {
            if (made is not null)
                return NormalizeFile(path, trustedRoot, ids, usersMayWrite: false, outcome);
        }
        if (!planted)
        {
            // Not known to be planted, so it stays where it is; the next pass looks again.
            if (!TryGetAttributes(path, out _)) return true;
            outcome.Failure = path + " " + refused;
            return false;
        }

        string aside = path + RejectedSuffix;
        try
        {
            if (TryGetAttributes(aside, out FileAttributes old))
            {
                if ((old & FileAttributes.Directory) != 0) Directory.Delete(aside, recursive: false);
                else File.Delete(aside);
            }
            if ((attributes & FileAttributes.Directory) != 0) Directory.Move(path, aside);
            else File.Move(path, aside);
            outcome.Changes.Add($"{path} {refused}; set aside as {aside}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                RemoveLink(path);
                outcome.Changes.Add($"{path} {refused}; removed");
                return true;
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                outcome.Failure = $"{path} {refused} and could not be set aside: {inner.Message}";
                return false;
            }
        }
    }

    /// <summary>A file the user's agent writes: made if missing, then writable in place for Users.</summary>
    private static bool SecureUserFile(string path, byte[]? initial, string trustedRoot, Identities ids, Outcome outcome)
    {
        if (TryGetAttributes(path, out FileAttributes attributes))
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                RemoveLink(path);
                outcome.Changes.Add("removed a link at " + path);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                outcome.Failure = path + " is a folder";
                return false;
            }
            else if (HasSecondName(path))
            {
                // Its rights would be set on a file that is also somewhere else. Only this
                // name goes; the file stays under its other name.
                File.Delete(path);
                outcome.Changes.Add("removed " + path + ", a second name (hard link) of another file");
            }
        }

        if (!TryGetAttributes(path, out _))
        {
            if (initial is null)
            {
                outcome.StatusFilesMissing = true;
                return true;
            }
            using (var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                created.Write(initial);
            outcome.Changes.Add("created " + path);
        }
        return NormalizeFile(path, trustedRoot, ids, usersMayWrite: true, outcome);
    }

    /// <summary>
    /// Owner an administrator, rights inherited from the folder, and for a file the user's
    /// agent writes one more: Users may read and write it, not delete or rename it.
    /// </summary>
    private static bool NormalizeFile(string path, string trustedRoot, Identities ids, bool usersMayWrite, Outcome outcome)
    {
        var wanted = new List<FolderRule>();
        if (usersMayWrite)
            wanted.Add(new FolderRule(ids.Readers, UserFileRights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));

        if (!ReadSecurity(path, trustedRoot, isFolder: false, ids, outcome, out SecurityIdentifier owner, out HandleSecurity? current))
            return false;
        bool ownerOk = ids.IsTrusted(owner);
        if (ownerOk && !current!.AreAccessRulesProtected && SameRules(current.GetAccessRules(true, false, typeof(SecurityIdentifier)), wanted))
            return true;

        if (!ownerOk && !SetOwner(path, trustedRoot, isFolder: false, ids, outcome)) return false;
        using SafeFileHandle? write = Open(path, trustedRoot, LinkSafeFile.ReadControl | LinkSafeFile.WriteDac, isFolder: false, outcome);
        if (write is null) return false;
        var security = new HandleSecurity(isContainer: false, write);
        security.SetAccessRuleProtection(false, false);
        foreach (FolderRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FolderRule>().ToList())
            security.RemoveAccessRuleSpecific(rule);
        foreach (FolderRule rule in wanted)
            security.AddAccessRule(rule);
        security.Save(write);
        outcome.Changes.Add("set the rights on " + path + (usersMayWrite ? " (users may write it)" : " (users read only)"));
        return true;
    }

    private static bool SameRules(AuthorizationRuleCollection actual, List<FolderRule> wanted)
    {
        var rules = actual.Cast<FolderRule>().ToList();
        return rules.Count == wanted.Count && wanted.All(want => rules.Any(rule =>
            rule.AccessControlType == want.AccessControlType &&
            rule.IdentityReference.Equals(want.IdentityReference) &&
            (rule.Rights | FileSystemRights.Synchronize) == (want.Rights | FileSystemRights.Synchronize) &&
            rule.InheritanceFlags == want.InheritanceFlags &&
            rule.PropagationFlags == want.PropagationFlags));
    }

    private static bool SetOwner(string path, string trustedRoot, bool isFolder, Identities ids, Outcome outcome)
    {
        // Only the right to change the owner: the present owner may have removed all others,
        // and that one an administrator and SYSTEM have whatever the rights say.
        using SafeFileHandle? handle = Open(path, trustedRoot, LinkSafeFile.WriteOwner, isFolder, outcome);
        if (handle is null) return false;
        var security = new HandleSecurity(isFolder);
        security.SetOwner(ids.Owner);
        security.Save(handle);
        return true;
    }

    private static bool ReadSecurity(
        string path,
        string trustedRoot,
        bool isFolder,
        Identities ids,
        Outcome outcome,
        out SecurityIdentifier owner,
        out HandleSecurity? security)
    {
        owner = new SecurityIdentifier(WellKnownSidType.NullSid, null);
        security = null;
        using SafeFileHandle? handle =
            StateFolderAcl.OpenVerified(path, trustedRoot, LinkSafeFile.ReadControl, isFolder, out string failure)
            // Its owner has taken the rights away from everyone else. An owner always has them.
            ?? (SetOwner(path, trustedRoot, isFolder, ids, new Outcome())
                ? StateFolderAcl.OpenVerified(path, trustedRoot, LinkSafeFile.ReadControl, isFolder, out failure)
                : null);
        if (handle is null)
        {
            outcome.Failure = path + ": " + failure;
            return false;
        }
        security = new HandleSecurity(isFolder, handle, AccessControlSections.Owner | AccessControlSections.Access);
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier sid) owner = sid;
        return true;
    }

    private static SafeFileHandle? Open(string path, string trustedRoot, uint access, bool isFolder, Outcome outcome)
    {
        SafeFileHandle? handle = StateFolderAcl.OpenVerified(path, trustedRoot, access, isFolder, out string failure);
        if (handle is null) outcome.Failure = path + ": " + failure;
        return handle;
    }

    private static bool HasSecondName(string path)
    {
        using SafeFileHandle handle = LinkSafeFile.CreateFileW(
            path, LinkSafeFile.FileReadAttributes, LinkSafeFile.ShareAll, IntPtr.Zero, LinkSafeFile.OpenExisting,
            LinkSafeFile.FlagBackupSemantics | LinkSafeFile.FlagOpenReparsePoint, IntPtr.Zero);
        return !handle.IsInvalid && GetFileInformationByHandle(handle, out FileInformation information) && information.NumberOfLinks > 1;
    }

    /// <summary>The owner of what is at the path itself, a link included; null when it cannot be read.</summary>
    private static SecurityIdentifier? TryReadOwner(string path)
    {
        try
        {
            using SafeFileHandle handle = LinkSafeFile.CreateFileW(
                path, LinkSafeFile.ReadControl, LinkSafeFile.ShareAll, IntPtr.Zero, LinkSafeFile.OpenExisting,
                LinkSafeFile.FlagBackupSemantics | LinkSafeFile.FlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            var security = new HandleSecurity(isContainer: false, handle, AccessControlSections.Owner);
            return security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct FileInformation
    {
        public uint Attributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    /// <summary>Attributes of what is at the path itself; false when nothing is there.</summary>
    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        attributes = default;
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
