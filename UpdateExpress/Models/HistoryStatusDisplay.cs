using System.Text.RegularExpressions;

namespace UpdateExpress.Models;

/// <summary>
/// Wording for the Status of a History / outcome row. The stored <see cref="UpdateEntry.Status"/>
/// is in whichever language the writing code used: Windows Update history and the archive store
/// Norwegian ("Vellykket"), Installation Manager outcomes store English ("Successful"). Lists
/// show the status in the UI language instead of the stored text.
/// </summary>
public static class HistoryStatusDisplay
{
    /// <summary>The UI language, set by MainWindow whenever it applies the language.</summary>
    public static bool NorwegianUi { get; set; }

    private enum Kind { Success, Partial, Failed, Cancelled, Fixed, Unknown, Other }

    // Same spellings as UpdateEntry.IsHistorySuccessful / IsHistoryPartial / IsHistoryFailed.
    private static Kind Classify(string? status) => status switch
    {
        "Vellykket" or "Successful" or "Installert" => Kind.Success,
        "Vellykket med feil" or "Delvis fullført" or "Partial" => Kind.Partial,
        "Mislykket" or "Failed" or "Feilet / avbrutt" => Kind.Failed,
        "Avbrutt" or "Cancelled" => Kind.Cancelled,
        "Fixed" or "Fikset" => Kind.Fixed,
        "Ukjent" or "Unknown" => Kind.Unknown,
        _ => Kind.Other
    };

    /// <summary>Status in the UI language. A status this class does not know is returned as stored.</summary>
    public static string Format(string? status, bool? norwegian = null)
    {
        bool no = norwegian ?? NorwegianUi;
        return Classify(status) switch
        {
            Kind.Success => no ? "Vellykket" : "Successful",
            Kind.Partial => no ? "Vellykket med feil" : "Partial",
            Kind.Failed => no ? "Mislykket" : "Failed",
            Kind.Cancelled => no ? "Avbrutt" : "Cancelled",
            Kind.Fixed => no ? "Fikset" : "Fixed",
            Kind.Unknown => no ? "Ukjent" : "Unknown",
            _ => status ?? ""
        };
    }

    /// <summary>Status cell text for a grid row.</summary>
    public static string Format(UpdateEntry entry, bool? norwegian = null)
    {
        // A failed row shows its HRESULT instead of a failure label.
        if (entry.HasFixableErrorCode) return entry.ErrorCode;
        // Pending driver rows (WUA + OEM catalog) prefer DriverStatus. History rows have a
        // Date and use Status. OEM release times live in DateDisplay only so they stay pending.
        if (entry.Category == "Driver / firmware"
            && !string.IsNullOrWhiteSpace(entry.DriverStatus)
            && (string.IsNullOrWhiteSpace(entry.Date) || entry.OpensExternalUrl))
            return entry.DriverStatus;
        return Format(entry.Status, norwegian);
    }

    /// <summary>
    /// Language-neutral status for identity keys, so "Vellykket" and "Successful" compare equal.
    /// The tokens are the upper-cased Norwegian spellings History has always stored, which keeps
    /// the keys saved in the archive's hidden-row list valid.
    /// </summary>
    internal static string StatusKey(string? status) => Classify(status) switch
    {
        Kind.Success => "VELLYKKET",
        Kind.Partial => "VELLYKKET MED FEIL",
        Kind.Failed => "MISLYKKET",
        Kind.Cancelled => "AVBRUTT",
        Kind.Fixed => "FIXED",
        Kind.Unknown => "UKJENT",
        _ => Regex.Replace(status ?? "", @"\s+", " ").Trim().ToUpperInvariant()
    };
}
