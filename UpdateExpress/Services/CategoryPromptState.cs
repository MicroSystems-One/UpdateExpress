using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UpdateExpress.Services;

public sealed class PromptUpdateItem
{
    public string UpdateId { get; set; } = "";
    public string KB { get; set; } = "";
    public string Title { get; set; } = "";
    public string Priority { get; set; } = "";
}

/// <summary>Persisted download+prompt queue for a Windows automation category (critical/general).</summary>
internal sealed class CategoryPromptState
{
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public bool Downloaded { get; set; }
    public List<PromptUpdateItem> Updates { get; set; } = new();
    public DateTime? SnoozeUntil { get; set; }
    public DateTime? ScheduledInstallAt { get; set; }
    /// <summary>awaitingPrompt | snoozed | scheduled | installing | cleared</summary>
    public string State { get; set; } = "cleared";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Category { get; set; } = "critical";

    private string PromptPath => Path.Combine(InstallationManager.InstallDir, Category + "-prompt.json");
    private string QueuePath => Path.Combine(InstallationManager.InstallDir, Category + "-install-queue.json");

    internal static CategoryPromptState Load(string category)
    {
        var state = new CategoryPromptState { Category = NormalizeCategory(category) };
        try
        {
            if (!File.Exists(state.PromptPath)) return state;
            var loaded = JsonSerializer.Deserialize<CategoryPromptState>(File.ReadAllText(state.PromptPath)) ?? new();
            loaded.Category = state.Category;
            return loaded;
        }
        catch { return state; }
    }

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(InstallationManager.InstallDir);
            string temp = PromptPath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, PromptPath, true);
        }
        catch (Exception ex) { AgentLog.Write($"{Category} prompt state save failed: " + ex.Message); }
    }

    internal static void Clear(string category)
    {
        var empty = new CategoryPromptState { Category = NormalizeCategory(category), State = "cleared", Updates = new() };
        empty.Save();
    }

    internal void WriteInstallQueue(IEnumerable<string> updateIds)
    {
        Directory.CreateDirectory(InstallationManager.InstallDir);
        var payload = new
        {
            CreatedAt = DateTime.Now,
            UpdateIds = updateIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        File.WriteAllText(QueuePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static List<string> ReadInstallQueue(string category)
    {
        string path = Path.Combine(InstallationManager.InstallDir, NormalizeCategory(category) + "-install-queue.json");
        try
        {
            if (!File.Exists(path)) return new();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
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
            AgentLog.Write($"Could not read {category} install queue: " + ex.Message);
            return new();
        }
    }

    internal static void ClearInstallQueue(string category)
    {
        string path = Path.Combine(InstallationManager.InstallDir, NormalizeCategory(category) + "-install-queue.json");
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AgentLog.Write($"Could not clear {category} install queue: " + ex.Message); }
    }

    private static string NormalizeCategory(string category) =>
        string.Equals(category, "general", StringComparison.OrdinalIgnoreCase) ? "general" : "critical";
}
