using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UpdateExpress.Services;

/// <summary>
/// Cross-process snapshot of the latest WinGet upgrade list so tray ApplicationsAutomation
/// and the elevated MainWindow Applications tab stay aligned without a second manual Check.
/// </summary>
internal static class WingetAvailableCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string PathName => Path.Combine(AutomationStatePaths.Directory, "winget-available.json");

    /// <summary>Raised after a successful Write in the same process (MainWindow can refresh immediately).</summary>
    internal static event Action? Changed;

    /// <summary>
    /// Persist a successful WinGet snapshot. Atomic temp+replace; I/O failures are logged and ignored
    /// so callers (tray scan / install) are not aborted by cache contention.
    /// </summary>
    internal static bool Write(IEnumerable<AppUpdate> updates, string? raw = null)
    {
        try
        {
            var list = updates?.ToList() ?? new List<AppUpdate>();
            var dto = new CacheDto
            {
                WrittenAt = DateTimeOffset.Now,
                Raw = raw ?? "",
                Updates = list.Select(u => new CacheItem
                {
                    Name = u.Name,
                    Id = u.Id,
                    Installed = u.Installed,
                    Available = u.Available
                }).ToList()
            };

            Directory.CreateDirectory(AutomationStatePaths.Directory);
            string temp = PathName + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(dto, JsonOptions));
            File.Move(temp, PathName, overwrite: true);
            try { Changed?.Invoke(); } catch { /* listener errors must not break automation */ }
            return true;
        }
        catch (Exception ex)
        {
            AgentLog.Write("WingetAvailableCache write failed: " + ex.Message);
            return false;
        }
    }

    internal static bool TryRead(out DateTimeOffset writtenAt, out List<AppUpdate> updates, out string raw)
    {
        writtenAt = default;
        updates = new List<AppUpdate>();
        raw = "";
        try
        {
            if (!File.Exists(PathName)) return false;
            var dto = JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(PathName));
            if (dto is null) return false;
            writtenAt = dto.WrittenAt;
            raw = dto.Raw ?? "";
            updates = (dto.Updates ?? new List<CacheItem>())
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .Select(i =>
                {
                    string id = i.Id!.Trim();
                    return new AppUpdate(string.IsNullOrWhiteSpace(i.Name) ? id : i.Name.Trim(), id, i.Installed ?? "", i.Available ?? "");
                })
                .ToList();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class CacheDto
    {
        public DateTimeOffset WrittenAt { get; set; }
        public string Raw { get; set; } = "";
        public List<CacheItem> Updates { get; set; } = new();
    }

    private sealed class CacheItem
    {
        public string? Name { get; set; }
        public string? Id { get; set; }
        public string? Installed { get; set; }
        public string? Available { get; set; }
    }
}
