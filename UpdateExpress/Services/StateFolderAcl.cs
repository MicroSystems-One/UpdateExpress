using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace UpdateExpress.Services;

/// <summary>
/// Changes the rights of BUILTIN\Users on the automation state folder. The service does this as
/// SYSTEM in a place where every user may create folders, so nothing here goes by path alone:
/// each folder or file is opened without following a link, checked to be the real object at the
/// expected place below a root only administrators control, and changed through that handle.
/// A junction a user planted is therefore never followed, neither as the folder itself nor
/// inside it. (icacls /T, used before, walks into junctions.)
/// </summary>
internal static class StateFolderAcl
{
    private const FileSystemRights ModifyRights = FileSystemRights.Modify | FileSystemRights.Synchronize;
    private const FileSystemRights ReadRights = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
    private const InheritanceFlags ToChildren = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    private static SecurityIdentifier Users => new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>
    /// Give Users modify on <paramref name="dir"/> and, by inheritance, on what is and will be
    /// in it. Works for an administrator, SYSTEM or the folder's owner.
    /// </summary>
    internal static bool TryGrantUsersModify(string dir, string trustedRoot, out string failure)
    {
        try
        {
            using (SafeFileHandle? read = OpenVerified(dir, trustedRoot, ReadControl, wantDirectory: true, out failure))
            {
                if (read is null) return false;
                if (HasUsersModify(new HandleSecurity(isContainer: true, read))) return true;
            }

            using SafeFileHandle? write = OpenVerified(dir, trustedRoot, ReadControl | WriteDac, wantDirectory: true, out failure);
            if (write is null) return false;
            var security = new HandleSecurity(isContainer: true, write);
            security.AddAccessRule(new AccessRule<FileSystemRights>(
                Users, ModifyRights, ToChildren, PropagationFlags.None, AccessControlType.Allow));
            security.Save(write);
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Turn every explicit "allow Users modify" rule on <paramref name="dir"/> and on what is in
    /// it into "allow Users read and execute". <paramref name="keep"/> is left out. Returns
    /// false when something could not be opened or changed.
    /// </summary>
    internal static bool TryRevokeUsersModify(string dir, string trustedRoot, string? keep, out int changed, out string failure)
    {
        changed = 0;
        try
        {
            using SafeFileHandle? handle = OpenVerified(dir, trustedRoot, ReadControl | WriteDac, wantDirectory: true, out failure);
            if (handle is null) return false;
            string? keepFull = keep is null ? null : Path.GetFullPath(keep).TrimEnd(Path.DirectorySeparatorChar);
            return RevokeInFolder(dir, handle, trustedRoot, keepFull, ref changed, ref failure);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    private static bool RevokeInFolder(
        string dir,
        SafeFileHandle handle,
        string trustedRoot,
        string? keep,
        ref int changed,
        ref string failure)
    {
        bool complete = true;
        if (Downgrade(new HandleSecurity(isContainer: true, handle), handle)) changed++;

        foreach (string file in Directory.EnumerateFiles(dir))
        {
            using SafeFileHandle? child = OpenVerified(file, trustedRoot, ReadControl | WriteDac, wantDirectory: false, out string why);
            if (child is null)
            {
                complete = false;
                failure = why;
                continue;
            }
            if (Downgrade(new HandleSecurity(isContainer: false, child), child)) changed++;
        }

        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            if (keep is not null &&
                string.Equals(Path.GetFullPath(sub).TrimEnd(Path.DirectorySeparatorChar), keep, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsLink(sub)) continue;
            using SafeFileHandle? child = OpenVerified(sub, trustedRoot, ReadControl | WriteDac, wantDirectory: true, out string why);
            if (child is null)
            {
                complete = false;
                failure = why;
                continue;
            }
            complete &= RevokeInFolder(sub, child, trustedRoot, keep, ref changed, ref failure);
        }
        return complete;
    }

    private static bool HasUsersModify(HandleSecurity security)
    {
        SecurityIdentifier users = Users;
        foreach (AccessRule<FileSystemRights> rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference.Equals(users) &&
                (rule.Rights & FileSystemRights.Modify) == FileSystemRights.Modify &&
                rule.InheritanceFlags == ToChildren &&
                rule.PropagationFlags == PropagationFlags.None)
                return true;
        }
        return false;
    }

    private static bool Downgrade(HandleSecurity security, SafeFileHandle handle)
    {
        SecurityIdentifier users = Users;
        bool found = false;
        foreach (AccessRule<FileSystemRights> rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                !rule.IdentityReference.Equals(users) ||
                (rule.Rights | FileSystemRights.Synchronize) != ModifyRights)
                continue;
            security.RemoveAccessRuleSpecific(rule);
            // The grant was merged into a read rule the folder may already have had for Users,
            // so reading is kept: the agent reads fleet-policy.json as the signed-in user.
            security.AddAccessRule(new AccessRule<FileSystemRights>(
                users, ReadRights, rule.InheritanceFlags, rule.PropagationFlags, AccessControlType.Allow));
            found = true;
        }
        if (found) security.Save(handle);
        return found;
    }

    /// <summary>True for a junction or symbolic link (and when the path cannot be examined).</summary>
    internal static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch { return true; }
    }

    /// <summary>
    /// Open <paramref name="path"/> itself, never what a link at that path points to, and make
    /// sure no folder between <paramref name="trustedRoot"/> and it is a link either.
    /// </summary>
    private static SafeFileHandle? OpenVerified(string path, string trustedRoot, uint access, bool wantDirectory, out string failure)
    {
        failure = "";
        string relative = Path.GetRelativePath(trustedRoot, path);
        if (relative == "." || Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            failure = "not below " + trustedRoot;
            return null;
        }

        SafeFileHandle handle = CreateFileW(
            path, access | FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting,
            FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            failure = error == ErrorAccessDenied ? "access denied" : "open failed (" + error + ")";
            return null;
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(handle);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                failure = "is a link";
            else if (((attributes & FileAttributes.Directory) != 0) != wantDirectory)
                failure = wantDirectory ? "not a folder" : "not a file";
            else
            {
                using SafeFileHandle root = CreateFileW(
                    trustedRoot, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, FlagBackupSemantics, IntPtr.Zero);
                if (root.IsInvalid)
                    failure = "root not available (" + Marshal.GetLastWin32Error() + ")";
                else if (!string.Equals(
                             FinalPath(handle),
                             FinalPath(root) + Path.DirectorySeparatorChar + relative.TrimEnd(Path.DirectorySeparatorChar),
                             StringComparison.OrdinalIgnoreCase))
                    failure = "the path leads through a link";
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (failure.Length == 0) return handle;
        handle.Dispose();
        return null;
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[2048];
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
            throw new IOException("final path not available (" + Marshal.GetLastWin32Error() + ")");
        return new string(buffer, 0, (int)length).TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>The access rules of an open file or folder, read and written through its handle.</summary>
    private sealed class HandleSecurity : ObjectSecurity<FileSystemRights>
    {
        internal HandleSecurity(bool isContainer, SafeFileHandle handle)
            : base(isContainer, ResourceType.FileObject, handle, AccessControlSections.Access)
        {
        }

        internal void Save(SafeFileHandle handle) => Persist(handle);
    }

    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const int ErrorAccessDenied = 5;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}
