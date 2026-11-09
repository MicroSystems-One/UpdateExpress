using System;
using Microsoft.Win32;

namespace UpdateExpress.Services;

// Offline lifecycle reference for standard Windows client releases.
// Unknown/ESU/LTSC configurations are never marked as definitively supported.
internal static class WindowsSupportReader
{
    internal sealed record Result(string Name, string Version, string Status, string Details);

    internal static Result Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string editionId = key?.GetValue("EditionID")?.ToString() ?? "";
            string edition = editionId switch
            {
                "Professional" => "Pro", "ProfessionalN" => "Pro N",
                "Core" => "Home", "CoreSingleLanguage" => "Home Single Language", "CoreN" => "Home N",
                "Enterprise" => "Enterprise", "EnterpriseS" => "Enterprise LTSC",
                "Education" => "Education", "IoTEnterprise" => "IoT Enterprise",
                "IoTEnterpriseS" => "IoT Enterprise LTSC", _ => editionId
            };
            int build = Environment.OSVersion.Version.Build;
            bool win11 = build >= 22000;
            string version = key?.GetValue("DisplayVersion")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(version)) version = key?.GetValue("ReleaseId")?.ToString() ?? "Unknown";
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
            if (DateTime.Today > end.Value)
                return new(name, version, "expired", details +
                    $"Standard security servicing ended on {end.Value:dd MMMM yyyy}. Upgrade to a supported Windows release. Avoid exposing an unpatched device to the internet or handling sensitive data. Special servicing arrangements must be verified separately.");
            return new(name, version, "supported", details +
                $"Standard Microsoft servicing is scheduled through {end.Value:dd MMMM yyyy}. This does not confirm that security updates are installed or that the device is fully protected.");
        }
        catch (Exception ex)
        {
            return new("Windows (unknown)", "", "unknown", "Could not determine Windows support status: " + ex.Message);
        }
    }
}
