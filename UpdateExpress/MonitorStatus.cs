using System;
using System.IO;
using System.Text.Json;
using UpdateExpress.Services;
namespace UpdateExpress;

internal sealed class MonitorStatus
{
    public string State { get; set; } = "waiting";
    public int Pending { get; set; }
    public int New { get; set; }
    public DateTime? LastCompleted { get; set; }
    public DateTime? Started { get; set; }
    public DateTime NextScheduled { get; set; }
    public string? Error { get; set; }
    public string[] Updates { get; set; } = Array.Empty<string>();
    public static MonitorStatus? Read()
    {
        try
        {
            string path = Path.Combine(InstallationManager.InstallDir, "monitor-status.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<MonitorStatus>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }
}
