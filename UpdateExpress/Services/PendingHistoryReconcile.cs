using System.Text.RegularExpressions;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

/// <summary>
/// Drops a cached Windows Update offer once History already records that it succeeded.
/// A new WUA search is not required. Defender signatures share KB2267602, so only a
/// success whose signature version is at least the offered version counts.
/// Other updates are removed only when the success is newer than the pending snapshot,
/// so an older install of the same KB does not hide an update the last search still offered.
/// </summary>
internal static class PendingHistoryReconcile
{
    private static readonly Regex KbPattern = new(@"KB\d{5,9}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SignatureVersion = new(
        @"(?:versjon|version)\s+(?<version>\d+(?:\.\d+){2,3})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    internal static bool ShouldHide(
        UpdateEntry pending,
        IReadOnlyList<UpdateEntry> history,
        DateTime pendingSnapshotAt,
        Version? installedDefenderSignature = null)
    {
        if (IsDefender(pending))
            return DefenderInstalledCovers(pending, installedDefenderSignature)
                || history.Any(row => row.IsHistorySuccessful && DefenderSuccessCovers(pending, row));

        var cutoff = TruncateToSeconds(pendingSnapshotAt);
        foreach (var row in history)
        {
            if (!row.IsHistorySuccessful || IsDefender(row)) continue;
            var when = ParseDate(row.Date);
            if (when is null || when <= cutoff) continue;
            if (SameUpdate(pending, row)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the live Defender signature version already covers the pending offer.
    /// </summary>
    internal static bool DefenderInstalledCovers(UpdateEntry pending, Version? installedSignature) =>
        IsDefender(pending) && DefenderLineCoveredBySignature(
            $"{pending.KB} {pending.Title} {pending.UnifiedVersion}".Trim(),
            installedSignature);

    /// <summary>
    /// Drop monitor-status lines for Defender definitions already on the PC.
    /// </summary>
    internal static string[] FilterInstalledDefenderLines(IEnumerable<string> updates, Version? installedSignature)
    {
        if (installedSignature is null) return updates.ToArray();
        return updates.Where(line => !DefenderLineCoveredBySignature(line, installedSignature)).ToArray();
    }

    internal static bool DefenderLineCoveredBySignature(string? line, Version? installedSignature)
    {
        if (installedSignature is null || string.IsNullOrWhiteSpace(line)) return false;
        if (!line.Contains("KB2267602", StringComparison.OrdinalIgnoreCase)) return false;
        var offered = ReadSignatureVersion(line);
        // No version in the line: any installed Defender signature means this KB is no longer pending.
        if (offered is null) return true;
        return installedSignature >= offered;
    }

    /// <summary>
    /// Newest successful Defender definition install time from History (local).
    /// </summary>
    internal static DateTime? LatestDefenderSuccessAt(IEnumerable<UpdateEntry> history)
    {
        DateTime? best = null;
        foreach (var row in history)
        {
            if (!row.IsHistorySuccessful || !IsDefender(row)) continue;
            var when = ParseDate(row.Date);
            if (when is null) continue;
            if (best is null || when > best) best = when;
        }
        return best;
    }

    private static bool DefenderSuccessCovers(UpdateEntry pending, UpdateEntry success)
    {
        if (!IsDefender(success)) return false;
        if (TitlesEqual(pending.Title, success.Title)) return true;
        var pendingVersion = ReadEntrySignatureVersion(pending);
        var successVersion = ReadEntrySignatureVersion(success);
        return pendingVersion is not null && successVersion is not null && successVersion >= pendingVersion;
    }

    private static bool SameUpdate(UpdateEntry pending, UpdateEntry success)
    {
        if (!string.IsNullOrWhiteSpace(pending.UpdateId) &&
            pending.UpdateId.Equals(success.UpdateId, StringComparison.OrdinalIgnoreCase))
            return true;

        var pendingKbs = KbsOf(pending);
        var successKbs = KbsOf(success);
        if (pendingKbs.Count > 0 && successKbs.Count > 0)
            return pendingKbs.SetEquals(successKbs);
        return pendingKbs.Count == 0 && successKbs.Count == 0 && TitlesEqual(pending.Title, success.Title);
    }

    private static bool IsDefender(UpdateEntry entry) => KbsOf(entry).Contains("KB2267602");

    private static HashSet<string> KbsOf(UpdateEntry entry)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(string? text)
        {
            foreach (Match match in KbPattern.Matches(text ?? ""))
                found.Add(match.Value.ToUpperInvariant());
        }
        Collect(entry.KB);
        if (found.Count == 0) Collect(entry.Title);
        return found;
    }

    private static Version? ReadEntrySignatureVersion(UpdateEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.VersionDisplay) &&
            Version.TryParse(NormalizeVersionText(entry.VersionDisplay), out var fromDisplay))
            return fromDisplay;
        if (!string.IsNullOrWhiteSpace(entry.UnifiedVersion) &&
            Version.TryParse(NormalizeVersionText(entry.UnifiedVersion), out var fromUnified))
            return fromUnified;
        return ReadSignatureVersion(entry.Title);
    }

    private static Version? ReadSignatureVersion(string? title)
    {
        var match = SignatureVersion.Match(title ?? "");
        if (!match.Success) return null;
        return Version.TryParse(NormalizeVersionText(match.Groups["version"].Value), out var version) ? version : null;
    }

    private static string NormalizeVersionText(string text)
    {
        text = text.Trim();
        if (text.Count(c => c == '.') == 2) text += ".0";
        return text;
    }

    private static bool TitlesEqual(string? left, string? right) =>
        string.Equals(NormalizeTitle(left), NormalizeTitle(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTitle(string? title) => Whitespace.Replace(title ?? "", " ").Trim();

    private static DateTime TruncateToSeconds(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Kind);

    private static DateTime? ParseDate(string? value) => ReminderSchedule.TryParseDate(value);
}
