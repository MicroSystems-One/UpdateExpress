using System;

namespace UpdateExpress.Models;

/// <summary>
/// Display grades for the Priority column (not the same as Windows-automation K1/K2/K3).
/// Labels follow MSRC where possible: Critical · Important · Moderate · Regular · Unknown
/// </summary>
public static class PriorityGrade
{
    public const string Critical = "Critical (K1)";
    public const string Important = "Important (K2)";
    public const string Moderate = "Moderate (K3)";
    public const string Regular = "Regular (K4)";
    public const string Unknown = "Unknown (K5)";

    public const string ColorCritical = "#C62828";
    public const string ColorImportant = "#E65100";
    public const string ColorModerate = "#1565C0";
    public const string ColorRegular = "#6A1B9A";
    public const string ColorUnknown = "#6D4C41";

    public static (string Display, string ColorHex) ForUpdate(UpdateEntry entry)
    {
        if (entry.Category == "Driver / firmware")
            return ForDriver(entry);

        string raw = entry.Priority ?? "";
        if (raw.Equals("Critical", StringComparison.OrdinalIgnoreCase))
            return (Critical, ColorCritical);
        if (raw.Equals("Important", StringComparison.OrdinalIgnoreCase))
            return (Important, ColorImportant);
        if (raw.Equals("Moderate", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("Low", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("Recommended", StringComparison.OrdinalIgnoreCase))
            return (Moderate, ColorModerate);
        if (raw.Equals("Optional", StringComparison.OrdinalIgnoreCase) ||
            entry.Category is "Preview / valgfri" or "Optional / valgfri")
            return (Regular, ColorRegular);
        if (raw.Equals("Unrated", StringComparison.OrdinalIgnoreCase) || entry.SafeDefault)
            return (Regular, ColorRegular);
        return (Unknown, ColorUnknown);
    }

    public static (string Display, string ColorHex) ForDriver(UpdateEntry entry)
    {
        if (entry.DriverStatus.Equals("Firmware", StringComparison.OrdinalIgnoreCase) ||
            entry.DriverClassification.Equals("Firmware", StringComparison.OrdinalIgnoreCase))
            return (Unknown, ColorUnknown);
        if (entry.DriverAssigned || entry.DriverStatus.Equals("Recommended", StringComparison.OrdinalIgnoreCase))
            return (Moderate, ColorModerate);
        if (entry.DriverStatus.Equals("Newer", StringComparison.OrdinalIgnoreCase))
            return (Regular, ColorRegular);
        if (entry.Priority.Equals("Critical", StringComparison.OrdinalIgnoreCase))
            return (Critical, ColorCritical);
        if (entry.Priority.Equals("Important", StringComparison.OrdinalIgnoreCase))
            return (Important, ColorImportant);
        return (Unknown, ColorUnknown);
    }

    public static (string Display, string ColorHex) ForApplication()
        => (Regular, ColorRegular);
}
