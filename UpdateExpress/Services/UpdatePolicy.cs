using System.Text.Json;
using System.IO;

namespace UpdateExpress.Services;

public sealed class CategoryAutomationPolicy
{
    public bool Enabled { get; set; } = true;
    /// <summary>Minutes between checks. Defender default 60, critical 1440, general 10080.</summary>
    public int IntervalMinutes { get; set; } = 60;
    /// <summary>off | checkOnly | downloadAndPrompt | autoInstall</summary>
    public string Mode { get; set; } = "checkOnly";
    public string PreferredInstallTime { get; set; } = AutomationSchedule.DefaultPreferredTime;
    /// <summary>0=Monday … 6=Sunday, 7=every day. Used by weekly general updates.</summary>
    public int Weekday { get; set; } = 2;
}

public sealed class WindowsAutomationPolicy
{
    public bool Enabled { get; set; } = true;
    public bool NeverAutoReboot { get; set; } = true;
    public CategoryAutomationPolicy Defender { get; set; } = new()
    {
        Enabled = true,
        IntervalMinutes = 60,
        Mode = "autoInstall"
    };
    public CategoryAutomationPolicy Critical { get; set; } = new()
    {
        Enabled = true,
        IntervalMinutes = 1440,
        Mode = "downloadAndPrompt",
        PreferredInstallTime = AutomationSchedule.DefaultPreferredTime
    };
    public CategoryAutomationPolicy General { get; set; } = new()
    {
        Enabled = true,
        IntervalMinutes = 10080,
        Mode = "downloadAndPrompt",
        PreferredInstallTime = AutomationSchedule.DefaultPreferredTime,
        Weekday = 2
    };

    public static WindowsAutomationPolicy CreateDefault() => new();
}

public sealed class UpdatePolicy
{
    public WindowsAutomationPolicy WindowsAutomation { get; set; } = WindowsAutomationPolicy.CreateDefault();

    // Legacy / not yet automated preferences (drivers & WinGet).
    public bool AutomaticDrivers { get; set; } = false;
    public bool AutomaticFirmware { get; set; } = false;
    public bool AutomaticApplications { get; set; } = false;
    public HashSet<string> AutomaticApplicationIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Legacy field from older builds; mapped into WindowsAutomation on load.</summary>
    public bool AutomaticWindows { get; set; } = true;

    private static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UpdateExpress", "update-policy.json");

    public static UpdatePolicy Load()
    {
        try
        {
            if (!File.Exists(PathName)) return new();
            var policy = JsonSerializer.Deserialize<UpdatePolicy>(File.ReadAllText(PathName)) ?? new();
            policy.Normalize();
            return policy;
        }
        catch { return new(); }
    }

    public void Save()
    {
        Normalize();
        AutomaticFirmware = false;
        AutomaticApplicationIds ??= new(StringComparer.OrdinalIgnoreCase);
        AutomaticWindows = WindowsAutomation.Enabled;
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal void Normalize()
    {
        WindowsAutomation ??= WindowsAutomationPolicy.CreateDefault();
        WindowsAutomation.Defender ??= new CategoryAutomationPolicy { Enabled = true, IntervalMinutes = 60, Mode = "autoInstall" };
        WindowsAutomation.Critical ??= new CategoryAutomationPolicy
        {
            Enabled = true,
            IntervalMinutes = 1440,
            Mode = "downloadAndPrompt",
            PreferredInstallTime = AutomationSchedule.DefaultPreferredTime
        };
        WindowsAutomation.General ??= new CategoryAutomationPolicy
        {
            Enabled = true,
            IntervalMinutes = 10080,
            Mode = "downloadAndPrompt",
            PreferredInstallTime = AutomationSchedule.DefaultPreferredTime,
            Weekday = 2
        };
        WindowsAutomation.NeverAutoReboot = true;

        if (string.IsNullOrWhiteSpace(WindowsAutomation.Critical.PreferredInstallTime) ||
            WindowsAutomation.Critical.PreferredInstallTime.Trim() == "20:00")
            WindowsAutomation.Critical.PreferredInstallTime = AutomationSchedule.DefaultPreferredTime;
        if (string.IsNullOrWhiteSpace(WindowsAutomation.General.PreferredInstallTime) ||
            WindowsAutomation.General.PreferredInstallTime.Trim() == "20:00")
            WindowsAutomation.General.PreferredInstallTime = AutomationSchedule.DefaultPreferredTime;

        // Hard rules for v1 (B+C): only Defender may auto-install.
        if (!string.Equals(WindowsAutomation.Defender.Mode, "autoInstall", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(WindowsAutomation.Defender.Mode, "off", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(WindowsAutomation.Defender.Mode, "checkOnly", StringComparison.OrdinalIgnoreCase))
            WindowsAutomation.Defender.Mode = "autoInstall";

        WindowsAutomation.Critical.Mode = "downloadAndPrompt";
        WindowsAutomation.General.Mode = "downloadAndPrompt";

        if (WindowsAutomation.Defender.IntervalMinutes < 15) WindowsAutomation.Defender.IntervalMinutes = 15;
        if (WindowsAutomation.Critical.IntervalMinutes < 60) WindowsAutomation.Critical.IntervalMinutes = 60;
        if (WindowsAutomation.General.IntervalMinutes < 1440) WindowsAutomation.General.IntervalMinutes = 1440;

        AutomaticApplicationIds ??= new(StringComparer.OrdinalIgnoreCase);
    }
}
