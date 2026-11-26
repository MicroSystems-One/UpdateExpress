using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UpdateExpress.Services;

/// <summary>
/// File access for the service and the elevated helper in folders where ordinary users may
/// create files and links: the old and the new automation state folder. A path there can be a
/// link a user planted to make a privileged process read or overwrite some other file. These
/// helpers open the object at the path itself, never what a link points to, and write only to
/// files they created themselves under a name nobody can predict.
/// </summary>
internal static class LinkSafeFile
{
    /// <summary>Open an existing regular file for reading. A link or a folder is refused.</summary>
    internal static FileStream OpenRead(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path, GenericRead, ShareRead | ShareDelete, IntPtr.Zero, OpenExisting, FlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw Failure(handle, path);
        try
        {
            ThrowUnlessRegularFile(handle, path);
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Open an existing regular file to overwrite it where it is. A link or a folder is refused.
    /// The file keeps its owner and rights, which a replaced file would not.
    /// </summary>
    internal static FileStream OpenWriteExisting(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path, GenericWrite | FileReadAttributes, ShareRead, IntPtr.Zero, OpenExisting, FlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw Failure(handle, path);
        try
        {
            if ((File.GetAttributes(handle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new FileNotFoundException("No regular file: " + path, path);
            return new FileStream(handle, FileAccess.Write);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Put a new file at <paramref name="target"/>: written under a random name in the same
    /// folder, created exclusively, then renamed over the target. A link waiting at the target
    /// name is replaced, not followed.
    /// </summary>
    internal static void Replace(string target, Action<Stream> write, DateTime? lastWriteUtc = null)
    {
        string dir = Path.GetDirectoryName(target) ?? throw new IOException("No folder: " + target);
        string temp = Path.Combine(dir, ".ue-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(output);
                output.Flush();
                if (lastWriteUtc is DateTime at) File.SetLastWriteTimeUtc(output.SafeFileHandle, at);
            }
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Clear the read-only attribute of the file at the path itself, so it can be deleted.</summary>
    internal static void ClearReadOnly(string path)
    {
        using SafeFileHandle handle = CreateFileW(
            path, FileReadAttributes | FileWriteAttributes, ShareAll, IntPtr.Zero, OpenExisting, FlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) return;
        FileAttributes attributes = File.GetAttributes(handle);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return;
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(handle, FileAttributes.Normal);
    }

    /// <summary>
    /// Take a lock that lasts until the returned handle is closed, or null when another process
    /// holds it. The lock is a file that only one process can have open; it disappears when
    /// that process closes it or ends.
    /// </summary>
    internal static SafeFileHandle? TryLock(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path, GenericRead | Delete, ShareNone, IntPtr.Zero, OpenAlways,
            FlagOpenReparsePoint | FlagDeleteOnClose, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        try
        {
            ThrowUnlessRegularFile(handle, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            return null;
        }
    }

    private static void ThrowUnlessRegularFile(SafeFileHandle handle, string path)
    {
        FileAttributes attributes = File.GetAttributes(handle);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Is a link: " + path);
        if ((attributes & FileAttributes.Directory) != 0)
            throw new IOException("Is a folder: " + path);
    }

    private static Exception Failure(SafeFileHandle handle, string path)
    {
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        return error switch
        {
            ErrorAccessDenied => new UnauthorizedAccessException("Access denied: " + path),
            ErrorFileNotFound or ErrorPathNotFound => new FileNotFoundException("Not found: " + path, path),
            _ => new IOException("Could not open " + path + " (" + error + ")")
        };
    }

    internal const uint GenericRead = 0x80000000;
    internal const uint Delete = 0x00010000;
    internal const uint ReadControl = 0x00020000;
    internal const uint WriteDac = 0x00040000;
    internal const uint WriteOwner = 0x00080000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileReadAttributes = 0x0080;
    internal const uint FileWriteAttributes = 0x0100;
    internal const uint ShareNone = 0x0;
    internal const uint ShareRead = 0x1;
    internal const uint ShareDelete = 0x4;
    internal const uint ShareAll = 0x7;
    internal const uint OpenExisting = 3;
    internal const uint OpenAlways = 4;
    internal const uint FlagBackupSemantics = 0x02000000;
    internal const uint FlagOpenReparsePoint = 0x00200000;
    internal const uint FlagDeleteOnClose = 0x04000000;
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorAccessDenied = 5;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
