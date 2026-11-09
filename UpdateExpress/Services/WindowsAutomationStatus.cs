using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateExpress.Services;

internal sealed class CategoryAutomationStatus
{
    public DateTime? LastRun { get; set; }
    public DateTime? NextDue { get; set; }
    public string LastResult { get; set; } = "Never run";
    public string Detail { get; set; } = "";
}

internal sealed class WindowsAutomationStatus
{
    public CategoryAutomationStatus Defender { get; set; } = new();
    public CategoryAutomationStatus Critical { get; set; } = new();
    public CategoryAutomationStatus General { get; set; } = new();

    private static string PathName => Path.Combine(InstallationManager.InstallDir, "windows-automation-status.json");

    internal static WindowsAutomationStatus Load()
    {
        try
        {
            if (!File.Exists(PathName)) return new();
            return JsonSerializer.Deserialize<WindowsAutomationStatus>(File.ReadAllText(PathName)) ?? new();
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
        catch (Exception ex) { AgentLog.Write("Windows automation status save failed: " + ex.Message); }
    }

    internal static void WriteCategory(string category, string result, string detail, DateTime? nextDue)
    {
        var status = Load();
        var entry = category.ToLowerInvariant() switch
        {
            "defender" => status.Defender,
            "critical" => status.Critical,
            "general" => status.General,
            _ => status.Defender
        };
        entry.LastRun = DateTime.Now;
        entry.LastResult = result;
        entry.Detail = detail;
        entry.NextDue = nextDue;
        status.Save();
    }
}
