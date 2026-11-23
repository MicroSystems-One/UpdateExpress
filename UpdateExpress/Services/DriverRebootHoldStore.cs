using System.IO;
using System.Text.Json;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

/// <summary>
/// Holds WUA driver UpdateIds that installed with Restart required so a second
/// install pass (e.g. Realtek) is not offered until Windows reboot indicators clear.
/// </summary>
internal static class DriverRebootHoldStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(ActivityLog.DirectoryPath, "driver-reboot-hold.json");

    internal static void Remember(UpdateEntry entry, string? note = null)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.UpdateId)) return;
        lock (Gate)
        {
            var state = LoadUnlocked();
            string id = entry.UpdateId.Trim();
            int index = state.Items.FindIndex(i =>
                string.Equals(i.UpdateId, id, StringComparison.OrdinalIgnoreCase));
            var item = new HoldItem
            {
                UpdateId = id,
                Title = entry.Title ?? "",
                HardwareId = entry.DriverHardwareId ?? "",
                Note = note ?? "Restart required",
                At = DateTimeOffset.Now
            };
            if (index < 0) state.Items.Add(item);
            else state.Items[index] = item;
            if (state.Items.Count > 100)
                state.Items = state.Items.OrderByDescending(i => i.At).Take(100).ToList();
            SaveUnlocked(state);
        }
    }

    /// <summary>
    /// Drop held UpdateIds from the available list while a reboot is still pending.
    /// When reboot indicators clear, release the holds so a genuine newer offer can return.
    /// </summary>
    internal static List<UpdateEntry> FilterAvailable(List<UpdateEntry> entries)
    {
        lock (Gate)
        {
            var state = LoadUnlocked();
            if (state.Items.Count == 0) return entries;

            if (!RebootPendingReader.IsRebootPending())
            {
                state.Items.Clear();
                SaveUnlocked(state);
                return entries;
            }

            var held = new HashSet<string>(
                state.Items.Select(i => i.UpdateId),
                StringComparer.OrdinalIgnoreCase);
            return entries
                .Where(e => string.IsNullOrWhiteSpace(e.UpdateId) || !held.Contains(e.UpdateId))
                .ToList();
        }
    }

    private static HoldState LoadUnlocked()
    {
        try
        {
            if (!File.Exists(FilePath)) return new HoldState();
            return JsonSerializer.Deserialize<HoldState>(File.ReadAllText(FilePath), JsonOptions)
                   ?? new HoldState();
        }
        catch
        {
            return new HoldState();
        }
    }

    private static void SaveUnlocked(HoldState state)
    {
        Directory.CreateDirectory(ActivityLog.DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private sealed class HoldState
    {
        public List<HoldItem> Items { get; set; } = new();
    }

    private sealed class HoldItem
    {
        public string UpdateId { get; set; } = "";
        public string Title { get; set; } = "";
        public string HardwareId { get; set; } = "";
        public string Note { get; set; } = "";
        public DateTimeOffset At { get; set; }
    }
}
