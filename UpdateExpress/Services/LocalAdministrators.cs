using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace UpdateExpress.Services;

/// <summary>Tells whether the owner of a file is an administrator of this PC.</summary>
internal static class LocalAdministrators
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Group = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>SYSTEM, the Administrators group and Windows' own installer account.</summary>
    internal static bool IsSystemOrGroup(SecurityIdentifier sid) =>
        sid.Equals(LocalSystem) || sid.Equals(Group) || sid.Equals(TrustedInstaller);

    /// <summary>
    /// True for SYSTEM, the Administrators group and an account that is a direct member of
    /// that group. The last case is a file an administrator made without elevation, which the
    /// account then owns in its own name. An account that is an administrator only through
    /// another group (Domain Admins) is not recognised and counts as an ordinary user.
    /// </summary>
    internal static bool IsAdministrator(SecurityIdentifier sid)
    {
        if (IsSystemOrGroup(sid)) return true;
        try
        {
            return IsDirectMember(sid);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDirectMember(SecurityIdentifier sid)
    {
        // The group's name is translated on a non-English Windows; the lookup needs the name.
        string account = Group.Translate(typeof(NTAccount)).Value;
        string name = account[(account.LastIndexOf('\\') + 1)..];

        IntPtr resume = IntPtr.Zero;
        int status = NetLocalGroupGetMembers(null, name, 0, out IntPtr buffer, -1, out int read, out _, ref resume);
        if (status != 0 || buffer == IntPtr.Zero) return false;
        try
        {
            for (int i = 0; i < read; i++)
            {
                IntPtr member = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                if (member != IntPtr.Zero && new SecurityIdentifier(member).Equals(sid)) return true;
            }
            return false;
        }
        finally
        {
            NetApiBufferFree(buffer);
        }
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(
        string? servername,
        string localgroupname,
        int level,
        out IntPtr bufptr,
        int prefmaxlen,
        out int entriesread,
        out int totalentries,
        ref IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}

/// <summary>Switches on a privilege the process token holds but does not use by default.</summary>
internal static class TokenPrivilege
{
    /// <summary>
    /// The privileges an administrator and SYSTEM hold for opening a file or folder, and
    /// changing its owner and rights, whatever its rights say. They are what gets a folder
    /// back whose owner has removed everyone else from its rights.
    /// </summary>
    internal static readonly string[] OverrideRights =
    {
        "SeBackupPrivilege", "SeRestorePrivilege", "SeTakeOwnershipPrivilege"
    };

    /// <summary>
    /// Switch the privileges on until the result is disposed. One that was on already stays
    /// on; one the token does not hold (a standard user) is skipped.
    /// </summary>
    internal static IDisposable Hold(params string[] privileges)
    {
        var enabled = new List<string>();
        foreach (string privilege in privileges)
        {
            if (TryEnable(privilege, out bool wasDisabled) && wasDisabled) enabled.Add(privilege);
        }
        return new Held(enabled);
    }

    private sealed class Held : IDisposable
    {
        private readonly List<string> _enabled;
        internal Held(List<string> enabled) => _enabled = enabled;

        public void Dispose()
        {
            foreach (string privilege in _enabled) Set(privilege, 0, out _);
            _enabled.Clear();
        }
    }

    /// <summary>False when the token does not hold the privilege.</summary>
    internal static bool TryEnable(string privilege, out bool wasDisabled) =>
        Set(privilege, PrivilegeEnabled, out wasDisabled);

    private static bool Set(string privilege, uint attributes, out bool changed)
    {
        changed = false;
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token)) return false;
            if (!LookupPrivilegeValueW(null, privilege, out long luid)) return false;
            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = attributes };
            if (!AdjustTokenPrivileges(token, false, ref state, (uint)Marshal.SizeOf<TokenPrivileges>(), out TokenPrivileges previous, out _))
                return false;
            // Success with "not all assigned" means the token does not hold it.
            if (Marshal.GetLastWin32Error() != 0) return false;
            // The previous state lists only what this call changed.
            changed = previous.Count == 1;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint PrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState,
        uint bufferLength,
        out TokenPrivileges previousState,
        out uint returnLength);
}
