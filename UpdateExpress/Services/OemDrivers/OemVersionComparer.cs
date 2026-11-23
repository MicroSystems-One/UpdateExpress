using System.Text.RegularExpressions;

namespace UpdateExpress.Services.OemDrivers;

/// <summary>
/// Compare OEM package / BIOS / driver version strings across Lenovo-style codes
/// (e.g. M28KT44A) and dotted numeric versions (e.g. 20.0.0.44).
/// </summary>
internal static class OemVersionComparer
{
    // Lenovo flash IDs: family prefix + revision digits + letter (M28KT44A, N2HET82W).
    private static readonly Regex LenovoCode = new(
        @"\b(?<prefix>[A-Z][A-Z0-9]*?[A-Z])(?<rev>\d{2,3})(?<letter>[A-Z])\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DottedVersion = new(
        @"\b(?<ver>\d+(?:\.\d+){1,7})\b",
        RegexOptions.Compiled);

    /// <summary>
    /// &gt;0 left newer, &lt;0 left older, 0 equal, null when versions cannot be compared.
    /// </summary>
    internal static int? TryCompare(string? left, string? right)
    {
        if (IsBlank(left) || IsBlank(right)) return null;
        left = left!.Trim();
        right = right!.Trim();
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return 0;

        var leftCode = BestLenovoCode(left);
        var rightCode = BestLenovoCode(right);
        if (leftCode is { } lc && rightCode is { } rc &&
            string.Equals(lc.Prefix, rc.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            int byRev = lc.Revision.CompareTo(rc.Revision);
            if (byRev != 0) return byRev;
            return string.Compare(lc.Letter, rc.Letter, StringComparison.OrdinalIgnoreCase);
        }

        string? leftDot = BestDotted(left);
        string? rightDot = BestDotted(right);
        if (leftDot is not null && rightDot is not null)
            return AppUpdateChecker.CompareVersions(leftDot, rightDot);

        // Slash compounds: M28KT44A/20.0.0.44 — recurse only when split reduces the input.
        // Atomic tokens (e.g. "A12" vs "A11") must not re-enter TryCompare or we stack-overflow.
        var leftParts = SplitParts(left).ToArray();
        var rightParts = SplitParts(right).ToArray();
        if (!SplitReduced(left, leftParts) && !SplitReduced(right, rightParts))
            return null;

        foreach (var leftPart in leftParts)
        {
            foreach (var rightPart in rightParts)
            {
                if (string.Equals(leftPart, left, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(rightPart, right, StringComparison.OrdinalIgnoreCase))
                    continue;
                int? nested = TryCompare(leftPart, rightPart);
                if (nested is not null and not 0) return nested;
                if (nested == 0) return 0;
            }
        }

        return null;
    }

    /// <summary>True when splitting produced a strictly smaller / multi-token form.</summary>
    private static bool SplitReduced(string original, string[] parts) =>
        parts.Length > 1 ||
        (parts.Length == 1 && !string.Equals(parts[0], original, StringComparison.OrdinalIgnoreCase));

    internal static bool IsBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) || value is "—" or "…" or "-" or "N/A" or "n/a";

    internal static string NormalizeComponentTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        string text = title.Trim();
        text = LenovoCode.Replace(text, " ");
        text = DottedVersion.Replace(text, " ");
        text = Regex.Replace(text, @"\([^)]*\)", " ");
        text = Regex.Replace(text, @"\s*·\s*", " ");
        text = Regex.Replace(text, @"\s*-\s*", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.ToUpperInvariant();
    }

    internal static IEnumerable<string> ExtractComparableTokens(string? value)
    {
        if (IsBlank(value)) yield break;
        foreach (Match m in LenovoCode.Matches(value!))
            yield return m.Value.ToUpperInvariant();
        foreach (Match m in DottedVersion.Matches(value!))
            yield return m.Groups["ver"].Value;
    }

    private static IEnumerable<string> SplitParts(string value) =>
        value.Split(new[] { '/', '\\', '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (string Prefix, int Revision, string Letter)? BestLenovoCode(string value)
    {
        (string Prefix, int Revision, string Letter)? best = null;
        foreach (Match m in LenovoCode.Matches(value))
        {
            if (!int.TryParse(m.Groups["rev"].Value, out int rev)) continue;
            var candidate = (m.Groups["prefix"].Value.ToUpperInvariant(), rev, m.Groups["letter"].Value.ToUpperInvariant());
            if (best is null || candidate.rev > best.Value.Revision)
                best = candidate;
        }
        return best;
    }

    private static string? BestDotted(string value)
    {
        string? best = null;
        foreach (Match m in DottedVersion.Matches(value))
        {
            string ver = m.Groups["ver"].Value;
            if (best is null || AppUpdateChecker.CompareVersions(ver, best) > 0)
                best = ver;
        }
        return best;
    }
}
