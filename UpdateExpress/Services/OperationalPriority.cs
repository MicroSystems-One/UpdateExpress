using UpdateExpress.Models;

namespace UpdateExpress.Services;

/// <summary>
/// Operational grades that are not official MSRC severities.
/// Display K1 stays reserved for MSRC Critical. Defender signatures are Moderate (K3).
/// </summary>
internal static class OperationalPriority
{
    internal const string DefenderLabel = "Moderate";
    internal const string DefenderExplanation =
        "Microsoft Defender Security Intelligence (KB2267602). Operational Moderate (K3) based on update identity, not an official MSRC severity.";

    internal static bool IsDefenderKb(string? kb) =>
        (kb ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(id => id.Equals("KB2267602", StringComparison.OrdinalIgnoreCase));

    internal static void Apply(UpdateEntry entry)
    {
        if (!IsDefenderKb(entry.KB)) return;
        entry.Priority = DefenderLabel;
        entry.PriorityExplanation = DefenderExplanation;
    }
}
