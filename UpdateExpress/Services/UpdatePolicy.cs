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
    /// <summary>Legacy mirror of !AutoReboot.Enabled (kept for older readers).</summary>
    public bool NeverAutoReboot { get; set; } = true;
    public AutoRebootPolicy AutoReboot { get; set; } = new();
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

    /// <summary>When true, tray agent downloads assigned WUA drivers and asks before install (firmware stays manual).</summary>
    public bool AutomaticDrivers { get; set; } = false;
    public bool AutomaticFirmware { get; set; } = false;
    /// <summary>When true, tray agent scans WinGet upgrades and asks before install.</summary>
    public bool AutomaticApplications { get; set; } = false;
    /// <summary>Minutes between automatic WinGet application scans. Default 240; floor 60.</summary>
    public int ApplicationsIntervalMinutes { get; set; } = ApplicationAutomation.DefaultIntervalMinutes;
    /// <summary>Minutes between automatic assigned-driver scans. Default 1440 (1 day); floor 1440.</summary>
    public int DriversIntervalMinutes { get; set; } = DriverAutomation.DefaultIntervalMinutes;
    /// <summary>Minutes between tray WUA pending-update monitor scans. Default 360 (6h); floor 60.</summary>
    public int WindowsMonitorIntervalMinutes { get; set; } = 360;
    /// <summary>Optional WinGet package-ID whitelist. Empty = all pending upgrades when AutomaticApplications is on.</summary>
    public HashSet<string> AutomaticApplicationIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Legacy field from older builds; mapped into WindowsAutomation on load.</summary>
    public bool AutomaticWindows { get; set; } = true;

    /// <summary>First-run / setup wizard completed.</summary>
    public bool SetupCompleted { get; set; }
    /// <summary>private | business | both | server</summary>
    public string SetupProfile { get; set; } = "";
    /// <summary>Mon=bit0 … Sun=bit6. Shared install days for Critical/General.</summary>
    public int InstallDaysMask { get; set; }
    /// <summary>Shared HH:mm install clock for Critical/General (and reminders).</summary>
    public string InstallTime { get; set; } = "";

    private static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UpdateExpress", "update-policy.json");

    /// <summary>Machine-wide policy mirror for LocalSystem service / all-users installs.</summary>
    internal static string MachinePathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MicroSystems", "Update Express", "update-policy.json");

    public static UpdatePolicy Load()
    {
        try
        {
            string path = ResolveReadablePath();
            if (path.Length == 0) return new();
            var policy = JsonSerializer.Deserialize<UpdatePolicy>(File.ReadAllText(path)) ?? new();
            policy.Normalize();
            return policy;
        }
        catch { return new(); }
    }

    private static string ResolveReadablePath()
    {
        // Prefer the interactive user's policy; fall back to ProgramData for session-0 service.
        if (File.Exists(PathName)) return PathName;
        if (File.Exists(MachinePathName)) return MachinePathName;
        return "";
    }

    public void Save()
    {
        Normalize();
        AutomaticFirmware = false;
        AutomaticApplicationIds ??= new(StringComparer.OrdinalIgnoreCase);
        AutomaticWindows = WindowsAutomation.Enabled;
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllText(PathName, json);
        TrySaveMachineMirror(json);
    }

    /// <summary>Write ProgramData mirror (best-effort; requires rights for machine/service installs).</summary>
    internal void SaveMachineMirror()
    {
        Normalize();
        AutomaticFirmware = false;
        AutomaticApplicationIds ??= new(StringComparer.OrdinalIgnoreCase);
        AutomaticWindows = WindowsAutomation.Enabled;
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        TrySaveMachineMirror(json);
    }

    private static void TrySaveMachineMirror(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MachinePathName)!);
            // Over the existing file: users may write it but not create files in that folder.
            PolicyFolderGuard.WriteUserFile(MachinePathName, new System.Text.UTF8Encoding(false).GetBytes(json));
        }
        catch
        {
            // Not writable for this user (another user's file in a folder that is not secured yet) — OK.
        }
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
        WindowsAutomation.AutoReboot ??= new AutoRebootPolicy();
        if (!string.Equals(WindowsAutomation.AutoReboot.Mode, "confirm", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(WindowsAutomation.AutoReboot.Mode, "outsideActiveHours", StringComparison.OrdinalIgnoreCase))
            WindowsAutomation.AutoReboot.Mode = "confirm";
        WindowsAutomation.AutoReboot.ActiveHoursStart = Math.Clamp(WindowsAutomation.AutoReboot.ActiveHoursStart, 0, 23);
        WindowsAutomation.AutoReboot.ActiveHoursEnd = Math.Clamp(WindowsAutomation.AutoReboot.ActiveHoursEnd, 0, 23);
        WindowsAutomation.AutoReboot.CountdownSeconds = Math.Clamp(WindowsAutomation.AutoReboot.CountdownSeconds, 60, 3600);
        // Keep legacy flag in sync (true when optional auto-reboot is off).
        WindowsAutomation.NeverAutoReboot = !WindowsAutomation.AutoReboot.Enabled;

        if (string.IsNullOrWhiteSpace(WindowsAutomation.Critical.PreferredInstallTime) ||
            WindowsAutomation.Critical.PreferredInstallTime.Trim() == "20:00")
            WindowsAutomation.Critical.PreferredInstallTime = AutomationSchedule.DefaultPreferredTime;
        if (string.IsNullOrWhiteSpace(WindowsAutomation.General.PreferredInstallTime) ||
            WindowsAutomation.General.PreferredInstallTime.Trim() == "20:00")
            WindowsAutomation.General.PreferredInstallTime = AutomationSchedule.DefaultPreferredTime;

        // Shared maintenance window (wizard / simplified Pilot).
        if (InstallDaysMask == 0)
        {
            int wd = WindowsAutomation.General.Weekday;
            InstallDaysMask = wd is >= 0 and <= 6 ? (1 << wd) : MaintenanceSetup.AllDaysMask;
        }
        InstallDaysMask &= MaintenanceSetup.AllDaysMask;
        if (string.IsNullOrWhiteSpace(InstallTime))
            InstallTime = WindowsAutomation.Critical.PreferredInstallTime;
        InstallTime = MaintenanceSetup.NormalizeTime(InstallTime, AutomationSchedule.DefaultPreferredTime);
        if (SetupCompleted)
        {
            WindowsAutomation.Critical.PreferredInstallTime = InstallTime;
            WindowsAutomation.General.PreferredInstallTime = InstallTime;
            WindowsAutomation.General.Weekday = InstallDaysMask == MaintenanceSetup.AllDaysMask
                ? 7
                : MaintenanceSetup.FirstSelectedWeekday(InstallDaysMask);
        }

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
        if (ApplicationsIntervalMinutes < ApplicationAutomation.AbsoluteFloorMinutes)
            ApplicationsIntervalMinutes = ApplicationAutomation.AbsoluteFloorMinutes;
        if (ApplicationsIntervalMinutes <= 0)
            ApplicationsIntervalMinutes = ApplicationAutomation.DefaultIntervalMinutes;

        if (DriversIntervalMinutes < DriverAutomation.AbsoluteFloorMinutes)
            DriversIntervalMinutes = DriverAutomation.AbsoluteFloorMinutes;
        if (DriversIntervalMinutes <= 0)
            DriversIntervalMinutes = DriverAutomation.DefaultIntervalMinutes;

        if (WindowsMonitorIntervalMinutes < 60)
            WindowsMonitorIntervalMinutes = 60;
        if (WindowsMonitorIntervalMinutes <= 0)
            WindowsMonitorIntervalMinutes = 360;

        AutomaticApplicationIds ??= new(StringComparer.OrdinalIgnoreCase);
    }
}
