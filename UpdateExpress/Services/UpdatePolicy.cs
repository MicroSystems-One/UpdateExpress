using System.Text.Json;
using System.IO;
namespace UpdateExpress.Services;
public sealed class UpdatePolicy
{
    public bool AutomaticWindows { get; set; } = true;
    public bool AutomaticDrivers { get; set; } = false;
    public bool AutomaticFirmware { get; set; } = false;
    public bool AutomaticApplications { get; set; } = false;
    public HashSet<string> AutomaticApplicationIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateExpress", "update-policy.json");
    public static UpdatePolicy Load()
    {
        try { return File.Exists(PathName) ? JsonSerializer.Deserialize<UpdatePolicy>(File.ReadAllText(PathName)) ?? new() : new(); }
        catch { return new(); }
    }
    public void Save()
    {
        AutomaticFirmware = false;
        AutomaticApplicationIds ??= new(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
