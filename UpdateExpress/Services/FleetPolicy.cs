using System;
using System.IO;
using System.Text.Json;

namespace UpdateExpress.Services;

/// <summary>
/// Optional machine-wide fleet policy overlay for RMM / shared profiles.
/// Reads %ProgramData%\MicroSystems\Update Express\fleet-policy.json and merges into the local policy.
/// </summary>
internal static class FleetPolicy
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    internal static string LastAppliedSource { get; private set; } = "";

    internal static string ProgramDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MicroSystems", "Update Express", "fleet-policy.json");

    /// <summary>
    /// Apply fleet overlay when present. Returns true when the local policy was changed and saved.
    /// </summary>
    internal static bool TryApplyOnStartup()
    {
        try
        {
            if (!File.Exists(ProgramDataPath))
            {
                LastAppliedSource = "";
                return false;
            }

            string raw = File.ReadAllText(ProgramDataPath);
            var overlay = JsonSerializer.Deserialize<FleetPolicyDocument>(raw, JsonOpts);
            if (overlay is null)
            {
                LastAppliedSource = "";
                return false;
            }

            var policy = UpdatePolicy.Load();
            policy.Normalize();
            ApplyOverlay(policy, overlay);
            policy.Normalize();
            policy.Save();
            LastAppliedSource = ProgramDataPath;
            AgentLog.Write("Fleet policy applied from " + ProgramDataPath);
            RmmStatusExport.WriteBestEffort("fleet-policy-applied", force: true);
            return true;
        }
        catch (Exception ex)
        {
            AgentLog.Write("Fleet policy apply failed: " + ex.Message);
            LastAppliedSource = "";
            return false;
        }
    }

    internal static void ApplyOverlay(UpdatePolicy policy, FleetPolicyDocument overlay)
    {
        if (!string.IsNullOrWhiteSpace(overlay.SetupProfile))
            policy.SetupProfile = overlay.SetupProfile.Trim();
        if (overlay.SetupCompleted is bool setupDone)
            policy.SetupCompleted = setupDone;
        if (overlay.InstallDaysMask is int mask && mask != 0)
            policy.InstallDaysMask = mask;
        if (!string.IsNullOrWhiteSpace(overlay.InstallTime))
            policy.InstallTime = overlay.InstallTime.Trim();
        if (overlay.AutomaticDrivers is bool drivers)
            policy.AutomaticDrivers = drivers;
        if (overlay.AutomaticApplications is bool apps)
            policy.AutomaticApplications = apps;
        if (overlay.ApplicationsIntervalMinutes is int appMin && appMin > 0)
            policy.ApplicationsIntervalMinutes = appMin;
        if (overlay.DriversIntervalMinutes is int drvMin && drvMin > 0)
            policy.DriversIntervalMinutes = drvMin;
        if (overlay.WindowsMonitorIntervalMinutes is int monMin && monMin > 0)
            policy.WindowsMonitorIntervalMinutes = monMin;

        policy.WindowsAutomation ??= WindowsAutomationPolicy.CreateDefault();
        if (overlay.WindowsAutomationEnabled is bool wa)
            policy.WindowsAutomation.Enabled = wa;

        if (overlay.AutoRebootEnabled is bool ar)
            policy.WindowsAutomation.AutoReboot.Enabled = ar;
        if (!string.IsNullOrWhiteSpace(overlay.AutoRebootMode))
            policy.WindowsAutomation.AutoReboot.Mode = overlay.AutoRebootMode.Trim();
        if (overlay.ActiveHoursStart is int start)
            policy.WindowsAutomation.AutoReboot.ActiveHoursStart = start;
        if (overlay.ActiveHoursEnd is int end)
            policy.WindowsAutomation.AutoReboot.ActiveHoursEnd = end;
        if (overlay.RebootCountdownSeconds is int countdown)
            policy.WindowsAutomation.AutoReboot.CountdownSeconds = countdown;

        if (overlay.DefenderIntervalMinutes is int defMin && defMin > 0)
            policy.WindowsAutomation.Defender.IntervalMinutes = defMin;
        if (overlay.CriticalIntervalMinutes is int critMin && critMin > 0)
            policy.WindowsAutomation.Critical.IntervalMinutes = critMin;
        if (overlay.GeneralIntervalMinutes is int genMin && genMin > 0)
            policy.WindowsAutomation.General.IntervalMinutes = genMin;
    }
}

/// <summary>Partial policy document for RMM fleet overlays (unknown fields ignored).</summary>
internal sealed class FleetPolicyDocument
{
    public string? SetupProfile { get; set; }
    public bool? SetupCompleted { get; set; }
    public int? InstallDaysMask { get; set; }
    public string? InstallTime { get; set; }
    public bool? WindowsAutomationEnabled { get; set; }
    public bool? AutomaticDrivers { get; set; }
    public bool? AutomaticApplications { get; set; }
    public int? ApplicationsIntervalMinutes { get; set; }
    public int? DriversIntervalMinutes { get; set; }
    public int? WindowsMonitorIntervalMinutes { get; set; }
    public bool? AutoRebootEnabled { get; set; }
    public string? AutoRebootMode { get; set; }
    public int? ActiveHoursStart { get; set; }
    public int? ActiveHoursEnd { get; set; }
    public int? RebootCountdownSeconds { get; set; }
    public int? DefenderIntervalMinutes { get; set; }
    public int? CriticalIntervalMinutes { get; set; }
    public int? GeneralIntervalMinutes { get; set; }
}
