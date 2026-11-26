using System;
using System.Runtime.InteropServices;

namespace UpdateExpress.Services;

/// <summary>
/// Tells a Windows client from a Windows Server. The build number cannot: Windows Server 2019
/// (17763) and 2022 (20348) lie below the first Windows 11 build, Windows Server 2025 (26100)
/// above it.
/// </summary>
internal static class WindowsProductType
{
    /// <summary>VER_NT_WORKSTATION: a client edition.</summary>
    internal const int Workstation = 1;
    /// <summary>VER_NT_DOMAIN_CONTROLLER: a server that is a domain controller.</summary>
    internal const int DomainController = 2;
    /// <summary>VER_NT_SERVER: a server that is not a domain controller.</summary>
    internal const int Server = 3;

    private static readonly Lazy<int?> Current = new(() =>
    {
        try { return Read(); }
        catch { return null; }
    });

    /// <summary>wProductType from RtlGetVersion, or null when Windows does not give one.</summary>
    internal static int? Read()
    {
        var info = new OSVERSIONINFOEXW { dwOSVersionInfoSize = Marshal.SizeOf<OSVERSIONINFOEXW>() };
        return RtlGetVersion(ref info) == 0 && info.wProductType != 0 ? info.wProductType : null;
    }

    /// <summary>A product type that could not be read is not taken for a server.</summary>
    internal static bool IsServer(int? productType) => productType is DomainController or Server;

    /// <summary>True when this machine runs Windows Server. Read once per process.</summary>
    internal static bool IsServerOs => IsServer(Current.Value);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OSVERSIONINFOEXW
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OSVERSIONINFOEXW versionInformation);
}
