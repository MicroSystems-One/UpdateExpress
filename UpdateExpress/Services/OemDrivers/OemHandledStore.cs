using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UpdateExpress.Models;

namespace UpdateExpress.Services.OemDrivers;

/// <summary>
/// Persists OEM packages the user already downloaded/installed through Update Express
/// so the next catalog search does not keep offering the same Id+Version.
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
            return LoadUnlocked().Items.Any(i =>
                string.Equals(Key(i.Id, i.Version), key, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal static void Remember(
        UpdateEntry entry,
        string outcome,
        string? localPath = null)
    {
        string id = entry.OemPackageId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(id))
            id = "url:" + (entry.ExternalUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id) || id == "url:") return;

        lock (Gate)
        {
            var state = LoadUnlocked();
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
                state.Items = state.Items.OrderByDescending(i => i.At).Take(500).ToList();
            SaveUnlocked(state);
        }
    }

    internal static IReadOnlyList<UpdateEntry> FilterUnhandled(IEnumerable<UpdateEntry> entries)
    {
        lock (Gate)
        {
            var handled = new HashSet<string>(
                LoadUnlocked().Items.Select(i => Key(i.Id, i.Version)),
                StringComparer.OrdinalIgnoreCase);
            return entries.Where(e =>
            {
                string key = Key(e.OemPackageId, e.UnifiedVersion);
                return string.IsNullOrWhiteSpace(key) || !handled.Contains(key);
            }).ToList();
        }
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
