using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace UpdateExpress.Services;

/// <summary>
/// Writes a compact status file N-able / RMM can scrape (JSON + key=value text).
/// Prefer ProgramData when writable; always mirror to the per-user install dir.
/// </summary>
internal static class RmmStatusExport
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static DateTime _lastWriteUtc = DateTime.MinValue;

    internal static string ProgramDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MicroSystems", "Update Express");

    internal static string JsonFileName => "rmm-status.json";
    internal static string TextFileName => "rmm-status.txt";

    internal static void WriteBestEffort(string? note = null, bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastWriteUtc < TimeSpan.FromSeconds(20))
            return;

        try
        {
            var payload = Build();
            if (!string.IsNullOrWhiteSpace(note))
                payload.Note = note.Trim();

            string json = JsonSerializer.Serialize(payload, JsonOpts);
            string text = ToKeyValue(payload);

            string programData = ProgramDataDir;
            TryWrite(programData, json, text, programData);
            TryWrite(InstallationManager.InstallDir, json, text, programData);
            _lastWriteUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            AgentLog.Write("RMM status export failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Create the ProgramData status files from a process running elevated or as SYSTEM. The
    /// signed-in user's agent can write them once they exist, but not create them.
    /// </summary>
    internal static void WriteProgramDataOnly(string note)
    {
        try
        {
            var payload = Build();
            payload.Note = note;
            string programData = ProgramDataDir;
            TryWrite(programData, JsonSerializer.Serialize(payload, JsonOpts), ToKeyValue(payload), programData);
        }
        catch (Exception ex)
        {
            AgentLog.Write("RMM status export failed: " + ex.Message);
        }
    }

    private static void TryWrite(string dir, string json, string text, string programData)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string jsonPath = Path.Combine(dir, JsonFileName);
            string textPath = Path.Combine(dir, TextFileName);
            if (ReferenceEquals(dir, programData))
            {
                // Users may write these two files there, but not create files beside them.
                var utf8 = new UTF8Encoding(false);
                PolicyFolderGuard.WriteUserFile(jsonPath, utf8.GetBytes(json));
                PolicyFolderGuard.WriteUserFile(textPath, utf8.GetBytes(text));
                return;
            }
            string tmpJson = jsonPath + "." + Environment.ProcessId + ".tmp";
            string tmpText = textPath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmpJson, json);
            File.WriteAllText(tmpText, text);
            File.Move(tmpJson, jsonPath, true);
            File.Move(tmpText, textPath, true);
        }
        catch
        {
            // ProgramData may be denied: another user's files in a folder that is not secured yet.
        }
    }

    private static RmmStatusPayload Build()
    {
        var policy = UpdatePolicy.Load();
        policy.Normalize();
        var auto = WindowsAutomationStatus.Load();
        var until = UpdateUntilCurrent.Load();
        var pme = PmeServiceControl.ReadState("PME.Agent.PmeService");
        var conflict = PatchConflictGuard.Evaluate(policy, pme);

        return new RmmStatusPayload
        {
            WrittenAt = DateTimeOffset.Now,
            Machine = Environment.MachineName,
            AppVersion = ProductVersion.Display,
            Elevated = ElevationHelper.IsElevated,
            SetupCompleted = policy.SetupCompleted,
            SetupProfile = policy.SetupProfile ?? "",
            WindowsAutomationEnabled = policy.WindowsAutomation.Enabled,
            AutomaticDrivers = policy.AutomaticDrivers,
            AutomaticApplications = policy.AutomaticApplications,
            ApplicationsIntervalMinutes = policy.ApplicationsIntervalMinutes,
            DriversIntervalMinutes = policy.DriversIntervalMinutes,
            WindowsMonitorIntervalMinutes = policy.WindowsMonitorIntervalMinutes,
            DefenderLastResult = auto.Defender.LastResult,
            CriticalLastResult = auto.Critical.LastResult,
            GeneralLastResult = auto.General.LastResult,
            DriversLastResult = auto.Drivers.LastResult,
            ApplicationsLastResult = auto.Applications.LastResult,
            DefenderDetail = auto.Defender.Detail,
            CriticalDetail = auto.Critical.Detail,
            GeneralDetail = auto.General.Detail,
            UpdateUntilCurrentActive = until.Active,
            UpdateUntilCurrentPhase = until.Phase,
            UpdateUntilCurrentMessage = until.Message,
            UpdateUntilCurrentRound = until.Round,
            PmeExists = pme.Exists,
            PmeState = pme.State,
            PmeStartMode = pme.StartMode,
            ConflictLevel = conflict.Level,
            ConflictSummary = conflict.Summary,
            FleetPolicyApplied = FleetPolicy.LastAppliedSource ?? ""
        };
    }

    private static string ToKeyValue(RmmStatusPayload p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ue.writtenAt=" + p.WrittenAt.ToString("o"));
        sb.AppendLine("ue.machine=" + p.Machine);
        sb.AppendLine("ue.appVersion=" + p.AppVersion);
        sb.AppendLine("ue.elevated=" + p.Elevated);
        sb.AppendLine("ue.setupCompleted=" + p.SetupCompleted);
        sb.AppendLine("ue.setupProfile=" + p.SetupProfile);
        sb.AppendLine("ue.windowsAutomation=" + p.WindowsAutomationEnabled);
        sb.AppendLine("ue.autoDrivers=" + p.AutomaticDrivers);
        sb.AppendLine("ue.autoApplications=" + p.AutomaticApplications);
        sb.AppendLine("ue.defender=" + Sanitize(p.DefenderLastResult));
        sb.AppendLine("ue.critical=" + Sanitize(p.CriticalLastResult));
        sb.AppendLine("ue.general=" + Sanitize(p.GeneralLastResult));
        sb.AppendLine("ue.drivers=" + Sanitize(p.DriversLastResult));
        sb.AppendLine("ue.applications=" + Sanitize(p.ApplicationsLastResult));
        sb.AppendLine("ue.untilCurrent.active=" + p.UpdateUntilCurrentActive);
        sb.AppendLine("ue.untilCurrent.phase=" + Sanitize(p.UpdateUntilCurrentPhase));
        sb.AppendLine("ue.untilCurrent.message=" + Sanitize(p.UpdateUntilCurrentMessage));
        sb.AppendLine("ue.untilCurrent.round=" + p.UpdateUntilCurrentRound);
        sb.AppendLine("ue.pme.exists=" + p.PmeExists);
        sb.AppendLine("ue.pme.state=" + Sanitize(p.PmeState));
        sb.AppendLine("ue.pme.start=" + Sanitize(p.PmeStartMode));
        sb.AppendLine("ue.conflict.level=" + Sanitize(p.ConflictLevel));
        sb.AppendLine("ue.conflict.summary=" + Sanitize(p.ConflictSummary));
        sb.AppendLine("ue.fleetPolicy=" + Sanitize(p.FleetPolicyApplied));
        if (!string.IsNullOrWhiteSpace(p.Note))
            sb.AppendLine("ue.note=" + Sanitize(p.Note));
        return sb.ToString();
    }

    private static string Sanitize(string? value) =>
        (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
}

internal sealed class RmmStatusPayload
{
    public DateTimeOffset WrittenAt { get; set; }
    public string Machine { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public bool Elevated { get; set; }
    public bool SetupCompleted { get; set; }
    public string SetupProfile { get; set; } = "";
    public bool WindowsAutomationEnabled { get; set; }
    public bool AutomaticDrivers { get; set; }
    public bool AutomaticApplications { get; set; }
    public int ApplicationsIntervalMinutes { get; set; }
    public int DriversIntervalMinutes { get; set; }
    public int WindowsMonitorIntervalMinutes { get; set; }
    public string DefenderLastResult { get; set; } = "";
    public string CriticalLastResult { get; set; } = "";
    public string GeneralLastResult { get; set; } = "";
    public string DriversLastResult { get; set; } = "";
    public string ApplicationsLastResult { get; set; } = "";
    public string DefenderDetail { get; set; } = "";
    public string CriticalDetail { get; set; } = "";
    public string GeneralDetail { get; set; } = "";
    public bool UpdateUntilCurrentActive { get; set; }
    public string UpdateUntilCurrentPhase { get; set; } = "";
    public string UpdateUntilCurrentMessage { get; set; } = "";
    public int UpdateUntilCurrentRound { get; set; }
    public bool PmeExists { get; set; }
    public string PmeState { get; set; } = "";
    public string PmeStartMode { get; set; } = "";
    public string ConflictLevel { get; set; } = "none";
    public string ConflictSummary { get; set; } = "";
    public string FleetPolicyApplied { get; set; } = "";
    public string Note { get; set; } = "";
}
