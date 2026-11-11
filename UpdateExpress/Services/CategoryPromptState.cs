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
    /// <summary>WUA driver hardware ID when Category is drivers; unused otherwise.</summary>
    public string HardwareId { get; set; } = "";
}

/// <summary>Persisted download+prompt queue for an automation category (critical/general/drivers/applications).</summary>
internal sealed class CategoryPromptState
{
    /// <summary>Abandoned elevated helpers older than this are restored to awaitingPrompt.</summary>
    internal static readonly TimeSpan InstallingTimeout = TimeSpan.FromMinutes(90);

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public bool Downloaded { get; set; }
    public List<PromptUpdateItem> Updates { get; set; } = new();
    public DateTime? SnoozeUntil { get; set; }
    public DateTime? ScheduledInstallAt { get; set; }
    /// <summary>When State became installing; used to recover after a crashed/restarted helper.</summary>
    public DateTime? InstallingStartedAt { get; set; }
    /// <summary>awaitingPrompt | snoozed | scheduled | installing | cleared</summary>
    public string State { get; set; } = "cleared";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Category { get; set; } = "critical";

    private static string StateDir => AutomationStatePaths.Directory;
    private string PromptPath => Path.Combine(StateDir, Category + "-prompt.json");
    private string QueuePath => Path.Combine(StateDir, Category + "-install-queue.json");

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

    /// <summary>
    /// Load prompt state and recover abandoned "installing" after helper crash / reboot.
    /// </summary>
    internal static CategoryPromptState LoadAndReconcile(string category)
    {
        var state = Load(category);
        if (!string.Equals(state.State, "installing", StringComparison.OrdinalIgnoreCase))
            return state;

        DateTime started = state.InstallingStartedAt ?? state.CreatedAt;
        if (DateTime.Now - started < InstallingTimeout)
            return state;

        AgentLog.Write(
            $"{state.Category} automation: abandoned installing state older than {InstallingTimeout.TotalMinutes:0}m; restoring confirmable state.");
        state.State = state.Updates.Count > 0 ? "awaitingPrompt" : "cleared";
        state.InstallingStartedAt = null;
        state.ScheduledInstallAt = null;
        state.Save();
        WindowsAutomationStatus.WriteCategory(
            state.Category,
            "Recovered",
            "Previous elevated install did not finish; confirmation restored.",
            DateTime.Now.AddMinutes(5));
        return state;
    }

    internal void MarkInstalling()
    {
        State = "installing";
        InstallingStartedAt = DateTime.Now;
    }

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(StateDir);
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
        Directory.CreateDirectory(StateDir);
        var payload = new
        {
            CreatedAt = DateTime.Now,
            UpdateIds = updateIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        File.WriteAllText(QueuePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static List<string> ReadInstallQueue(string category)
    {
        string path = Path.Combine(StateDir, NormalizeCategory(category) + "-install-queue.json");
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
        string path = Path.Combine(StateDir, NormalizeCategory(category) + "-install-queue.json");
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AgentLog.Write($"Could not clear {category} install queue: " + ex.Message); }
    }

    private static string NormalizeCategory(string category)
    {
        if (string.Equals(category, "general", StringComparison.OrdinalIgnoreCase)) return "general";
        if (string.Equals(category, "drivers", StringComparison.OrdinalIgnoreCase)) return "drivers";
        if (string.Equals(category, "applications", StringComparison.OrdinalIgnoreCase)) return "applications";
        return "critical";
    }
}
