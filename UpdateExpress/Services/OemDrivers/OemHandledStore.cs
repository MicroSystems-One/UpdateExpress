using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UpdateExpress.Models;

namespace UpdateExpress.Services.OemDrivers;

/// <summary>
/// Persists OEM packages the user confirmed installed, reboot-held, or dismissed through
/// Update Express so the next catalog search does not keep offering the same Id+Version.
/// Download-only must never hide a row — only Installed / RebootHold / Dismissed count.
/// </summary>
internal static class OemHandledStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string FilePath => Path.Combine(ActivityLog.DirectoryPath, "oem-handled.json");

    internal static bool IsHandled(UpdateEntry entry) =>
        IsHandled(entry.OemPackageId, entry.UnifiedVersion);

    internal static bool IsHandled(string? packageId, string? version)
    {
        string key = Key(packageId, version);
        if (string.IsNullOrWhiteSpace(key)) return false;
        lock (Gate)
        {
            var state = LoadAndPruneUnlocked();
            return state.Items.Any(i =>
                IsHideOutcome(i.Outcome) &&
                string.Equals(Key(i.Id, i.Version), key, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Record a confirmed install only. Do not call for download-only.</summary>
    internal static void RememberInstalled(UpdateEntry entry, string? localPath = null) =>
        Remember(entry, "Installed", localPath);

    /// <summary>
    /// Hold an OEM Id+Version after the installer was launched (or exit 3010/1641) so
    /// Check for Updates cannot re-offer it while reboot is pending / before PnP registers.
    /// Cleared via <see cref="Forget"/> on a clear failed installer exit.
    /// </summary>
    internal static void RememberRebootHold(UpdateEntry entry, string? localPath = null) =>
        Remember(entry, "RebootHold", localPath);

    /// <summary>Persist hide so the catalog will not keep offering an incompatible package.</summary>
    internal static void RememberDismissed(UpdateEntry entry, string? localPath = null) =>
        Remember(entry, "Dismissed", localPath);

    private static void Remember(UpdateEntry entry, string outcome, string? localPath)
    {
        string id = entry.OemPackageId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(id))
            id = "url:" + (entry.ExternalUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id) || id == "url:")
        {
            // History OEM failures may have ExternalUrl cleared — fall back to title+version.
            id = "title:" + (entry.Title ?? "").Trim();
        }
        if (string.IsNullOrWhiteSpace(id) || id == "title:") return;

        lock (Gate)
        {
            var state = LoadAndPruneUnlocked();
            string key = Key(id, entry.UnifiedVersion);
            int index = state.Items.FindIndex(i =>
                string.Equals(Key(i.Id, i.Version), key, StringComparison.OrdinalIgnoreCase));
            var item = new HandledItem
            {
                Id = id,
                Version = NormalizeVersion(entry.UnifiedVersion),
                Title = entry.Title,
                Source = entry.Source,
                Outcome = outcome,
                LocalPath = localPath ?? "",
                At = DateTimeOffset.Now
            };
            if (index < 0) state.Items.Add(item);
            else state.Items[index] = item;
            if (state.Items.Count > 500)
                state.Items = state.Items
                    .Where(i => IsHideOutcome(i.Outcome))
                    .OrderByDescending(i => i.At)
                    .Take(500)
                    .ToList();
            SaveUnlocked(state);
        }
    }

    /// <summary>Forget a handled package so a failed/cancelled install can reappear.</summary>
    internal static void Forget(UpdateEntry entry)
    {
        string id = entry.OemPackageId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(id))
            id = "url:" + (entry.ExternalUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id) || id == "url:")
            id = "title:" + (entry.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id) || id == "title:") return;
        string key = Key(id, entry.UnifiedVersion);
        lock (Gate)
        {
            var state = LoadAndPruneUnlocked();
            int removed = state.Items.RemoveAll(i =>
                string.Equals(Key(i.Id, i.Version), key, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) SaveUnlocked(state);
        }
    }

    internal static IReadOnlyList<UpdateEntry> FilterUnhandled(IEnumerable<UpdateEntry> entries)
    {
        lock (Gate)
        {
            var state = LoadAndPruneUnlocked();
            var handled = new HashSet<string>(
                state.Items
                    .Where(i => IsHideOutcome(i.Outcome))
                    .Select(i => Key(i.Id, i.Version)),
                StringComparer.OrdinalIgnoreCase);
            // Also match title-keyed dismissals when OemPackageId is empty on pending rows.
            var handledTitles = new HashSet<string>(
                state.Items
                    .Where(i => IsHideOutcome(i.Outcome) && i.Id.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                    .Select(i => Key(i.Id, i.Version)),
                StringComparer.OrdinalIgnoreCase);
            return entries.Where(e =>
            {
                string key = Key(e.OemPackageId, e.UnifiedVersion);
                if (!string.IsNullOrWhiteSpace(key) && handled.Contains(key)) return false;
                string titleKey = Key("title:" + (e.Title ?? "").Trim(), e.UnifiedVersion);
                if (!string.IsNullOrWhiteSpace(titleKey) && handledTitles.Contains(titleKey)) return false;
                return true;
            }).ToList();
        }
    }

    private static bool IsHideOutcome(string? outcome) =>
        string.Equals(outcome, "Installed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(outcome, "Installert", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(outcome, "RebootHold", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(outcome, "Dismissed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(outcome, "Skjult", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Drop legacy download-only rows written by earlier builds so Check for Updates
    /// offers those packages again until a real install is confirmed or dismissed.
    /// </summary>
    private static OemHandledState LoadAndPruneUnlocked()
    {
        var state = LoadUnlocked();
        int before = state.Items.Count;
        state.Items.RemoveAll(i => !IsHideOutcome(i.Outcome));
        if (state.Items.Count != before)
            SaveUnlocked(state);
        return state;
    }

    private static string Key(string? id, string? version)
    {
        id = (id ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) return "";
        return id + "|" + NormalizeVersion(version);
    }

    private static string NormalizeVersion(string? version)
    {
        version = (version ?? "").Trim();
        if (string.IsNullOrWhiteSpace(version) || version == "—") return "";
        return version;
    }

    private static OemHandledState LoadUnlocked()
    {
        try
        {
            if (!File.Exists(FilePath)) return new OemHandledState();
            return JsonSerializer.Deserialize<OemHandledState>(File.ReadAllText(FilePath), JsonOptions)
                   ?? new OemHandledState();
        }
        catch
        {
            return new OemHandledState();
        }
    }

    private static void SaveUnlocked(OemHandledState state)
    {
        Directory.CreateDirectory(ActivityLog.DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private sealed class OemHandledState
    {
        public List<HandledItem> Items { get; set; } = new();
    }

    private sealed class HandledItem
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Title { get; set; } = "";
        public string Source { get; set; } = "";
        public string Outcome { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public DateTimeOffset At { get; set; }
    }
}
