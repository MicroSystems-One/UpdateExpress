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

    private static string StatusPath => Path.Combine(InstallationManager.InstallDir, "monitor-status.json");

    public static MonitorStatus? Read()
    {
        try
        {
            string path = StatusPath;
            return File.Exists(path) ? JsonSerializer.Deserialize<MonitorStatus>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Remove Defender definition lines already covered by the installed signature version,
    /// and persist the trimmed list so Results «Vis» stops offering them.
    /// </summary>
    public static MonitorStatus? PruneInstalledDefender(Version? installedSignature)
    {
        if (installedSignature is null) return Read();
        try
        {
            var current = Read();
            if (current is null || current.Updates.Length == 0) return current;
            var filtered = PendingHistoryReconcile.FilterInstalledDefenderLines(current.Updates, installedSignature);
            if (filtered.Length == current.Updates.Length) return current;
            current.Updates = filtered;
            // Pending count from the last scan; clamp to remaining listed lines when we drop Defender.
            if (current.State == "completed")
                current.Pending = Math.Min(current.Pending, filtered.Length);
            Write(current);
            return current;
        }
        catch
        {
            return Read();
        }
    }

    internal static void Write(MonitorStatus data)
    {
        Directory.CreateDirectory(InstallationManager.InstallDir);
        string path = StatusPath;
        string temp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data));
        File.Move(temp, path, true);
    }
}
