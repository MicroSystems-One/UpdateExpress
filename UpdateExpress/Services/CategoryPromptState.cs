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

    internal static readonly string[] AllCategories = { "critical", "general", "drivers", "applications" };

    /// <summary>
    /// True in the two states the tray shows the updates-ready dialog for: updates awaiting
    /// the user's answer, or a scheduled install whose time has passed.
    /// </summary>
    internal bool IsWaitingForUser(DateTime now)
    {
        if (Updates.Count == 0) return false;
        if (string.Equals(State, "awaitingPrompt", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(State, "scheduled", StringComparison.OrdinalIgnoreCase)
            && ScheduledInstallAt is DateTime scheduled
            && scheduled <= now;
    }

    /// <summary>
    /// Updates of every category that are waiting for the user. The Main UI reads this so the
    /// Påminnelse covers updates the tray found, whose updates-ready dialog the tray skips
    /// while the Main UI is open and a Påminnelse is due.
    /// </summary>
    internal static List<PromptUpdateItem> LoadUpdatesWaitingForUser(DateTime now) =>
        UpdatesWaitingForUser(AllCategories.Select(Load), now);

    internal static List<PromptUpdateItem> UpdatesWaitingForUser(IEnumerable<CategoryPromptState> prompts, DateTime now) =>
        prompts.Where(p => p.IsWaitingForUser(now)).SelectMany(p => p.Updates).ToList();

    /// <summary>
    /// The waiting updates the caller does not already list, each update ID once.
    /// An item without an update ID cannot be matched, so it is kept.
    /// </summary>
    internal static List<PromptUpdateItem> NotAlreadyListed(
        IEnumerable<PromptUpdateItem> waiting,
        IEnumerable<string> listedUpdateIds)
    {
        var seen = new HashSet<string>(
            listedUpdateIds.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        return waiting
            .Where(item => string.IsNullOrWhiteSpace(item.UpdateId) || seen.Add(item.UpdateId))
            .ToList();
    }

    /// <summary>
    /// Load prompt state and recover abandoned "installing" after helper crash / reboot.
    /// Applications keep an installing + remaining-queue marker so resume can relaunch
    /// without asking the user to confirm the same batch again.
    /// </summary>
    internal static CategoryPromptState LoadAndReconcile(string category)
    {
        var state = Load(category);
        if (!string.Equals(state.State, "installing", StringComparison.OrdinalIgnoreCase))
            return state;

        DateTime started = state.InstallingStartedAt ?? state.CreatedAt;
        if (DateTime.Now - started < InstallingTimeout)
            return state;

        var queue = ReadInstallQueueState(category);
        if (string.Equals(NormalizeCategory(category), "applications", StringComparison.OrdinalIgnoreCase) &&
            queue.UpdateIds.Count > 0)
        {
            // Keep installing + remaining IDs. Do NOT set ResumeAfterReboot here — that flag
            // means an OS restart is required; crash recovery may resume on the same boot.
            AgentLog.Write(
                $"{state.Category} automation: abandoned installing with {queue.UpdateIds.Count} remaining; tray will resume without re-prompt.");
            WindowsAutomationStatus.WriteCategory(
                state.Category,
                "Recovered",
                $"Previous elevated install did not finish; {queue.UpdateIds.Count} package(s) will resume.",
                DateTime.Now.AddMinutes(2));
            return state;
        }

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

    internal void WriteInstallQueue(IEnumerable<string> updateIds) =>
        WriteInstallQueueState(new InstallQueueState
        {
            Category = Category,
            CreatedAt = DateTime.Now,
            UpdateIds = updateIds.Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        });

    internal static void WriteInstallQueueState(InstallQueueState state)
    {
        string category = NormalizeCategory(
            string.IsNullOrWhiteSpace(state.Category) ? "critical" : state.Category);
        state.Category = category;
        string path = Path.Combine(StateDir, category + "-install-queue.json");
        Directory.CreateDirectory(StateDir);
        state.UpdateIds = (state.UpdateIds ?? new())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        state.CompletedIds = (state.CompletedIds ?? new())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static InstallQueueState ReadInstallQueueState(string category)
    {
        string path = Path.Combine(StateDir, NormalizeCategory(category) + "-install-queue.json");
        try
        {
            if (!File.Exists(path)) return new InstallQueueState { Category = NormalizeCategory(category) };
            var loaded = JsonSerializer.Deserialize<InstallQueueState>(File.ReadAllText(path))
                         ?? new InstallQueueState();
            loaded.Category = NormalizeCategory(category);
            loaded.UpdateIds ??= new();
            loaded.CompletedIds ??= new();
            return loaded;
        }
        catch (Exception ex)
        {
            AgentLog.Write($"Could not read {category} install queue: " + ex.Message);
            return new InstallQueueState { Category = NormalizeCategory(category) };
        }
    }

    internal static List<string> ReadInstallQueue(string category) =>
        ReadInstallQueueState(category).UpdateIds;

    /// <summary>
    /// Persist remaining packages after each item so a reboot mid-queue can resume.
    /// </summary>
    internal static void MarkInstallQueueItemDone(string category, string packageId, bool success)
    {
        var state = ReadInstallQueueState(category);
        state.UpdateIds = state.UpdateIds
            .Where(id => !id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (success &&
            !state.CompletedIds.Any(id => id.Equals(packageId, StringComparison.OrdinalIgnoreCase)))
            state.CompletedIds.Add(packageId);
        WriteInstallQueueState(state);
    }

    internal static void MarkInstallQueueResumeAfterReboot(string category, bool resume = true)
    {
        var state = ReadInstallQueueState(category);
        state.ResumeAfterReboot = resume;
        state.ResumeBlockedAtBoot = resume ? ApproximateBootTime() : null;
        WriteInstallQueueState(state);
    }

    /// <summary>Approximate Windows boot time (local clock − TickCount64).</summary>
    internal static DateTime ApproximateBootTime() =>
        DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>True when two boot stamps look like the same OS session (~3 minutes).</summary>
    internal static bool IsSameBoot(DateTime a, DateTime b) =>
        Math.Abs((a - b).TotalMinutes) < 3;

    internal static bool StartedDuringCurrentBoot(DateTime? startedAt)
    {
        if (startedAt is null) return false;
        DateTime boot = ApproximateBootTime();
        return startedAt.Value >= boot.AddMinutes(-1);
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

/// <summary>Persisted elevated install queue (remaining UpdateIds survive reboot).</summary>
internal sealed class InstallQueueState
{
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>Package / update IDs still waiting to install.</summary>
    public List<string> UpdateIds { get; set; } = new();
    /// <summary>Successfully finished in this confirmed queue (best-effort).</summary>
    public List<string> CompletedIds { get; set; } = new();
    /// <summary>Continue remaining IDs automatically after the next reboot.</summary>
    public bool ResumeAfterReboot { get; set; }
    /// <summary>Boot stamp when ResumeAfterReboot was set; resume only after a different boot.</summary>
    public DateTime? ResumeBlockedAtBoot { get; set; }
    public DateTime? LastResumeLaunch { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Category { get; set; } = "critical";
}
