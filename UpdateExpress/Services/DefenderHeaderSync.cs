using System;
using System.Text.RegularExpressions;

namespace UpdateExpress.Services;

/// <summary>
/// Rules for moving the header's Defender «Sjekket» time forward from a source other than
/// Get-MpComputerStatus (Historikk, or the tray's Defender automation result).
/// </summary>
internal static class DefenderHeaderSync
{
    private static readonly Regex LastUpdatedLine = new(@"Last updated:.*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True when a Defender automation run changed the signatures. "Already up to date" and
    /// "Verification unavailable" installed nothing, so they leave «Sjekket» alone.
    /// </summary>
    internal static bool InstalledNewSignatures(string? automationResult)
    {
        string result = (automationResult ?? "").Trim();
        return result.Equals("Installert", StringComparison.OrdinalIgnoreCase)
            || result.Equals("Installed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a background read should leave the header as it is: Defender could not be
    /// asked this time, and there is a status on screen to keep. A read the user asked for, or
    /// the first read, shows the failure.
    /// </summary>
    internal static bool KeepShownStatus(DefenderState? shown, DefenderState read, bool quiet) =>
        quiet && read.ReadFailed && shown is not null;

    /// <summary>
    /// The same state with a newer signature time, or the state itself when
    /// <paramref name="when"/> is not newer than what it already shows.
    /// </summary>
    internal static DefenderState WithNewerSignatureTime(DefenderState status, DateTime when, string source)
    {
        if (status.SignatureLastUpdated is DateTime shown && shown >= when) return status;
        string line = $"Last updated: {when:dd.MM.yyyy HH:mm} ({source})";
        string details = status.Details.Contains("Last updated:", StringComparison.OrdinalIgnoreCase)
            ? LastUpdatedLine.Replace(status.Details, _ => line, 1)
            : status.Details + "\n" + line;
        return status with { SignatureLastUpdated = when, Details = details };
    }
}
