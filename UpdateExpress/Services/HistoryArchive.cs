using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

/// <summary>
/// Copy of Windows Update history kept by Update Express.
/// Quick/Medium/Deep fix renames SoftwareDistribution, which empties WUA history.
/// The archive is the copy shown afterwards. The Windows database is not restored.
/// </summary>
internal static class HistoryArchive
{
    private static readonly object Gate = new();
    private static readonly Regex KbPattern = new(@"KB\d{5,9}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SignatureVersion = new(
        @"(?:versjon|version)\s+(?<version>\d+(?:\.\d+){2,3})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IgnoreReadOnlyProperties = true
    };

    private static string FilePath => Path.Combine(ActivityLog.DirectoryPath, "wua-history-archive.json");

    /// <summary>
    /// Drop the previous post-fix scan. Call only after SoftwareDistribution has been renamed,
    /// so a failed later scan cannot keep stale update ids or stale "not offered" results.
    /// </summary>
    internal static void BeginFix()
    {
        lock (Gate)
        {
            var state = LoadUnlocked();
            state.PostFixScanCompleted = false;
            state.PostFixScanAt = null;
            state.PendingKbs.Clear();
            state.PendingEntries.Clear();
            state.LastScanError = null;
            SaveUnlocked(state);
        }
    }

    internal static int Remember(IReadOnlyList<UpdateEntry> live, UpdateEntry? fixTarget)
    {
        lock (Gate)
        {
            var state = LoadUnlocked();
            foreach (var entry in live)
            {
                if (entry.Status == "Fixed") continue;
                var copy = entry.Clone();
                OperationalPriority.Apply(copy);
                string key = EventKey(copy);
                int index = state.Entries.FindIndex(existing => EventKey(existing) == key);
                if (index < 0) state.Entries.Add(copy);
                else state.Entries[index] = copy;
            }
            Trim(state);
            if (fixTarget != null)
            {
                string targetKey = EventKey(fixTarget);
                if (!state.FixTargets.Contains(targetKey, StringComparer.OrdinalIgnoreCase))
                    state.FixTargets.Add(targetKey);
            }
            SaveUnlocked(state);
            return state.Entries.Count;
        }
    }

    internal static void RecordScan(IReadOnlyList<UpdateEntry> pending, bool success, string? error)
    {
        lock (Gate)
        {
            var state = LoadUnlocked();
            if (!success)
            {
                state.LastScanError = error;
                SaveUnlocked(state);
                return;
            }
            state.PostFixScanCompleted = true;
            state.PostFixScanAt = DateTimeOffset.Now;
            state.LastScanError = null;
            state.PendingKbs = pending.SelectMany(KbsOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            state.PendingEntries = pending.Select(entry => entry.Clone()).ToList();
            SaveUnlocked(state);
        }
    }

    /// <summary>Pending updates from the scan that followed the last successful fix. Null when that scan did not finish.</summary>
    internal static List<UpdateEntry>? CopyPendingEntries()
    {
        lock (Gate)
        {
            var state = LoadUnlocked();
            if (!state.PostFixScanCompleted) return null;
            return state.PendingEntries.Select(entry => entry.Clone()).ToList();
        }
    }

    internal static List<UpdateEntry> MergeForDisplay(IReadOnlyList<UpdateEntry> live)
    {
        HistoryArchiveState state;
        lock (Gate) state = LoadUnlocked();

        var liveKeys = new HashSet<string>(live.Select(EventKey), StringComparer.OrdinalIgnoreCase);
        var combined = new Dictionary<string, UpdateEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in state.Entries)
            combined[EventKey(entry)] = entry.Clone();
        foreach (var entry in live)
            combined[EventKey(entry)] = entry.Clone();

        foreach (var pair in combined)
        {
            if (!liveKeys.Contains(pair.Key))
                MarkPreservedSource(pair.Value);
        }

        var rows = combined.Values.ToList();
        // A failed row for an update that already succeeded is the same event twice
        // (often a blocked retry such as 0x80240016). Keep a failure that is a new
        // attempt more than a day after the latest success.
        rows.RemoveAll(entry => CoveredBySuccess(entry, rows));
        ApplyFixed(rows, state);
        var hidden = new HashSet<string>(state.HiddenEventKeys, StringComparer.OrdinalIgnoreCase);
        if (hidden.Count > 0)
            rows.RemoveAll(entry => hidden.Contains(EventKey(entry)));
        foreach (var entry in rows)
            OperationalPriority.Apply(entry);
        return rows.OrderByDescending(entry => ParseDate(entry.Date) ?? DateTime.MinValue).ToList();
    }

    /// <summary>Remember a history row the user chose to hide. Windows Update history itself is left unchanged.</summary>
    internal static void HideFromDisplay(UpdateEntry entry)
    {
        lock (Gate)
        {
            var state = LoadUnlocked();
            string key = EventKey(entry);
            if (!state.HiddenEventKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                state.HiddenEventKeys.Add(key);
            SaveUnlocked(state);
        }
    }

    internal static bool IsStillOffered(UpdateEntry history, IReadOnlyList<UpdateEntry> pending) =>
        pending.Any(item => SameIdentity(history, item));

    internal static UpdateEntry? FindOffered(UpdateEntry history, IReadOnlyList<UpdateEntry> pending) =>
        pending.FirstOrDefault(item => SameIdentity(history, item));

    private static bool CoveredBySuccess(UpdateEntry failure, IReadOnlyList<UpdateEntry> rows)
    {
        if (!failure.IsHistoryFailed) return false;
        var failureDate = ParseDate(failure.Date);
        DateTime? latestSuccess = null;
        bool any = false;
        foreach (var row in rows)
        {
            if (!row.IsHistorySuccessful || !SameIdentity(failure, row)) continue;
            any = true;
            var successDate = ParseDate(row.Date);
            if (failureDate is null || successDate is null) return true;
            if (successDate >= failureDate) return true;
            if (latestSuccess is null || successDate > latestSuccess) latestSuccess = successDate;
        }
        return any && latestSuccess is not null && failureDate is not null
            && failureDate.Value - latestSuccess.Value <= TimeSpan.FromHours(24);
    }

    internal static bool SameIdentity(UpdateEntry left, UpdateEntry right)
    {
        bool defender = OperationalPriority.IsDefenderKb(left.KB) || OperationalPriority.IsDefenderKb(right.KB);
        var leftKbs = KbsOf(left);
        var rightKbs = KbsOf(right);
        if (defender)
        {
            if (!leftKbs.Contains("KB2267602") || !rightKbs.Contains("KB2267602")) return false;
            var leftVersion = ReadSignatureVersion(left.Title);
            var rightVersion = ReadSignatureVersion(right.Title);
            if (leftVersion is null || rightVersion is null) return TitlesEqual(left.Title, right.Title);
            return leftVersion == rightVersion;
        }
        if (leftKbs.Count > 0 && rightKbs.Count > 0)
            return leftKbs.Any(kb => rightKbs.Contains(kb, StringComparer.OrdinalIgnoreCase));
        if (leftKbs.Count == 0 && rightKbs.Count == 0)
            return TitlesEqual(left.Title, right.Title);
        return false;
    }

    private static bool TitlesEqual(string? left, string? right)
    {
        static string Norm(string? value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();
        var a = Norm(left);
        var b = Norm(right);
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyFixed(List<UpdateEntry> rows, HistoryArchiveState state)
    {
        foreach (var entry in rows)
        {
            if (!entry.IsHistoryFailed) continue;
            if (HasLaterSuccess(entry, rows))
            {
                entry.StatusNote = "Marked Fixed because a later successful result for the same KB is already in history. The original error code is unchanged. This does not rewrite the Windows Update database.";
                entry.Status = "Fixed";
                continue;
            }
            if (!state.PostFixScanCompleted || state.PostFixScanAt is null) continue;
            if (!state.FixTargets.Contains(EventKey(entry), StringComparer.OrdinalIgnoreCase)) continue;
            var kbs = KbsOf(entry);
            if (kbs.Count == 0) continue;
            if (kbs.Any(kb => state.PendingKbs.Contains(kb, StringComparer.OrdinalIgnoreCase))) continue;
            entry.StatusNote =
                $"Marked Fixed because this update was not offered after the scan at {state.PostFixScanAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}. " +
                "Absence does not prove the original error was repaired.";
            entry.Status = "Fixed";
        }
    }

    private static bool HasLaterSuccess(UpdateEntry failure, IReadOnlyList<UpdateEntry> rows)
    {
        var failureKbs = KbsOf(failure);
        if (failureKbs.Count == 0) return false;
        var failureDate = ParseDate(failure.Date);
        if (failureDate is null) return false;
        bool defender = OperationalPriority.IsDefenderKb(failure.KB);
        Version? failureVersion = defender ? ReadSignatureVersion(failure.Title) : null;

        foreach (var row in rows)
        {
            if (!row.IsHistorySuccessful) continue;
            if (!KbsOf(row).Any(kb => failureKbs.Contains(kb, StringComparer.OrdinalIgnoreCase))) continue;
            var successDate = ParseDate(row.Date);
            if (successDate is null || successDate <= failureDate) continue;
            if (defender && failureVersion is not null)
            {
                var successVersion = ReadSignatureVersion(row.Title);
                if (successVersion is not null && successVersion < failureVersion) continue;
            }
            return true;
        }
        return false;
    }

    private static void MarkPreservedSource(UpdateEntry entry)
    {
        const string marker = "saved before Windows Update fix";
        if ((entry.Source ?? "").Contains(marker, StringComparison.OrdinalIgnoreCase)) return;
        entry.Source = string.IsNullOrWhiteSpace(entry.Source)
            ? "WUA-historikk (saved before Windows Update fix)"
            : entry.Source + " (saved before Windows Update fix)";
    }

    internal static string EventKey(UpdateEntry entry) =>
        string.Join("|",
            (entry.Date ?? "").Trim(),
            string.Join(",", KbsOf(entry)),
            (entry.Title ?? "").Trim(),
            (entry.ErrorCode ?? "").Trim().ToUpperInvariant(),
            (entry.Status ?? "").Trim().ToUpperInvariant());

    private static List<string> KbsOf(UpdateEntry entry)
    {
        var found = new List<string>();
        void Collect(string? text)
        {
            foreach (Match match in KbPattern.Matches(text ?? ""))
            {
                string kb = match.Value.ToUpperInvariant();
                if (!found.Contains(kb, StringComparer.OrdinalIgnoreCase))
                    found.Add(kb);
            }
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

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact;
        return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    private static void Trim(HistoryArchiveState state)
    {
        const int max = 1000;
        if (state.Entries.Count <= max) return;
        state.Entries = state.Entries
            .OrderByDescending(entry => ParseDate(entry.Date) ?? DateTime.MinValue)
            .Take(max)
            .ToList();
    }

    private static HistoryArchiveState LoadUnlocked()
    {
        try
        {
            if (!File.Exists(FilePath)) return new HistoryArchiveState();
            return JsonSerializer.Deserialize<HistoryArchiveState>(File.ReadAllText(FilePath), JsonOptions)
                   ?? new HistoryArchiveState();
        }
        catch
        {
            return new HistoryArchiveState();
        }
    }

    private static void SaveUnlocked(HistoryArchiveState state)
    {
        Directory.CreateDirectory(ActivityLog.DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private sealed class HistoryArchiveState
    {
        public List<UpdateEntry> Entries { get; set; } = new();
        public List<string> FixTargets { get; set; } = new();
        public bool PostFixScanCompleted { get; set; }
        public DateTimeOffset? PostFixScanAt { get; set; }
        public List<string> PendingKbs { get; set; } = new();
        public List<UpdateEntry> PendingEntries { get; set; } = new();
        public string? LastScanError { get; set; }
        public List<string> HiddenEventKeys { get; set; } = new();
    }
}
