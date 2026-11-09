using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace UpdateExpress.Services;

public sealed class CriticalPromptUpdate
{
    public string UpdateId { get; set; } = "";
    public string KB { get; set; } = "";
    public string Title { get; set; } = "";
    public string Priority { get; set; } = "";
}

internal sealed class CriticalPromptState
{
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public bool Downloaded { get; set; }
    public List<CriticalPromptUpdate> Updates { get; set; } = new();
    public DateTime? SnoozeUntil { get; set; }
    public DateTime? ScheduledInstallAt { get; set; }
    /// <summary>awaitingPrompt | snoozed | scheduled | installing | cleared</summary>
    public string State { get; set; } = "cleared";

    private static string PathName => Path.Combine(InstallationManager.InstallDir, "critical-prompt.json");
    private static string InstallQueuePath => Path.Combine(InstallationManager.InstallDir, "critical-install-queue.json");

    internal static CriticalPromptState Load()
    {
        try
        {
            if (!File.Exists(PathName)) return new();
            return JsonSerializer.Deserialize<CriticalPromptState>(File.ReadAllText(PathName)) ?? new();
        }
        catch { return new(); }
    }

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(InstallationManager.InstallDir);
            string temp = PathName + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, PathName, true);
        }
        catch (Exception ex) { AgentLog.Write("Critical prompt state save failed: " + ex.Message); }
    }

    internal static void Clear()
    {
        var empty = new CriticalPromptState { State = "cleared", Updates = new() };
        empty.Save();
    }

    internal static void WriteInstallQueue(IEnumerable<string> updateIds)
    {
        Directory.CreateDirectory(InstallationManager.InstallDir);
        var payload = new { CreatedAt = DateTime.Now, UpdateIds = updateIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
        File.WriteAllText(InstallQueuePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static List<string> ReadInstallQueue()
    {
        try
        {
            if (!File.Exists(InstallQueuePath)) return new();
            using var doc = JsonDocument.Parse(File.ReadAllText(InstallQueuePath));
            if (!doc.RootElement.TryGetProperty("UpdateIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                return new();
            return ids.EnumerateArray()
                .Select(x => x.GetString() ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            AgentLog.Write("Could not read critical install queue: " + ex.Message);
            return new();
        }
    }

    internal static void ClearInstallQueue()
    {
        try { if (File.Exists(InstallQueuePath)) File.Delete(InstallQueuePath); }
        catch (Exception ex) { AgentLog.Write("Could not clear critical install queue: " + ex.Message); }
    }
}
