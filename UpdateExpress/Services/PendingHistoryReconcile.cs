using System.Globalization;
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

    internal static bool ShouldHide(UpdateEntry pending, IReadOnlyList<UpdateEntry> history, DateTime pendingSnapshotAt)
    {
        if (IsDefender(pending))
            return history.Any(row => row.IsHistorySuccessful && DefenderSuccessCovers(pending, row));

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

    private static bool DefenderSuccessCovers(UpdateEntry pending, UpdateEntry success)
    {
        if (!IsDefender(success)) return false;
        if (TitlesEqual(pending.Title, success.Title)) return true;
        var pendingVersion = ReadSignatureVersion(pending.Title);
        var successVersion = ReadSignatureVersion(success.Title);
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

    private static Version? ReadSignatureVersion(string? title)
    {
        var match = SignatureVersion.Match(title ?? "");
        if (!match.Success) return null;
        string text = match.Groups["version"].Value;
        if (text.Count(c => c == '.') == 2) text += ".0";
        return Version.TryParse(text, out var version) ? version : null;
    }

    private static bool TitlesEqual(string? left, string? right) =>
        string.Equals(NormalizeTitle(left), NormalizeTitle(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTitle(string? title) => Whitespace.Replace(title ?? "", " ").Trim();

    private static DateTime TruncateToSeconds(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Kind);

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact;
        return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }
}
