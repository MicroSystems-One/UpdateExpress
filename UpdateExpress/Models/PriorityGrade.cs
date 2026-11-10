using System;

namespace UpdateExpress.Models;

/// <summary>
/// Display labels for the Priority column (end-user plain text, no automation K-codes).
/// Critical · Important · Moderate · Optional · Unrated · Unknown
/// </summary>
public static class PriorityGrade
{
    public const string Critical = "Critical";
    public const string Important = "Important";
    public const string Moderate = "Moderate";
    public const string Optional = "Optional";
    public const string Unrated = "Unrated";
    public const string Unknown = "Unknown";

    public const string ColorCritical = "#C62828";
    public const string ColorImportant = "#E65100";
    public const string ColorModerate = "#1565C0";
    public const string ColorOptional = "#6A1B9A";
    public const string ColorUnrated = "#6A1B9A";
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
            return (Optional, ColorOptional);
        if (raw.Equals("Unrated", StringComparison.OrdinalIgnoreCase) || entry.SafeDefault)
            return (Unrated, ColorUnrated);
        return (Unknown, ColorUnknown);
    }

    public static (string Display, string ColorHex) ForDriver(UpdateEntry entry)
    {
        if (entry.DriverStatus.Equals("Firmware", StringComparison.OrdinalIgnoreCase) ||
            entry.DriverClassification.Equals("Firmware", StringComparison.OrdinalIgnoreCase))
        {
            if (entry.Priority.Equals("Critical", StringComparison.OrdinalIgnoreCase))
                return (Critical, ColorCritical);
            if (entry.Priority.Equals("Important", StringComparison.OrdinalIgnoreCase))
                return (Important, ColorImportant);
            return (Moderate, ColorModerate);
        }
        if (entry.DriverAssigned || entry.DriverStatus.Equals("Recommended", StringComparison.OrdinalIgnoreCase))
            return (Moderate, ColorModerate);
        if (entry.DriverStatus.Equals("Newer", StringComparison.OrdinalIgnoreCase))
            return (Optional, ColorOptional);
        if (entry.Priority.Equals("Critical", StringComparison.OrdinalIgnoreCase))
            return (Critical, ColorCritical);
        if (entry.Priority.Equals("Important", StringComparison.OrdinalIgnoreCase))
            return (Important, ColorImportant);
        return (Unknown, ColorUnknown);
    }

    public static (string Display, string ColorHex) ForApplication()
        => (Unrated, ColorUnrated);
}
