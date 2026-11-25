using System.Text.RegularExpressions;

namespace UpdateExpress.Models;

/// <summary>
/// Decides when History / outcome rows describe the same installed update, so History and
/// the Success / Failures lists show it once. One install is recorded up to three times: by
/// Windows Update history, by the Installation Manager row kept in the History archive, and
/// by the Installation Manager outcome held in memory until the next History read.
/// </summary>
internal static class OutcomeIdentity
{
    private static string Norm(string? value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();

    private static string OutcomeKey(UpdateEntry entry) =>
        HistoryStatusDisplay.StatusKey(entry.Status) + "|" + Norm(entry.ErrorCode).ToUpperInvariant();

    /// <summary>
    /// Title + KB + Version + status (+ ErrorCode). Empty fields match each other. A success
    /// and a failure of one update, or two different error codes, stay separate rows.
    /// </summary>
    internal static string TitleKey(UpdateEntry entry) => string.Join("|",
        Norm(entry.IdentityTitle).ToUpperInvariant(),
        Norm(entry.KB).ToUpperInvariant(),
        Norm(entry.UnifiedVersion),
        OutcomeKey(entry));

    private static string IdKey(UpdateEntry entry) =>
        entry.UpdateId.Trim().ToUpperInvariant() + "|" + OutcomeKey(entry);

    /// <summary>
    /// One row per installed update and outcome. Rows are the same update when they have
    /// the same WUA UpdateId, or the same <see cref="TitleKey"/>.
    /// <para>
    /// Exception: rows whose title carries a version (drivers) and that have different
    /// UpdateIds are different updates, such as one driver version published for two
    /// hardware IDs. They stay separate. A row with that title and no id (archived before the
    /// id was recorded) is then one of them, it cannot be told which, and the id rows stand
    /// for it. Titles without a version keep collapsing across ids: Store apps reuse one title
    /// for every release.
    /// </para>
    /// Of the rows for one update, a <paramref name="preferred"/> row wins, then the newest.
    /// </summary>
    internal static List<UpdateEntry> Collapse(
        IEnumerable<UpdateEntry> rows,
        Func<string?, DateTime?> parseDate,
        Func<UpdateEntry, bool>? preferred = null)
    {
        // Union-find over id keys and title keys; each row hangs on one of them.
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string node)
        {
            if (!parent.TryGetValue(node, out var up)) return parent[node] = node;
            return up == node ? node : parent[node] = Find(up);
        }
        void Union(string a, string b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b) parent[b] = a;
        }

        var placed = new List<(string Node, UpdateEntry Row)>();
        var idNodesByVersionedTitle = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var versionedWithoutId = new List<(string TitleNode, UpdateEntry Row)>();

        foreach (var row in rows)
        {
            string titleNode = "title:" + TitleKey(row);
            bool versioned = Norm(row.UnifiedVersion).Length > 0;
            if (!string.IsNullOrWhiteSpace(row.UpdateId))
            {
                string idNode = "id:" + IdKey(row);
                placed.Add((idNode, row));
                if (!versioned)
                    Union(idNode, titleNode);
                else if (idNodesByVersionedTitle.TryGetValue(titleNode, out var idNodes))
                    idNodes.Add(idNode);
                else
                    idNodesByVersionedTitle[titleNode] = new HashSet<string>(StringComparer.Ordinal) { idNode };
            }
            else if (versioned)
                versionedWithoutId.Add((titleNode, row));
            else
                placed.Add((titleNode, row));
        }

        foreach (var (titleNode, row) in versionedWithoutId)
        {
            if (!idNodesByVersionedTitle.TryGetValue(titleNode, out var idNodes))
            {
                placed.Add((titleNode, row));
                continue;
            }
            var updates = idNodes.Select(Find).Distinct(StringComparer.Ordinal).ToList();
            if (updates.Count == 1) placed.Add((updates[0], row));
        }

        return placed
            .GroupBy(item => Find(item.Node), StringComparer.Ordinal)
            .Select(group => Best(group.Select(item => item.Row)))
            .ToList();

        UpdateEntry Best(IEnumerable<UpdateEntry> group)
        {
            UpdateEntry? best = null;
            foreach (var row in group)
            {
                if (best is null) { best = row; continue; }
                bool rowPreferred = preferred?.Invoke(row) ?? false;
                bool bestPreferred = preferred?.Invoke(best) ?? false;
                if (rowPreferred != bestPreferred)
                {
                    if (rowPreferred) best = row;
                    continue;
                }
                if ((parseDate(row.Date) ?? DateTime.MinValue) >= (parseDate(best.Date) ?? DateTime.MinValue))
                    best = row;
            }
            return best!;
        }
    }

    /// <summary>
    /// History rows plus the outcomes of the latest install run that History does not show
    /// yet, newest first. An outcome History already records is shown as the History row:
    /// that is the row the summary counts and that is still there after the next read.
    /// </summary>
    internal static List<UpdateEntry> MergeRecentWithHistory(
        IEnumerable<UpdateEntry> history,
        IEnumerable<UpdateEntry> recent,
        Func<string?, DateTime?> parseDate)
    {
        var historyRows = history.ToList();
        var fromHistory = new HashSet<UpdateEntry>(historyRows, ReferenceEqualityComparer.Instance);
        return Collapse(historyRows.Concat(recent), parseDate, fromHistory.Contains)
            .OrderByDescending(entry => parseDate(entry.Date) ?? DateTime.MinValue)
            .ToList();
    }
}
