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

    /// <summary>
    /// Persist Installation Manager Completed/Failed rows into the same archive Historikk reads.
    /// WinGet (and driver/WUA installs that are not yet in live WUA history) otherwise never appear.
    /// </summary>
    internal static int RecordInstallOutcomes(IReadOnlyList<UpdateEntry> outcomes)
    {
        if (outcomes == null || outcomes.Count == 0) return 0;
        lock (Gate)
        {
            var state = LoadUnlocked();
            int recorded = 0;
            foreach (var entry in outcomes)
            {
                if (entry == null) continue;
                if (!entry.IsHistorySuccessful && !entry.IsHistoryFailed) continue;
                var copy = entry.Clone();
                if (string.IsNullOrWhiteSpace(copy.Date))
                    copy.Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                OperationalPriority.Apply(copy);
                string key = EventKey(copy);
                int index = state.Entries.FindIndex(existing => EventKey(existing) == key);
                if (index < 0) state.Entries.Add(copy);
                else state.Entries[index] = copy;
                recorded++;
            }
            if (recorded == 0) return 0;
            Trim(state);
            SaveUnlocked(state);
            return recorded;
        }
    }

    /// <summary>
    /// Build a Historikk row from an Installation Manager queue item.
    /// Returns null for non-terminal states (Pending / Verifying / Not verified).
    /// </summary>
    internal static UpdateEntry? FromInstallationQueueItem(InstallationQueueItem item, DateTime? at = null)
    {
        if (item == null) return null;
        bool failed = item.State.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
            || item.State.StartsWith("Feilet", StringComparison.OrdinalIgnoreCase);
        bool completed = InstallationManagerWindow.IsCompletedState(item.State);
        if (!failed && !completed) return null;

        string blob = item.State + " " + (item.Tip ?? "");
        var hr = Regex.Match(blob, @"0x[0-9A-Fa-f]{8}");
        string error = hr.Success ? hr.Value : "";
        if (string.IsNullOrWhiteSpace(error) &&
            (item.State.Contains("tech mismatch", StringComparison.OrdinalIgnoreCase)
             || item.State.Contains("teknologi-mismatch", StringComparison.OrdinalIgnoreCase)
             || (item.Tip?.Contains("0x8A15008E", StringComparison.OrdinalIgnoreCase) ?? false)
             || (item.Tip?.Contains("-1978335090", StringComparison.OrdinalIgnoreCase) ?? false)))
            error = "0x8A15008E";

        string category = item.Source.Contains("WinGet", StringComparison.OrdinalIgnoreCase) ? "Application"
            : item.Source.Contains("Driver", StringComparison.OrdinalIgnoreCase) ? "Driver / firmware"
            : "Windows Update";

        // Match WindowsUpdateReader.History(): KB column from title so DisplayIdentityKey
        // can collapse this synthetic row with the live WUA event once it appears.
        string title = item.Name ?? "";
        var kbs = new List<string>();
        foreach (Match match in KbPattern.Matches(title))
        {
            string kb = match.Value.ToUpperInvariant();
            if (!kbs.Contains(kb, StringComparer.OrdinalIgnoreCase))
                kbs.Add(kb);
        }

        return new UpdateEntry
        {
            Title = title,
            KB = string.Join(", ", kbs),
            Category = category,
            Source = string.IsNullOrWhiteSpace(item.Source) ? "Installation Manager" : item.Source,
            Status = failed ? "Mislykket" : "Vellykket",
            ErrorCode = error,
            StatusNote = string.IsNullOrWhiteSpace(item.Tip) ? "" : item.Tip,
            Date = (at ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            UpdateId = item.UpdateId,
            SafeDefault = false
        };
    }

    /// <summary>
    /// Record an OEM install outcome that never goes through WUA.
    /// Download-only is not written to History (product: only Vellykket / Mislykket).
    /// </summary>
    internal static UpdateEntry? RecordOemOutcome(
        UpdateEntry source,
        string status,
        string? statusNote = null,
        DateTime? at = null)
    {
        if (source == null) return null;
        bool ok = status is "Vellykket" or "Successful" or "Installert";
        bool failed = status is "Mislykket" or "Failed" or "Avbrutt" or "Cancelled" or "Feilet / avbrutt";
        if (!ok && !failed) return null;

        string title = string.IsNullOrWhiteSpace(source.Title) ? source.DisplayTitle : source.Title;
        title = StripDownloadedSuffix(title);

        var entry = new UpdateEntry
        {
            Title = title,
            KB = string.IsNullOrWhiteSpace(source.KB) ? "OEM" : source.KB,
            Category = "Driver / firmware",
            Source = string.IsNullOrWhiteSpace(source.Source) ? "OEM" : source.Source,
            Status = ok ? "Vellykket" : "Mislykket",
            StatusNote = statusNote ?? "",
            Priority = source.Priority,
            PriorityExplanation = source.PriorityExplanation,
            DriverClassification = "OEM",
            DriverStatus = "",
            DriverVersionFromWua = source.DriverVersionFromWua,
            DriverManufacturer = source.DriverManufacturer,
            DriverClass = source.DriverClass,
            OemPackageId = source.OemPackageId,
            // Never carry package URLs into History — no DOC/OEM download affordance there.
            ExternalUrl = "",
            ReadmeUrl = "",
            Date = (at ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            UpdateId = "",
            SafeDefault = false
        };
        return RecordInstallOutcomes(new[] { entry }) > 0 ? entry : null;
    }

    /// <summary>Mark a failed History row Fixed so it no longer counts as Unresolved.</summary>
    internal static void MarkClearedUnresolved(UpdateEntry entry, string? note = null)
    {
        if (entry == null || !entry.IsHistoryFailed) return;
        lock (Gate)
        {
            var state = LoadUnlocked();
            string key = EventKey(entry);
            int index = state.Entries.FindIndex(existing => EventKey(existing) == key);
            var copy = (index >= 0 ? state.Entries[index] : entry).Clone();
            copy.Status = "Fixed";
            copy.StatusNote = string.IsNullOrWhiteSpace(note)
                ? "Marked Fixed because Unresolved was cleared by the user. The original error code is unchanged."
                : note;
            copy.ExternalUrl = "";
            copy.ReadmeUrl = "";
            if (index < 0) state.Entries.Add(copy);
            else state.Entries[index] = copy;
            Trim(state);
            SaveUnlocked(state);
        }
    }

    private static string StripDownloadedSuffix(string title)
    {
        title = Regex.Replace(title, @"\s*\(downloaded\)\s*$", "", RegexOptions.IgnoreCase).Trim();
        title = Regex.Replace(title, @"\s*\(lastet ned\)\s*$", "", RegexOptions.IgnoreCase).Trim();
        return title;
    }

    private static bool IsDownloadedHistoryRow(UpdateEntry entry)
    {
        if (entry.Status is "Downloaded" or "Lastet ned") return true;
        string title = entry.Title ?? "";
        return title.Contains("(downloaded)", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("(lastet ned)", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drop legacy Downloaded History rows from the archive (and live merge).</summary>
    internal static int PruneDownloadedHistory()
    {
        lock (Gate)
        {
            var state = LoadUnlocked();
            int before = state.Entries.Count;
            state.Entries.RemoveAll(IsDownloadedHistoryRow);
            if (state.Entries.Count == before) return 0;
            SaveUnlocked(state);
            return before - state.Entries.Count;
        }
    }

    /// <summary>
    /// Record Completed and Failed Installation Manager rows for Historikk.
    /// </summary>
    internal static int RecordInstallationManagerQueue(IEnumerable<InstallationQueueItem> queue, DateTime? at = null)
    {
        if (queue == null) return 0;
        var when = at ?? DateTime.Now;
        // Stagger identical titles by one second when needed so EventKey stays unique within a run.
        int offsetSeconds = 0;
        var entries = new List<UpdateEntry>();
        foreach (var item in queue)
        {
            var entry = FromInstallationQueueItem(item, when.AddSeconds(offsetSeconds));
            if (entry == null) continue;
            entries.Add(entry);
            offsetSeconds++;
        }
        return RecordInstallOutcomes(entries);
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
        lock (Gate)
        {
            state = LoadUnlocked();
            // Legacy builds wrote Downloaded rows; drop them from the archive permanently.
            int before = state.Entries.Count;
            state.Entries.RemoveAll(IsDownloadedHistoryRow);
            if (state.Entries.Count != before)
                SaveUnlocked(state);
        }

        var liveKeys = new HashSet<string>(live.Select(EventKey), StringComparer.OrdinalIgnoreCase);
        var combined = new Dictionary<string, UpdateEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in state.Entries)
            combined[EventKey(entry)] = entry.Clone();
        foreach (var entry in live)
        {
            if (IsDownloadedHistoryRow(entry)) continue;
            combined[EventKey(entry)] = entry.Clone();
        }

        foreach (var pair in combined)
        {
            if (!liveKeys.Contains(pair.Key) && ShouldMarkPreservedSource(pair.Value))
                MarkPreservedSource(pair.Value);
            // History must never expose OEM download buttons.
            pair.Value.ExternalUrl = "";
            pair.Value.ReadmeUrl = "";
        }

        var rows = combined.Values.ToList();
        rows.RemoveAll(IsDownloadedHistoryRow);
        // A failed row for an update that already succeeded is the same event twice
        // (often a blocked retry such as 0x80240016). Keep a failure that is a new
        // attempt more than a day after the latest success.
        rows.RemoveAll(entry => CoveredBySuccess(entry, rows));
        ApplyFixed(rows, state);
        var hidden = new HashSet<string>(state.HiddenEventKeys, StringComparer.OrdinalIgnoreCase);
        if (hidden.Count > 0 || state.HiddenDisplayIdentities.Count > 0)
            rows.RemoveAll(entry => IsHidden(entry, hidden, state.HiddenDisplayIdentities));
        // Collapse History rows for the same update and outcome (same WUA UpdateId, or the
        // same title, KB, version, status, and error code). Empty KB/Version count as equal.
        // Keep the newest Date/Time. Distinct versions (e.g. Defender KB2267602), success vs
        // failure, and one driver version under two UpdateIds stay separate.
        rows = CollapseIdenticalRows(rows);
        foreach (var entry in rows)
            OperationalPriority.Apply(entry);
        return rows.OrderByDescending(entry => ParseDate(entry.Date) ?? DateTime.MinValue).ToList();
    }

    /// <summary>
    /// One visible History row per installed update. Prefer the newest Date/Time.
    /// </summary>
    private static List<UpdateEntry> CollapseIdenticalRows(IReadOnlyList<UpdateEntry> rows) =>
        OutcomeIdentity.Collapse(rows, ParseDate);

    /// <summary>
    /// Stable History display key: Update title + KB + Version + Status (+ ErrorCode).
    /// Empty fields match each other. Status differs keep success and failure both visible.
    /// The title is compared without its version and WUA date noise, and the status without
    /// regard to the language it was stored in.
    /// </summary>
    internal static string DisplayIdentityKey(UpdateEntry entry) => OutcomeIdentity.TitleKey(entry);

    /// <summary>
    /// <see cref="DisplayIdentityKey"/> as builds up to 0.7.2.59 computed it: the displayed
    /// title (Windows Update history driver rows kept their date noise) and the stored status
    /// text. Rows hidden by those builds are saved under this key.
    /// </summary>
    internal static string LegacyDisplayIdentityKey(UpdateEntry entry)
    {
        static string Norm(string? value) =>
            Regex.Replace(value ?? "", @"\s+", " ").Trim();

        bool uncategorizedDriver = entry.HasWuaDriverDateNoise
            && entry.Category is not ("Driver / firmware" or "Application");
        return string.Join("|",
            Norm(uncategorizedDriver ? entry.Title : entry.DisplayTitle).ToUpperInvariant(),
            Norm(entry.KB).ToUpperInvariant(),
            Norm(entry.UnifiedVersion),
            Norm(entry.Status).ToUpperInvariant(),
            Norm(entry.ErrorCode).ToUpperInvariant());
    }

    /// <summary>
    /// Remember a history row the user chose to hide. Windows Update history itself is left unchanged.
    /// Hides every existing event that shares the collapsed display identity up through the
    /// newest Date/Time on that row; a later genuine new attempt stays visible.
    /// </summary>
    internal static void HideFromDisplay(UpdateEntry entry)
    {
        lock (Gate)
        {
            var state = LoadUnlocked();
            string identity = DisplayIdentityKey(entry);
            var maxDate = ParseDate(entry.Date) ?? DateTime.MinValue;

            void HideEvent(UpdateEntry candidate)
            {
                string key = EventKey(candidate);
                if (!state.HiddenEventKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    state.HiddenEventKeys.Add(key);
                var when = ParseDate(candidate.Date);
                if (when is DateTime dated && dated > maxDate)
                    maxDate = dated;
            }

            HideEvent(entry);
            foreach (var existing in state.Entries)
            {
                if (string.Equals(DisplayIdentityKey(existing), identity, StringComparison.OrdinalIgnoreCase))
                    HideEvent(existing);
            }

            string maxDateText = maxDate == DateTime.MinValue
                ? (entry.Date ?? "").Trim()
                : maxDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            int index = state.HiddenDisplayIdentities.FindIndex(rule =>
                string.Equals(rule.Identity, identity, StringComparison.OrdinalIgnoreCase));
            var rule = new HiddenDisplayIdentity { Identity = identity, MaxDate = maxDateText };
            if (index < 0)
                state.HiddenDisplayIdentities.Add(rule);
            else
            {
                var existingMax = ParseDate(state.HiddenDisplayIdentities[index].MaxDate) ?? DateTime.MinValue;
                if (maxDate >= existingMax)
                    state.HiddenDisplayIdentities[index] = rule;
            }
            SaveUnlocked(state);
        }
    }

    private static bool IsHidden(
        UpdateEntry entry,
        HashSet<string> hiddenEventKeys,
        IReadOnlyList<HiddenDisplayIdentity> hiddenIdentities)
    {
        if (hiddenEventKeys.Contains(EventKey(entry))) return true;
        if (hiddenIdentities.Count == 0) return false;
        string identity = DisplayIdentityKey(entry);
        string legacyIdentity = LegacyDisplayIdentityKey(entry);
        var when = ParseDate(entry.Date) ?? DateTime.MinValue;
        foreach (var rule in hiddenIdentities)
        {
            if (!string.Equals(rule.Identity, identity, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rule.Identity, legacyIdentity, StringComparison.OrdinalIgnoreCase)) continue;
            // No recorded cutoff: treat as hide-all for this identity (legacy-safe).
            if (string.IsNullOrWhiteSpace(rule.MaxDate)) return true;
            var max = ParseDate(rule.MaxDate) ?? DateTime.MaxValue;
            if (when <= max) return true;
        }
        return false;
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

    /// <summary>
    /// Only WUA-backed archive rows should get the "saved before Windows Update fix" label.
    /// Installation Manager outcomes (WinGet / Driver / Windows Update) are not WUA history copies.
    /// </summary>
    private static bool ShouldMarkPreservedSource(UpdateEntry entry)
    {
        string source = entry.Source ?? "";
        if (string.IsNullOrWhiteSpace(source)) return true;
        if (source.Contains("WinGet", StringComparison.OrdinalIgnoreCase)) return false;
        if (source.Contains("Installation Manager", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(source, "Driver", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(source, "Windows Update", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(source, "Application", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
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

    private static DateTime? ParseDate(string? value) => ReminderSchedule.TryParseDate(value);

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
        /// <summary>
        /// Collapsed History identities the user hid, with a Date/Time cutoff so older
        /// duplicates stay hidden while a later new attempt can reappear.
        /// </summary>
        public List<HiddenDisplayIdentity> HiddenDisplayIdentities { get; set; } = new();
    }

    private sealed class HiddenDisplayIdentity
    {
        public string Identity { get; set; } = "";
        /// <summary>Inclusive newest Date/Time (yyyy-MM-dd HH:mm:ss) covered by the hide.</summary>
        public string MaxDate { get; set; } = "";
    }
}
