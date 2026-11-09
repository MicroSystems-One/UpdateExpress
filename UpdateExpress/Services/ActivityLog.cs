using System;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace UpdateExpress.Services;

internal static class ActivityLog
{
    // Per-user writable location, never Program Files. All-users installations
    // have distinct per-user logs until a privileged shared log is implemented.
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicroSystems", "Update Express", "Logs");
    internal static string FilePath => Path.Combine(DirectoryPath, "activity.jsonl");
    internal static string UserName
    {
        get
        {
            try { return WindowsIdentity.GetCurrent().Name; }
            catch { return Environment.UserDomainName + "\\" + Environment.UserName; }
        }
    }
    internal static void Write(string action, string status, object? detail = null, string mode = "Manual", string source = "Update Express", string? runId = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        var record = new
        {
            Timestamp = DateTimeOffset.Now,
            Computer = Environment.MachineName,
            User = UserName,
            ExecutedAs = UserName,
            Mode = mode,
            Source = source,
            Action = action,
            Status = status,
            RunId = runId,
            Detail = detail
        };
        File.AppendAllText(FilePath, JsonSerializer.Serialize(record) + Environment.NewLine, new UTF8Encoding(false));
    }
    internal static void TryWrite(string action, string status, object? detail = null, string mode = "Manual", string source = "Update Express", string? runId = null)
    {
        try { Write(action, status, detail, mode, source, runId); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Activity logging failed: " + ex.Message); }
    }
}
