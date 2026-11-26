using UpdateExpress.Services;
using Xunit;
using Check = UpdateExpress.Services.Windows11Eligibility.Check;
using Facts = UpdateExpress.Services.Windows11Eligibility.Facts;

namespace UpdateExpress.Tests;

public class Windows11EligibilityTests
{
    private const long Gb = 1L << 30;

    /// <summary>A Windows 10 Pro 22H2 PC that meets every requirement (12th gen Intel).</summary>
    private static Facts EligiblePc() => new()
    {
        OsBuild = 19045,
        ProductType = Windows11Eligibility.ProductTypeWorkstation,
        EditionId = "Professional",
        ProcessorArchitecture = Windows11Eligibility.ArchitectureX64,
        CpuVendor = "GenuineIntel",
        CpuFamily = 6,
        CpuModel = 154,
        CpuStepping = 3,
        CpuMhz = 2496,
        LogicalProcessors = 12,
        CpuName = "12th Gen Intel(R) Core(TM) i5-1235U",
        SystemModel = "20Y1",
        InstalledMemoryBytes = 16 * Gb,
        SystemDriveBytes = 237 * Gb,
        FirmwareType = Windows11Eligibility.FirmwareUefi,
        SecureBootStatePresent = true,
        TpmVersion = 2
    };

    /// <summary>The internal result type cannot be a parameter of a public test method.</summary>
    public enum Expect { Met, NotMet, Unknown }

    private static Expect As(Check check) => Enum.Parse<Expect>(check.ToString());

    private static Expect Result(Facts facts, string requirement) =>
        As(Windows11Eligibility.Decide(facts).Requirements.Single(r => r.Name == requirement).Result);

    [Fact]
    public void Windows10PcMeetingEveryRequirement_IsEligible()
    {
        var decision = Windows11Eligibility.Decide(EligiblePc());

        Assert.True(decision.Eligible, decision.Summary);
        Assert.Equal(
            new[] { "Windows 10", "Processor", "Memory", "Storage", "UEFI and Secure Boot", "TPM 2.0" },
            decision.Requirements.Select(r => r.Name));
        Assert.All(decision.Requirements, r => Assert.Equal(Check.Met, r.Result));
    }

    [Fact]
    public void NothingRead_IsNotEligible_AndEveryRequirementIsUnknown()
    {
        var decision = Windows11Eligibility.Decide(new Facts());

        Assert.False(decision.Eligible);
        Assert.All(decision.Requirements, r => Assert.Equal(Check.Unknown, r.Result));
    }

    private static readonly Dictionary<string, Facts> OneValueNotRead = new()
    {
        { "build", EligiblePc() with { OsBuild = null } },
        { "product type", EligiblePc() with { ProductType = null } },
        { "edition", EligiblePc() with { EditionId = null } },
        { "architecture", EligiblePc() with { ProcessorArchitecture = null } },
        { "maker", EligiblePc() with { CpuVendor = null } },
        { "family", EligiblePc() with { CpuFamily = null } },
        { "model", EligiblePc() with { CpuModel = null } },
        { "stepping", EligiblePc() with { CpuStepping = null } },
        { "speed", EligiblePc() with { CpuMhz = null } },
        { "processor count", EligiblePc() with { LogicalProcessors = null } },
        { "memory", EligiblePc() with { InstalledMemoryBytes = null } },
        { "system drive", EligiblePc() with { SystemDriveBytes = null } },
        { "firmware", EligiblePc() with { FirmwareType = null } },
        { "secure boot", EligiblePc() with { SecureBootStatePresent = null } },
        { "tpm", EligiblePc() with { TpmVersion = null } }
    };

    [Theory]
    [InlineData("build")]
    [InlineData("product type")]
    [InlineData("edition")]
    [InlineData("architecture")]
    [InlineData("maker")]
    [InlineData("family")]
    [InlineData("model")]
    [InlineData("stepping")]
    [InlineData("speed")]
    [InlineData("processor count")]
    [InlineData("memory")]
    [InlineData("system drive")]
    [InlineData("firmware")]
    [InlineData("secure boot")]
    [InlineData("tpm")]
    public void OneValueNotRead_IsUnknown_AndNotEligible(string what)
    {
        var decision = Windows11Eligibility.Decide(OneValueNotRead[what]);

        Assert.False(decision.Eligible, what);
        Assert.Contains(decision.Requirements, r => r.Result == Check.Unknown);
        Assert.DoesNotContain(decision.Requirements, r => r.Result == Check.NotMet);
    }

    [Theory]
    [InlineData(17763, 3, "ServerStandard")]   // Windows Server 2019
    [InlineData(20348, 3, "ServerStandard")]   // Windows Server 2022
    [InlineData(20348, 2, "ServerDatacenter")] // Windows Server 2022 domain controller
    [InlineData(19045, 3, "Professional")]     // a server whatever the build and edition say
    [InlineData(19045, 2, "Professional")]
    public void WindowsServer_IsNeverEligible(int build, int productType, string edition)
    {
        var facts = EligiblePc() with { OsBuild = build, ProductType = productType, EditionId = edition };

        var decision = Windows11Eligibility.Decide(facts);

        Assert.False(decision.Eligible);
        var windows = decision.Requirements.Single(r => r.Name == "Windows 10");
        Assert.Equal(Check.NotMet, windows.Result);
        Assert.Contains("Windows Server", windows.Detail);
    }

    [Fact]
    public void WindowsServer_IsRefusedEvenWhenNothingElseWasRead()
    {
        var windows = Windows11Eligibility.CheckWindows(new Facts { ProductType = 3 });

        Assert.Equal(Check.NotMet, windows.Result);
    }

    [Theory]
    [InlineData(19041, "Core", Expect.Met)]          // Windows 10 2004 Home
    [InlineData(19045, "Professional", Expect.Met)]
    [InlineData(19045, "Enterprise", Expect.Met)]
    [InlineData(19045, "Education", Expect.Met)]
    [InlineData(19044, "EnterpriseS", Expect.NotMet)]    // LTSC 2021
    [InlineData(19044, "IoTEnterpriseS", Expect.NotMet)]
    [InlineData(18363, "Professional", Expect.NotMet)]   // Windows 10 1909: too old for the Assistant
    [InlineData(17763, "Professional", Expect.NotMet)]
    [InlineData(22000, "Professional", Expect.NotMet)]   // Windows 11 21H2
    [InlineData(26200, "Professional", Expect.NotMet)]   // Windows 11 25H2
    public void WindowsRequirement_FollowsBuildAndEdition(int build, string edition, Expect expected)
    {
        var facts = EligiblePc() with { OsBuild = build, EditionId = edition };

        Assert.Equal(expected, Result(facts, "Windows 10"));
        Assert.Equal(expected == Expect.Met, Windows11Eligibility.Decide(facts).Eligible);
    }

    [Theory]
    // Intel
    [InlineData("GenuineIntel", 6, 154, 3, null, Expect.Met)]    // 12th gen
    [InlineData("GenuineIntel", 6, 186, 3, null, Expect.Met)]    // 13th gen
    [InlineData("GenuineIntel", 6, 142, 10, null, Expect.Met)]   // 8th gen U (Kaby Lake R)
    [InlineData("GenuineIntel", 6, 158, 10, null, Expect.Met)]   // 8th gen (Coffee Lake)
    [InlineData("GenuineIntel", 6, 85, 4, null, Expect.Met)]     // Skylake-X / Xeon Scalable, the one exception at or below 95
    [InlineData("GenuineIntel", 6, 94, 3, null, Expect.NotMet)]  // 6th gen (Skylake)
    [InlineData("GenuineIntel", 6, 78, 3, null, Expect.NotMet)]  // 6th gen U
    [InlineData("GenuineIntel", 6, 95, 1, null, Expect.NotMet)]
    [InlineData("GenuineIntel", 6, 60, 3, null, Expect.NotMet)]  // 4th gen (Haswell)
    [InlineData("GenuineIntel", 15, 4, 1, null, Expect.NotMet)]  // Pentium 4
    [InlineData("GenuineIntel", 6, 142, 9, 16, Expect.Met)]      // 8th gen Y with the 8th gen platform id
    [InlineData("GenuineIntel", 6, 142, 9, 64, Expect.NotMet)]   // 7th gen U
    [InlineData("GenuineIntel", 6, 158, 9, 8, Expect.Met)]
    [InlineData("GenuineIntel", 6, 158, 9, 2, Expect.NotMet)]    // 7th gen H/S
    [InlineData("GenuineIntel", 6, 142, 9, null, Expect.Unknown)] // 7th or 8th gen: platform id not read
    [InlineData("GenuineIntel", 6, 158, 9, null, Expect.Unknown)]
    [InlineData("GenuineIntel", 19, 1, 0, null, Expect.Unknown)] // a family the rule was not written for
    // AMD
    [InlineData("AuthenticAMD", 25, 80, 0, null, Expect.Met)]    // Zen 3
    [InlineData("AuthenticAMD", 23, 8, 2, null, Expect.Met)]     // Zen+
    [InlineData("AuthenticAMD", 23, 113, 0, null, Expect.Met)]   // Zen 2
    [InlineData("AuthenticAMD", 23, 1, 1, null, Expect.NotMet)]  // first Zen
    [InlineData("AuthenticAMD", 23, 17, 0, null, Expect.NotMet)] // first Zen APU
    [InlineData("AuthenticAMD", 21, 2, 0, null, Expect.NotMet)]  // Piledriver
    // Makers the rule does not cover
    [InlineData("HygonGenuine", 24, 0, 1, null, Expect.Unknown)]
    [InlineData("CentaurHauls", 7, 59, 0, null, Expect.Unknown)]
    [InlineData("  Shanghai  ", 7, 91, 0, null, Expect.Unknown)]
    [InlineData("VMware-CPU", 6, 154, 3, null, Expect.Unknown)]
    public void ProcessorModel_IsClassifiedByMicrosoftsRule(string vendor, int family, int model, int stepping, int? platformField, Expect expected)
    {
        var facts = EligiblePc() with
        {
            CpuVendor = vendor,
            CpuFamily = family,
            CpuModel = model,
            CpuStepping = stepping,
            CpuPlatformField1 = platformField,
            CpuName = "test"
        };

        Assert.Equal(expected, As(Windows11Eligibility.ClassifyCpuModel(facts).Result));
        Assert.Equal(expected, Result(facts, "Processor"));
        Assert.Equal(expected == Expect.Met, Windows11Eligibility.Decide(facts).Eligible);
    }

    [Theory]
    [InlineData("Surface Studio 2", true)]
    [InlineData("Precision 5520", true)]
    [InlineData("Precision 7520", false)]
    [InlineData(null, false)]
    public void I7_7820Hq_PassesOnlyInTheDevicesMicrosoftLists(string? systemModel, bool expected)
    {
        var facts = EligiblePc() with
        {
            CpuModel = 158,
            CpuStepping = 9,
            CpuPlatformField1 = 2,
            CpuName = "Intel(R) Core(TM) i7-7820HQ CPU @ 2.90GHz",
            SystemModel = systemModel
        };

        Assert.Equal(expected, Windows11Eligibility.Decide(facts).Eligible);
    }

    [Theory]
    [InlineData(Windows11Eligibility.ArchitectureX64, 2, 1001, Expect.Met)]
    [InlineData(Windows11Eligibility.ArchitectureX64, 2, 1000, Expect.NotMet)]  // must be faster than 1 GHz
    [InlineData(Windows11Eligibility.ArchitectureX64, 1, 3000, Expect.NotMet)]  // one logical processor
    [InlineData(Windows11Eligibility.ArchitectureX86, 4, 3000, Expect.NotMet)]  // 32-bit processor
    [InlineData(Windows11Eligibility.ArchitectureArm64, 8, 3000, Expect.NotMet)]
    public void Processor_NeedsX64_TwoLogicalProcessors_AndMoreThanOneGhz(int architecture, int logical, int mhz, Expect expected)
    {
        var facts = EligiblePc() with { ProcessorArchitecture = architecture, LogicalProcessors = logical, CpuMhz = mhz };

        Assert.Equal(expected, Result(facts, "Processor"));
    }

    [Theory]
    [InlineData(4 * Gb, Expect.Met)]
    [InlineData(8 * Gb, Expect.Met)]
    [InlineData(3 * Gb, Expect.NotMet)]
    [InlineData(2 * Gb, Expect.NotMet)]
    public void Memory_NeedsFourGb(long bytes, Expect expected)
    {
        Assert.Equal(expected, Result(EligiblePc() with { InstalledMemoryBytes = bytes }, "Memory"));
    }

    [Theory]
    [InlineData(64 * Gb, Expect.Met)]
    [InlineData(237 * Gb, Expect.Met)]
    [InlineData(68_500_000_000, Expect.Met)]     // 63.8 GB rounds to 64, as in Microsoft's script
    [InlineData(67_663_556_608, Expect.NotMet)]  // 63.0 GB: a "64 GB" disk with a recovery partition
    [InlineData(59 * Gb, Expect.NotMet)]
    [InlineData(32 * Gb, Expect.NotMet)]
    public void Storage_NeedsSixtyFourGbSystemDrive(long bytes, Expect expected)
    {
        Assert.Equal(expected, Result(EligiblePc() with { SystemDriveBytes = bytes }, "Storage"));
    }

    [Theory]
    [InlineData(Windows11Eligibility.FirmwareUefi, true, Expect.Met)]
    [InlineData(Windows11Eligibility.FirmwareUefi, false, Expect.Unknown)]  // UEFI, but no Secure Boot state to read
    [InlineData(Windows11Eligibility.FirmwareBios, true, Expect.NotMet)]
    [InlineData(Windows11Eligibility.FirmwareBios, false, Expect.NotMet)]
    [InlineData(0, true, Expect.Unknown)]
    [InlineData(3, true, Expect.Unknown)]
    public void Firmware_NeedsUefiWithSecureBoot(int firmware, bool secureBootState, Expect expected)
    {
        var facts = EligiblePc() with { FirmwareType = firmware, SecureBootStatePresent = secureBootState };

        Assert.Equal(expected, Result(facts, "UEFI and Secure Boot"));
    }

    [Theory]
    [InlineData(2, Expect.Met)]
    [InlineData(1, Expect.NotMet)]
    [InlineData(0, Expect.NotMet)]
    [InlineData(7, Expect.Unknown)]
    public void Tpm_NeedsVersionTwo(int version, Expect expected)
    {
        Assert.Equal(expected, Result(EligiblePc() with { TpmVersion = version }, "TPM 2.0"));
    }

    [Fact]
    public void ReadFacts_OnThisMachine_ReadsTheBasics_AndDecidesWithoutThrowing()
    {
        var facts = Windows11Eligibility.ReadFacts();

        Assert.Equal(Environment.OSVersion.Version.Build, facts.OsBuild);
        Assert.InRange(facts.ProductType ?? 0, 1, 3);
        Assert.Equal(Environment.ProcessorCount, facts.LogicalProcessors);
        Assert.True(facts.SystemDriveBytes > 0);

        var decision = Windows11Eligibility.Evaluate();
        Assert.Equal(6, decision.Requirements.Count);
        // A machine that already runs Windows 11, or a server, must never get the button.
        if (facts.OsBuild >= Windows11Eligibility.FirstWindows11Build || facts.ProductType != Windows11Eligibility.ProductTypeWorkstation)
            Assert.False(decision.Eligible);
    }
}
