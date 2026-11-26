using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace UpdateExpress.Services;

/// <summary>
/// Decides whether this PC runs Windows 10 and meets the Windows 11 upgrade requirements.
/// The hardware rules are the ones in Microsoft's Hardware Readiness script
/// (https://aka.ms/HWReadinessScript): storage, memory, TPM, processor and Secure Boot.
/// A requirement that cannot be read counts as unknown, and unknown is never eligible.
/// </summary>
internal static class Windows11Eligibility
{
    internal enum Check { Met, NotMet, Unknown }

    internal const int FirstWindows11Build = 22000;
    /// <summary>Windows 10 version 2004, the oldest the Installation Assistant runs on.</summary>
    internal const int OldestAssistantBuild = 19041;
    internal const int MinMemoryGb = 4;
    internal const int MinSystemDriveGb = 64;
    internal const int MinLogicalProcessors = 2;
    internal const int MinClockMhz = 1000;

    /// <summary>VER_NT_WORKSTATION: a client edition, not Windows Server.</summary>
    internal const int ProductTypeWorkstation = WindowsProductType.Workstation;
    internal const int ArchitectureX86 = 0;
    internal const int ArchitectureX64 = 9;
    internal const int ArchitectureArm64 = 12;
    internal const int FirmwareBios = 1;
    internal const int FirmwareUefi = 2;

    /// <summary>What was read from the PC. Null means the value could not be read.</summary>
    internal sealed record Facts
    {
        public int? OsBuild { get; init; }
        /// <summary>
        /// wProductType from RtlGetVersion: 1 = workstation (client), 2 = domain controller,
        /// 3 = server. Windows Server 2019 and 2022 have builds below 22000, so the build alone
        /// does not tell them from Windows 10.
        /// </summary>
        public int? ProductType { get; init; }
        public string? EditionId { get; init; }
        public int? ProcessorArchitecture { get; init; }
        public string? CpuVendor { get; init; }
        public int? CpuFamily { get; init; }
        public int? CpuModel { get; init; }
        public int? CpuStepping { get; init; }
        /// <summary>"Platform Specific Field 1" of processor 0; tells 7th and 8th gen Intel apart.</summary>
        public int? CpuPlatformField1 { get; init; }
        public int? CpuMhz { get; init; }
        public int? LogicalProcessors { get; init; }
        public string? CpuName { get; init; }
        public string? SystemModel { get; init; }
        public long? InstalledMemoryBytes { get; init; }
        public long? SystemDriveBytes { get; init; }
        public int? FirmwareType { get; init; }
        /// <summary>True when Windows holds a Secure Boot state for this firmware, on or off.</summary>
        public bool? SecureBootStatePresent { get; init; }
        /// <summary>2 = TPM 2.0, 1 = TPM 1.2, 0 = no TPM found.</summary>
        public int? TpmVersion { get; init; }
    }

    internal sealed record Requirement(string Name, Check Result, string Detail);

    internal sealed record Decision(IReadOnlyList<Requirement> Requirements)
    {
        /// <summary>True only when every requirement is met; one unknown is enough for false.</summary>
        public bool Eligible => Requirements.Count > 0 && Requirements.All(r => r.Result == Check.Met);

        public string Summary => string.Join("; ", Requirements.Select(r => $"{r.Name}: {r.Result} ({r.Detail})"));
    }

    internal static Decision Evaluate() => Decide(ReadFacts());

    internal static Decision Decide(Facts facts) => new(new[]
    {
        CheckWindows(facts),
        CheckProcessor(facts),
        CheckMemory(facts),
        CheckStorage(facts),
        CheckFirmware(facts),
        CheckTpm(facts)
    });

    internal static Requirement CheckWindows(Facts f)
    {
        const string name = "Windows 10";
        // First, so that a server is refused whatever else was or was not read.
        if (f.ProductType is int productType && productType != ProductTypeWorkstation)
            return new(name, Check.NotMet, $"Windows Server (product type {productType})");
        if (f.OsBuild is not int build) return new(name, Check.Unknown, "build not read");
        if (build >= FirstWindows11Build) return new(name, Check.NotMet, $"build {build} is Windows 11 or later");
        if (build < OldestAssistantBuild)
            return new(name, Check.NotMet, $"build {build} is older than Windows 10 version 2004");
        if (f.ProductType is null) return new(name, Check.Unknown, "product type not read");
        if (string.IsNullOrWhiteSpace(f.EditionId)) return new(name, Check.Unknown, "edition not read");
        // LTSC (EnterpriseS, IoTEnterpriseS) has its own lifecycle and no Installation Assistant path.
        if (f.EditionId.Contains("EnterpriseS", StringComparison.OrdinalIgnoreCase))
            return new(name, Check.NotMet, $"edition {f.EditionId.Trim()} is LTSC");
        return new(name, Check.Met, $"build {build}, {f.EditionId.Trim()}");
    }

    internal static Requirement CheckProcessor(Facts f)
    {
        const string name = "Processor";
        if (f.ProcessorArchitecture is not int architecture) return new(name, Check.Unknown, "architecture not read");
        if (architecture == ArchitectureArm64)
            return new(name, Check.NotMet, "ARM64: the Installation Assistant runs on x64 only");
        if (architecture != ArchitectureX64)
            return new(name, Check.NotMet, $"architecture {architecture} is not 64-bit x64");
        if (f.LogicalProcessors is not int logical) return new(name, Check.Unknown, "processor count not read");
        if (logical < MinLogicalProcessors) return new(name, Check.NotMet, $"{logical} logical processor");
        if (f.CpuMhz is not int mhz) return new(name, Check.Unknown, "clock speed not read");
        // Microsoft's script fails a speed of exactly 1000 MHz as well.
        if (mhz <= MinClockMhz) return new(name, Check.NotMet, $"{mhz} MHz");

        var (model, detail) = ClassifyCpuModel(f);
        return new(name, model, detail);
    }

    /// <summary>
    /// Microsoft's CPU generation rule. Intel: family 6 models up to 95 fail except model 85;
    /// models 142 and 158 stepping 9 pass only with the 8th gen platform id. AMD: family below
    /// 23 fails, and family 23 models 1 and 17 (first Zen) fail. Any other maker, or a missing
    /// value, is unknown.
    /// </summary>
    internal static (Check Result, string Detail) ClassifyCpuModel(Facts f)
    {
        if (string.IsNullOrWhiteSpace(f.CpuVendor)) return (Check.Unknown, "processor maker not read");
        if (f.CpuFamily is not int family || f.CpuModel is not int model || f.CpuStepping is not int stepping)
            return (Check.Unknown, "processor family, model or stepping not read");
        string vendor = f.CpuVendor.Trim();
        string id = $"{vendor} family {family} model {model} stepping {stepping}";

        if (vendor.Equals("GenuineIntel", StringComparison.OrdinalIgnoreCase))
        {
            if (IsExemptI7_7820Hq(f)) return (Check.Met, id + ", i7-7820HQ in a device Microsoft lists");
            if (family == 15) return (Check.NotMet, id);
            // Microsoft's rule reads the model alone; a family it was not written for is not guessed.
            if (family != 6) return (Check.Unknown, id + " is not classified");
            if (model <= 95 && model != 85) return (Check.NotMet, id);
            if ((model == 142 || model == 158) && stepping == 9)
            {
                if (f.CpuPlatformField1 is not int platform) return (Check.Unknown, id + ", platform id not read");
                bool eighthGen = (model == 142 && platform == 16) || (model == 158 && platform == 8);
                return (eighthGen ? Check.Met : Check.NotMet, id + $", platform id {platform}");
            }
            return (Check.Met, id);
        }

        if (vendor.Equals("AuthenticAMD", StringComparison.OrdinalIgnoreCase))
        {
            if (family < 23 || (family == 23 && (model == 1 || model == 17))) return (Check.NotMet, id);
            return (Check.Met, id);
        }

        return (Check.Unknown, id + " is not classified");
    }

    /// <summary>The one 7th gen Intel CPU Microsoft accepts, and only in these two devices.</summary>
    private static bool IsExemptI7_7820Hq(Facts f)
    {
        if (f.CpuName is null || f.SystemModel is null) return false;
        if (!f.CpuName.Contains("i7-7820hq cpu @ 2.90ghz", StringComparison.OrdinalIgnoreCase)) return false;
        string model = f.SystemModel.Trim();
        return model.Equals("Surface Studio 2", StringComparison.OrdinalIgnoreCase) ||
               model.Equals("Precision 5520", StringComparison.OrdinalIgnoreCase);
    }

    internal static Requirement CheckMemory(Facts f)
    {
        const string name = "Memory";
        if (f.InstalledMemoryBytes is not long bytes) return new(name, Check.Unknown, "installed memory not read");
        long gb = WholeGb(bytes);
        return new(name, gb >= MinMemoryGb ? Check.Met : Check.NotMet, $"{gb} GB installed");
    }

    internal static Requirement CheckStorage(Facts f)
    {
        const string name = "Storage";
        if (f.SystemDriveBytes is not long bytes) return new(name, Check.Unknown, "system drive size not read");
        long gb = WholeGb(bytes);
        return new(name, gb >= MinSystemDriveGb ? Check.Met : Check.NotMet, $"{gb} GB system drive");
    }

    internal static Requirement CheckFirmware(Facts f)
    {
        const string name = "UEFI and Secure Boot";
        if (f.FirmwareType is not int firmware) return new(name, Check.Unknown, "firmware type not read");
        if (firmware == FirmwareBios) return new(name, Check.NotMet, "legacy BIOS start");
        if (firmware != FirmwareUefi) return new(name, Check.Unknown, $"firmware type {firmware}");
        if (f.SecureBootStatePresent == true) return new(name, Check.Met, "UEFI, Secure Boot capable");
        return new(name, Check.Unknown, "UEFI, Secure Boot capability not read");
    }

    internal static Requirement CheckTpm(Facts f)
    {
        const string name = "TPM 2.0";
        return f.TpmVersion switch
        {
            null => new(name, Check.Unknown, "TPM not read"),
            2 => new(name, Check.Met, "TPM 2.0"),
            1 => new(name, Check.NotMet, "TPM 1.2"),
            0 => new(name, Check.NotMet, "no TPM found (missing or switched off in firmware)"),
            int other => new(name, Check.Unknown, $"TPM version {other}")
        };
    }

    /// <summary>Whole GB the way Microsoft's script rounds them: to nearest, halves to even.</summary>
    internal static long WholeGb(long bytes) => (long)Math.Round(bytes / (double)(1L << 30), MidpointRounding.ToEven);

    internal static Facts ReadFacts()
    {
        var facts = new Facts
        {
            OsBuild = Try(() => (int?)Environment.OSVersion.Version.Build),
            ProductType = Try(WindowsProductType.Read),
            LogicalProcessors = Try(() => (int?)Environment.ProcessorCount),
            InstalledMemoryBytes = Try(ReadInstalledMemoryBytes),
            SystemDriveBytes = Try(ReadSystemDriveBytes),
            FirmwareType = Try(ReadFirmwareType),
            SecureBootStatePresent = Try(ReadSecureBootStatePresent),
            TpmVersion = Try(ReadTpmVersion)
        };

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            facts = facts with { EditionId = key?.GetValue("EditionID") as string };
        }
        catch { }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            facts = facts with
            {
                CpuVendor = key?.GetValue("VendorIdentifier") as string,
                CpuName = (key?.GetValue("ProcessorNameString") as string)?.Trim(),
                CpuMhz = key?.GetValue("~MHz") as int?,
                CpuPlatformField1 = key?.GetValue("Platform Specific Field 1") as int?
            };
        }
        catch { }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            facts = facts with { SystemModel = key?.GetValue("SystemProductName") as string };
        }
        catch { }

        try
        {
            GetNativeSystemInfo(out SYSTEM_INFO info);
            facts = facts with
            {
                ProcessorArchitecture = info.wProcessorArchitecture,
                CpuFamily = info.wProcessorLevel,
                CpuModel = (info.wProcessorRevision >> 8) & 0xFF,
                CpuStepping = info.wProcessorRevision & 0xFF
            };
        }
        catch { }

        return facts;
    }

    private static T? Try<T>(Func<T?> read)
    {
        try { return read(); }
        catch { return default; }
    }

    private static long? ReadInstalledMemoryBytes() =>
        GetPhysicallyInstalledSystemMemory(out long kilobytes) && kilobytes > 0 ? kilobytes * 1024 : null;

    private static long? ReadSystemDriveBytes()
    {
        string? root = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrEmpty(root)) return null;
        var drive = new DriveInfo(root);
        return drive.IsReady ? drive.TotalSize : null;
    }

    private static int? ReadFirmwareType() => GetFirmwareType(out int type) ? type : null;

    private static bool? ReadSecureBootStatePresent()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return key?.GetValue("UEFISecureBootEnabled") is int;
    }

    private const uint TbsSuccess = 0;
    private const uint TbsTpmNotFound = 0x8028400F;

    private static int? ReadTpmVersion()
    {
        uint result = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TPM_DEVICE_INFO>(), out TPM_DEVICE_INFO info);
        if (result == TbsTpmNotFound) return 0;
        if (result != TbsSuccess) return null;
        return (int)info.tpmVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_INFO
    {
        public ushort wProcessorArchitecture;
        public ushort wReserved;
        public uint dwPageSize;
        public IntPtr lpMinimumApplicationAddress;
        public IntPtr lpMaximumApplicationAddress;
        public IntPtr dwActiveProcessorMask;
        public uint dwNumberOfProcessors;
        public uint dwProcessorType;
        public uint dwAllocationGranularity;
        public ushort wProcessorLevel;
        public ushort wProcessorRevision;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TPM_DEVICE_INFO
    {
        public uint structVersion;
        public uint tpmVersion;
        public uint tpmInterfaceType;
        public uint tpmImpRevision;
    }

    [DllImport("kernel32.dll")]
    private static extern void GetNativeSystemInfo(out SYSTEM_INFO lpSystemInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out int firmwareType);

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, out TPM_DEVICE_INFO info);
}
