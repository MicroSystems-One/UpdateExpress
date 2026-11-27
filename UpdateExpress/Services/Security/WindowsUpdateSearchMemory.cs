using System;
using System.Collections.Generic;
using System.Linq;
using UpdateExpress.Models;

namespace UpdateExpress.Services.Security;

/// <summary>One update that waits, as the Security Overview names it.</summary>
internal sealed record WaitingUpdate(string Kb, string Title);

/// <summary>What the last Windows Update search of this session found, less what has been installed since.</summary>
/// <param name="Offered">How many updates from Windows Update still wait, of any kind.</param>
internal sealed record WindowsUpdateSearchResult(
    DateTimeOffset SearchedAt,
    int Offered,
    IReadOnlyList<WaitingUpdate> SecurityUpdates);

/// <summary>
/// The result of the last Windows Update search the main window ran, kept in memory for the
/// check of waiting security updates. The Security Overview never searches by itself: a
/// search is the main window's work and can take minutes. Nothing is kept on disk, so a
/// newly started Update Express knows of no search.
/// </summary>
internal static class WindowsUpdateSearchMemory
{
    private sealed record Row(string Identity, bool Security, WaitingUpdate Update);

    private static readonly object Gate = new();
    private static DateTimeOffset? _searchedAt;
    private static List<Row> _waiting = new();
    /// <summary>Every update a search of this session has offered, also those installed since.</summary>
    private static readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);

    internal static WindowsUpdateSearchResult? Last
    {
        get
        {
            lock (Gate)
            {
                return _searchedAt is DateTimeOffset at
                    ? new WindowsUpdateSearchResult(at, _waiting.Count, _waiting.Where(row => row.Security).Select(row => row.Update).ToArray())
                    : null;
            }
        }
    }

    /// <summary>A search has finished and its result replaces the list on screen.</summary>
    internal static void Record(IEnumerable<UpdateEntry> found, DateTimeOffset searchedAt)
    {
        var rows = Rows(found);
        lock (Gate)
        {
            _searchedAt = searchedAt;
            _waiting = rows;
            Seen.Clear();
            Seen.UnionWith(rows.Select(row => row.Identity));
        }
    }

    /// <summary>
    /// A search made to verify an installation has finished; the list on screen stays as it
    /// was. Updates it offers for the first time are added. An update that was installed
    /// after an earlier search does not come back, although Windows Update goes on offering
    /// it until the PC has restarted. Without an earlier search this is the first one.
    /// </summary>
    internal static void Learn(IEnumerable<UpdateEntry> found, DateTimeOffset searchedAt)
    {
        var rows = Rows(found);
        lock (Gate)
        {
            if (_searchedAt is not null)
            {
                var waiting = new HashSet<string>(_waiting.Select(row => row.Identity), StringComparer.OrdinalIgnoreCase);
                rows = rows.Where(row => waiting.Contains(row.Identity) || !Seen.Contains(row.Identity)).ToList();
            }
            _searchedAt = searchedAt;
            _waiting = rows;
            Seen.UnionWith(rows.Select(row => row.Identity));
        }
    }

    /// <summary>
    /// Rows left the list on screen because their updates were installed. The time of the
    /// search stays. Does nothing when no search is known.
    /// </summary>
    internal static void Remove(IEnumerable<UpdateEntry> installed)
    {
        var gone = new HashSet<string>(Rows(installed).Select(row => row.Identity), StringComparer.OrdinalIgnoreCase);
        if (gone.Count == 0) return;
        lock (Gate)
        {
            if (_searchedAt is null) return;
            _waiting = _waiting.Where(row => !gone.Contains(row.Identity)).ToList();
        }
    }

    /// <summary>What the search found no longer holds, and no new search has been made.</summary>
    internal static void Forget()
    {
        lock (Gate)
        {
            _searchedAt = null;
            _waiting = new List<Row>();
            Seen.Clear();
        }
    }

    private static List<Row> Rows(IEnumerable<UpdateEntry>? entries)
    {
        var rows = new List<Row>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries ?? Enumerable.Empty<UpdateEntry>())
        {
            // Rows from Windows Update only: OEM driver packages and apps share the list.
            if (entry is null || !(entry.Source ?? "").StartsWith("WUA", StringComparison.OrdinalIgnoreCase)) continue;
            string title = (entry.Title ?? "").Trim();
            string id = (entry.UpdateId ?? "").Trim();
            string identity = id.Length > 0 ? "id:" + id : "title:" + title;
            if (!taken.Add(identity)) continue;
            rows.Add(new Row(identity, entry.IsSecurityUpdate, new WaitingUpdate((entry.KB ?? "").Trim(), title)));
        }
        return rows;
    }
}
