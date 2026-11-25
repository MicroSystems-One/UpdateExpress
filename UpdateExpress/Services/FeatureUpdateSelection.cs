using System.Text.RegularExpressions;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

/// <summary>
/// Among same-product feature-update rows newer than the installed release, keep only the
/// highest target version. Never hide a verified WUA offer in favour of a registry candidate.
/// Win10→Win11 cross-product offers and non-feature rows are left alone.
/// </summary>
public static class FeatureUpdateSelection
{
    private static readonly Regex ReleaseToken = new(@"\b(?<rel>\d{2}H[12])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Windows11Title = new(@"\bWindows\s*11\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Windows10Title = new(@"\bWindows\s*10\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Filters a pending list so consumers (grid, counts, Select All, IM, reminders, unattended)
    /// all see the same newest feature-update row.
    /// </summary>
    public static List<UpdateEntry> KeepNewest(
        IEnumerable<UpdateEntry> entries,
        string installedDisplayVersion,
        int installedBuild)
    {
        var list = entries?.ToList() ?? new List<UpdateEntry>();
        if (list.Count == 0) return list;

        int installedProduct = ProductFromBuild(installedBuild);
        int? installedRank = RankRelease(installedDisplayVersion);

        var keep = new List<UpdateEntry>();
        var sameProductUpgrades = new List<(UpdateEntry Entry, int Rank, bool Verified)>();

        foreach (var entry in list)
        {
            if (!TryDescribeFeatureUpdate(entry, out int targetProduct, out int targetRank))
            {
                keep.Add(entry);
                continue;
            }

            // Win10 → Win11 (or any other cross-product) offers stay visible as-is.
            if (targetProduct != installedProduct)
            {
                keep.Add(entry);
                continue;
            }

            // Same or older than installed — not an upgrade path we collapse; leave alone
            // (should be rare for pending offers).
            if (installedRank is int installed && targetRank <= installed)
            {
                keep.Add(entry);
                continue;
            }

            sameProductUpgrades.Add((entry, targetRank, IsVerifiedWuaOffer(entry)));
        }

        if (sameProductUpgrades.Count == 0)
            return keep;

        keep.AddRange(SelectNewestSameProduct(sameProductUpgrades));
        return keep;
    }

    /// <summary>True when the entry is a Windows feature / enablement / registry candidate row.</summary>
    public static bool IsFeatureUpdateRow(UpdateEntry entry)
    {
        if (entry is null) return false;
        if (string.Equals(entry.Category, "Versjonsoppgradering", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(entry.Source, FeatureUpdateOfferReader.Source, StringComparison.OrdinalIgnoreCase))
            return true;
        if (entry.OpensWindowsUpdate) return true;
        string title = entry.Title ?? "";
        return title.Contains("feature update", StringComparison.OrdinalIgnoreCase)
            || title.Contains("funksjonsoppdatering", StringComparison.OrdinalIgnoreCase)
            || title.Contains("enablement package", StringComparison.OrdinalIgnoreCase)
            || (ReleaseToken.IsMatch(title) && (Windows11Title.IsMatch(title) || Windows10Title.IsMatch(title)));
    }

    public static bool IsVerifiedWuaOffer(UpdateEntry entry) =>
        entry is not null
        && !string.IsNullOrWhiteSpace(entry.UpdateId)
        && !string.Equals(entry.Source, FeatureUpdateOfferReader.Source, StringComparison.OrdinalIgnoreCase)
        && !entry.OpensWindowsUpdate;

    public static bool IsRegistryCandidate(UpdateEntry entry) =>
        entry is not null
        && (string.Equals(entry.Source, FeatureUpdateOfferReader.Source, StringComparison.OrdinalIgnoreCase)
            || entry.OpensWindowsUpdate);

    public static int? RankRelease(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = ReleaseToken.Match(text);
        if (!match.Success) return null;
        string rel = match.Groups["rel"].Value.ToUpperInvariant();
        if (!int.TryParse(rel.AsSpan(0, 2), out int year)) return null;
        int half = rel.EndsWith("H2", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        return year * 10 + half;
    }

    public static int ProductFromBuild(int build) => build >= 22000 ? 11 : 10;

    public static int? ProductFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (Windows11Title.IsMatch(title)) return 11;
        if (Windows10Title.IsMatch(title)) return 10;
        return null;
    }

    private static bool TryDescribeFeatureUpdate(UpdateEntry entry, out int product, out int rank)
    {
        product = 0;
        rank = 0;
        if (!IsFeatureUpdateRow(entry)) return false;
        int? parsedRank = RankRelease(entry.Title);
        if (parsedRank is null) return false;
        int? parsedProduct = ProductFromTitle(entry.Title);
        // Registry candidate titles are "Windows 11, 24H2" — product always present.
        // If somehow missing, fail open (do not treat as same-product upgrade).
        if (parsedProduct is null) return false;
        product = parsedProduct.Value;
        rank = parsedRank.Value;
        return true;
    }

    private static IEnumerable<UpdateEntry> SelectNewestSameProduct(
        List<(UpdateEntry Entry, int Rank, bool Verified)> upgrades)
    {
        var verified = upgrades.Where(x => x.Verified).ToList();
        if (verified.Count > 0)
        {
            // Prefer the highest verified WUA offer. Never drop it for a newer registry candidate.
            int best = verified.Max(x => x.Rank);
            return verified.Where(x => x.Rank == best).Select(x => x.Entry);
        }

        // No verified WUA feature update — keep only the newest registry / Settings candidate.
        int bestCandidate = upgrades.Max(x => x.Rank);
        return upgrades.Where(x => x.Rank == bestCandidate).Select(x => x.Entry);
    }
}
