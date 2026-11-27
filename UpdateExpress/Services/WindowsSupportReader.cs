using System;
using Microsoft.Win32;

namespace UpdateExpress.Services;

// Offline lifecycle reference for standard Windows client releases and Windows Server LTSC.
// Unknown/ESU/LTSC configurations are never marked as definitively supported.
internal static class WindowsSupportReader
{
    internal const string ClientReferenceUrl =
        "https://learn.microsoft.com/windows/release-health/supported-versions-windows-client";
    internal const string ServerReferenceUrl =
        "https://learn.microsoft.com/windows/release-health/windows-server-release-info";

    internal sealed record Result(string Name, string Version, string Status, string Details)
    {
        /// <summary>Microsoft's page for the lifecycle this result was judged by.</summary>
        public string ReferenceUrl { get; init; } = ClientReferenceUrl;
    }

    /// <summary>What Windows says about itself. <see cref="ProductType"/> null = not read.</summary>
    internal sealed record OsFacts(
        int Build,
        int? ProductType,
        string EditionId,
        string DisplayVersion,
        string ReleaseId,
        string ProductName);

    /// <summary>
    /// Windows Server LTSC releases by build, with the day security updates end (end of extended
    /// support) from Microsoft's Windows Server release information page.
    /// </summary>
    private static readonly (int Build, string Name, DateTime SecurityUpdatesEnd)[] ServerReleases =
    {
        (14393, "Windows Server 2016", new DateTime(2027, 1, 12)),
        (17763, "Windows Server 2019", new DateTime(2029, 1, 9)),
        (20348, "Windows Server 2022", new DateTime(2031, 10, 14)),
        (26100, "Windows Server 2025", new DateTime(2034, 11, 14))
    };

    internal static Result Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            int? productType;
            try { productType = WindowsProductType.Read(); }
            catch { productType = null; }
            return Describe(
                new OsFacts(
                    Environment.OSVersion.Version.Build,
                    productType,
                    key?.GetValue("EditionID")?.ToString() ?? "",
                    key?.GetValue("DisplayVersion")?.ToString() ?? "",
                    key?.GetValue("ReleaseId")?.ToString() ?? "",
                    key?.GetValue("ProductName")?.ToString() ?? ""),
                DateTime.Today);
        }
        catch (Exception ex)
        {
            return new("Windows (unknown)", "", "unknown", "Could not determine Windows support status: " + ex.Message);
        }
    }

    internal static Result Describe(OsFacts facts, DateTime today)
    {
        string version = facts.DisplayVersion;
        if (string.IsNullOrWhiteSpace(version)) version = string.IsNullOrWhiteSpace(facts.ReleaseId) ? "Unknown" : facts.ReleaseId;

        // Before the build split: a server build says nothing about Windows 10 or 11.
        if (WindowsProductType.IsServer(facts.ProductType))
            return DescribeServer(facts, version, today);

        string editionId = facts.EditionId;
        string edition = editionId switch
        {
            "Professional" => "Pro", "ProfessionalN" => "Pro N",
            "Core" => "Home", "CoreSingleLanguage" => "Home Single Language", "CoreN" => "Home N",
            "Enterprise" => "Enterprise", "EnterpriseS" => "Enterprise LTSC",
            "Education" => "Education", "IoTEnterprise" => "IoT Enterprise",
            "IoTEnterpriseS" => "IoT Enterprise LTSC", _ => editionId
        };
        int build = facts.Build;
        bool win11 = build >= 22000;
        string name = $"Windows {(win11 ? "11" : "10")} {edition}".Trim();
        if (!string.IsNullOrWhiteSpace(version) && !version.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            name += $" {version}";
        string details = $"{name} · Version {version} · Build {build}.\n";
        bool ltsc = editionId.Contains("EnterpriseS", StringComparison.OrdinalIgnoreCase) ||
                    editionId.Contains("IoTEnterpriseS", StringComparison.OrdinalIgnoreCase);
        if (ltsc)
            return new(name, version, "unknown", details + "LTSC has a separate lifecycle. Verify this edition against Microsoft's lifecycle documentation; support has not been verified.");
        if (!win11)
            return new(name, version, "expired", details +
                "Standard Windows 10 support ended on 14 October 2025. Without verified Extended Security Updates (ESU), this device no longer receives regular Microsoft security fixes. Upgrade or replace it; avoid internet exposure and sensitive workloads until protected. ESU enrollment cannot be verified by this indicator.");
        bool enterprise = editionId.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) ||
                          editionId.Contains("Education", StringComparison.OrdinalIgnoreCase);
        DateTime? end = version.ToUpperInvariant() switch
        {
            "21H2" => enterprise ? new DateTime(2024, 10, 8) : new DateTime(2023, 10, 10),
            "22H2" => enterprise ? new DateTime(2025, 10, 14) : new DateTime(2024, 10, 8),
            "23H2" => enterprise ? new DateTime(2026, 11, 10) : new DateTime(2025, 11, 11),
            "24H2" => enterprise ? new DateTime(2027, 10, 12) : new DateTime(2026, 10, 13),
            "25H2" => enterprise ? new DateTime(2028, 10, 10) : new DateTime(2027, 10, 12),
            "26H1" => enterprise ? new DateTime(2029, 3, 13) : new DateTime(2028, 3, 14),
            "26H2" => enterprise ? new DateTime(2029, 10, 9) : new DateTime(2028, 10, 10),
            _ => null
        };
        if (end is null)
            return new(name, version, "unknown", details + "This Windows release or edition is not in the built-in lifecycle table. Verify its support status with Microsoft. No supported-status claim is made.");
        // The sentence is English, so the month is too, whatever the regional settings are.
        string endDate = end.Value.ToString("dd MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        if (today > end.Value)
            return new(name, version, "expired", details +
                $"Standard security servicing ended on {endDate}. Upgrade to a supported Windows release. Avoid exposing an unpatched device to the internet or handling sensitive data. Special servicing arrangements must be verified separately.");
        return new(name, version, "supported", details +
            $"Standard Microsoft servicing is scheduled through {endDate}. This does not confirm that security updates are installed or that the device is fully protected.");
    }

    private static Result DescribeServer(OsFacts facts, string version, DateTime today)
    {
        string edition = ServerEdition(facts.EditionId);
        string release = "";
        DateTime? end = null;
        foreach (var known in ServerReleases)
        {
            if (known.Build != facts.Build) continue;
            release = known.Name;
            end = known.SecurityUpdatesEnd;
            break;
        }

        // A release outside the table: Windows' own name when it names a server, never "Windows 10".
        string name = release.Length > 0
            ? $"{release} {edition}".Trim()
            : facts.ProductName.Trim().StartsWith("Windows Server", StringComparison.OrdinalIgnoreCase)
                ? facts.ProductName.Trim()
                : $"Windows Server {edition}".Trim();

        string details = $"{name} · Version {version} · Build {facts.Build}.\n";
        if (end is null)
            return new(name, version, "unknown", details +
                "This Windows Server release is not in the built-in lifecycle table. Verify its support status with Microsoft. No supported-status claim is made.")
            { ReferenceUrl = ServerReferenceUrl };
        // The sentence is English, so the month is too, whatever the regional settings are.
        string endDate = end.Value.ToString("dd MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        if (today > end.Value)
            return new(name, version, "expired", details +
                $"Microsoft security updates for {release} ended on {endDate}. Without verified Extended Security Updates (ESU), this server no longer receives Microsoft security fixes. Upgrade or replace it. ESU enrollment cannot be verified by this indicator.")
            { ReferenceUrl = ServerReferenceUrl };
        return new(name, version, "supported", details +
            $"Microsoft security updates for {release} are scheduled through {endDate} (end of extended support). This does not confirm that security updates are installed or that the server is fully protected.")
        { ReferenceUrl = ServerReferenceUrl };
    }

    private static string ServerEdition(string editionId) => editionId switch
    {
        "ServerStandard" => "Standard",
        "ServerDatacenter" => "Datacenter",
        "ServerStandardEval" => "Standard Evaluation",
        "ServerDatacenterEval" => "Datacenter Evaluation",
        "ServerSolution" => "Essentials",
        "ServerTurbine" => "Datacenter: Azure Edition",
        _ when editionId.StartsWith("Server", StringComparison.OrdinalIgnoreCase) => editionId["Server".Length..],
        _ => editionId
    };
}
