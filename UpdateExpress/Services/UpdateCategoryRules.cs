using System;
using System.Linq;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

/// <summary>
/// Fail-closed classification for Windows automation categories (design §3).
/// K2 uses official MSRC severity only — never title/KB guessing.
/// </summary>
internal static class UpdateCategoryRules
{
    internal static bool IsDefenderSignature(UpdateEntry entry) =>
        OperationalPriority.IsDefenderKb(entry.KB);

    /// <summary>
    /// K2: software with MSRC Critical/Important. Excludes Defender, drivers,
    /// preview/optional, and feature/enablement packages.
    /// </summary>
    internal static bool IsCriticalSoftware(UpdateEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.UpdateId)) return false;
        if (IsDefenderSignature(entry)) return false;
        if (entry.Category is "Driver / firmware"
            or "Preview / valgfri"
            or "Optional / valgfri"
            or "Versjonsoppgradering")
            return false;

        return entry.Priority.Equals("Critical", StringComparison.OrdinalIgnoreCase)
               || entry.Priority.Equals("Important", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// K3: remaining SafeDefault software (not K1/K2, not preview/optional/feature/drivers).
    /// Includes Unrated and Moderate/Low MSRC. Feature updates stay manual.
    /// </summary>
    internal static bool IsGeneralSoftware(UpdateEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.UpdateId)) return false;
        if (IsDefenderSignature(entry)) return false;
        if (IsCriticalSoftware(entry)) return false;
        if (entry.Category is "Driver / firmware"
            or "Preview / valgfri"
            or "Optional / valgfri"
            or "Versjonsoppgradering")
            return false;
        return entry.Category == "Programvare / sikkerhet" && entry.SafeDefault;
    }

    /// <summary>
    /// Optional drivers automation: WUA-assigned non-firmware drivers with a verified hardware ID.
    /// Firmware and unassigned/optional driver offers stay manual.
    /// </summary>
    internal static bool IsAutomaticDriver(UpdateEntry entry)
    {
        if (entry.Category != "Driver / firmware") return false;
        if (string.IsNullOrWhiteSpace(entry.UpdateId)) return false;
        if (string.IsNullOrWhiteSpace(entry.DriverHardwareId)) return false;
        if (entry.DriverClassification.Equals("Firmware", StringComparison.OrdinalIgnoreCase)) return false;
        if (entry.DriverStatus.Equals("Firmware", StringComparison.OrdinalIgnoreCase)) return false;
        return entry.DriverAssigned;
    }
}
