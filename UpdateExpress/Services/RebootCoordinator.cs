using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace UpdateExpress.Services;

public sealed class AutoRebootPolicy
{
    /// <summary>Default off. When enabled, K2/K3 may schedule or start a Windows restart after updates that require it.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>confirm = ask after install; outsideActiveHours = reboot only outside active hours (ask if already outside).</summary>
    public string Mode { get; set; } = "confirm";
    public int ActiveHoursStart { get; set; } = 7;
    public int ActiveHoursEnd { get; set; } = 18;
    public int CountdownSeconds { get; set; } = 300;
}

internal sealed class PendingRebootState
{
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? EarliestRebootAt { get; set; }
    public string Reason { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Cancelled { get; set; }
}

/// <summary>
/// Optional post-K2/K3 reboot: confirm now, or defer until outside active hours.
/// Default policy keeps auto-reboot off.
/// </summary>
internal static class RebootCoordinator
{
    private static string PendingPath => Path.Combine(InstallationManager.InstallDir, "pending-reboot.json");

    internal static bool IsInsideActiveHours(AutoRebootPolicy policy, DateTime now)
    {
        int start = Math.Clamp(policy.ActiveHoursStart, 0, 23);
        int end = Math.Clamp(policy.ActiveHoursEnd, 0, 23);
        if (start == end) return false; // 24h outside = always allow
        int hour = now.Hour;
        if (start < end)
            return hour >= start && hour < end;
        // Overnight window e.g. 22–06 means active 22..23 and 0..5
        return hour >= start || hour < end;
    }

    internal static DateTime NextOutsideActiveHours(AutoRebootPolicy policy, DateTime from)
    {
        int start = Math.Clamp(policy.ActiveHoursStart, 0, 23);
        int end = Math.Clamp(policy.ActiveHoursEnd, 0, 23);
        if (start == end) return from;
        if (!IsInsideActiveHours(policy, from)) return from;

        // Jump to ActiveHoursEnd today (or tomorrow if overnight config).
        DateTime candidate;
        if (start < end)
            candidate = from.Date.AddHours(end);
        else
            candidate = from.Hour >= start ? from.Date.AddDays(1).AddHours(end) : from.Date.AddHours(end);

        if (candidate <= from) candidate = candidate.AddDays(1);
        return candidate;
    }

    internal static void HandleAfterInstall(string source, bool rebootRequired, System.Windows.Window? owner = null)
    {
        if (!rebootRequired) return;

        var policy = UpdatePolicy.Load();
        policy.Normalize();
        var reboot = policy.WindowsAutomation.AutoReboot;
        if (!reboot.Enabled)
        {
            AgentLog.Write($"{source}: restart may be required; auto-reboot is disabled.");
            return;
        }

        string mode = (reboot.Mode ?? "confirm").Trim();
        if (mode.Equals("outsideActiveHours", StringComparison.OrdinalIgnoreCase))
        {
            if (IsInsideActiveHours(reboot, DateTime.Now))
            {
                var when = NextOutsideActiveHours(reboot, DateTime.Now);
                SchedulePending(source, when, "Deferred until outside active hours.");
                System.Windows.MessageBox.Show(
                    owner,
                    $"A restart is required.\n\n" +
                    $"Auto-reboot is set to outside active hours ({reboot.ActiveHoursStart:00}:00–{reboot.ActiveHoursEnd:00}:00).\n" +
                    $"Update Express will offer/restart around {when:dd.MM HH:mm} (countdown {reboot.CountdownSeconds}s).\n\n" +
                    "You can cancel a pending restart from the tray agent log / by running shutdown /a if a countdown has started.",
                    "Update Express – restart scheduled",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }
            // Already outside hours → confirm before starting countdown.
            PromptConfirmAndMaybeReboot(source, reboot, owner);
            return;
        }

        // confirm (default when enabled)
        PromptConfirmAndMaybeReboot(source, reboot, owner);
    }

    private static void PromptConfirmAndMaybeReboot(string source, AutoRebootPolicy reboot, System.Windows.Window? owner)
    {
        var choice = System.Windows.MessageBox.Show(
            owner,
            $"A restart is required to finish Windows updates ({source}).\n\n" +
            $"Restart Windows now with a {reboot.CountdownSeconds}-second countdown?\n\n" +
            "Yes = Restart (countdown)\nNo = Not now",
            "Update Express – restart required",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (choice != System.Windows.MessageBoxResult.Yes)
        {
            AgentLog.Write($"{source}: user declined auto-reboot.");
            ClearPending();
            return;
        }

        StartCountdownReboot(source, reboot.CountdownSeconds,
            "Update Express: restart required after Windows updates. Save your work.");
    }

    internal static void SchedulePending(string source, DateTime earliest, string reason)
    {
        var state = new PendingRebootState
        {
            CreatedAt = DateTime.Now,
            EarliestRebootAt = earliest,
            Reason = reason,
            Source = source,
            Cancelled = false
        };
        try
        {
            Directory.CreateDirectory(InstallationManager.InstallDir);
            File.WriteAllText(PendingPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            AgentLog.Write($"Pending reboot scheduled for {earliest:O} ({source}): {reason}");
            ActivityLog.TryWrite("AutoReboot", "Scheduled", new { earliest, source, reason }, mode: "Automatic", source: "RebootCoordinator");
        }
        catch (Exception ex) { AgentLog.Write("Could not save pending reboot: " + ex.Message); }
    }

    internal static PendingRebootState? LoadPending()
    {
        try
        {
            if (!File.Exists(PendingPath)) return null;
            return JsonSerializer.Deserialize<PendingRebootState>(File.ReadAllText(PendingPath));
        }
        catch { return null; }
    }

    internal static void ClearPending()
    {
        try { if (File.Exists(PendingPath)) File.Delete(PendingPath); }
        catch (Exception ex) { AgentLog.Write("Could not clear pending reboot: " + ex.Message); }
    }

    /// <summary>
    /// Poll pending reboot file. Reminder-confirmed reboots run without a second prompt
    /// (and without requiring AutoReboot.Enabled). Other sources may prompt when
    /// <paramref name="allowUiPrompt"/> is true (tray); the Windows service passes false.
    /// </summary>
    internal static void RunDueJob(bool allowUiPrompt = true)
    {
        var pending = LoadPending();
        if (pending is null || pending.Cancelled || pending.EarliestRebootAt is null) return;
        if (DateTime.Now < pending.EarliestRebootAt.Value) return;

        var policy = UpdatePolicy.Load();
        policy.Normalize();
        var reboot = policy.WindowsAutomation.AutoReboot;
        int countdown = Math.Clamp(reboot.CountdownSeconds, 60, 3600);

        // User already confirmed the maintenance reminder — honor it from tray or service.
        if (string.Equals(pending.Source, "reminder", StringComparison.OrdinalIgnoreCase))
        {
            ClearPending();
            bool norwegian = string.Equals(ReminderSchedule.Load().Language, "no", StringComparison.OrdinalIgnoreCase);
            StartCountdownReboot(
                "reminder",
                countdown,
                norwegian
                    ? "Update Express: omstart etter bekreftet vedlikeholdspåminnelse. Lagre arbeidet."
                    : "Update Express: restart after confirmed maintenance reminder. Save your work.");
            return;
        }

        if (!reboot.Enabled)
        {
            ClearPending();
            return;
        }

        if (IsInsideActiveHours(reboot, DateTime.Now))
        {
            // Slip to next outside window if clock drifted into active hours.
            SchedulePending(pending.Source, NextOutsideActiveHours(reboot, DateTime.Now), pending.Reason);
            return;
        }

        if (!allowUiPrompt)
        {
            // Session-0 service cannot prompt; leave pending for the tray agent.
            return;
        }

        var choice = System.Windows.Forms.MessageBox.Show(
            $"A deferred restart from Update Express is due.\n\n" +
            $"Source: {pending.Source}\n{pending.Reason}\n\n" +
            $"Restart Windows now with a {countdown}-second countdown?",
            "Update Express – deferred restart",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (choice != DialogResult.Yes)
        {
            pending.Cancelled = true;
            try
            {
                File.WriteAllText(PendingPath, JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { ClearPending(); }
            AgentLog.Write("Deferred reboot declined by user.");
            return;
        }

        ClearPending();
        StartCountdownReboot(pending.Source, countdown,
            "Update Express: deferred restart after Windows updates. Save your work.");
    }

    internal static void StartCountdownReboot(string source, int countdownSeconds, string comment)
    {
        int seconds = Math.Clamp(countdownSeconds, 60, 3600);
        string safeComment = (comment ?? "Update Express restart").Replace("\"", "'");
        try
        {
            AgentLog.Write($"Starting reboot countdown ({seconds}s) for {source}.");
            ActivityLog.TryWrite("AutoReboot", "CountdownStarted", new { source, seconds }, mode: "Automatic", source: "RebootCoordinator");
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = $"/r /t {seconds} /c \"{safeComment}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            AgentLog.Write("Failed to start reboot countdown: " + ex);
            ActivityLog.TryWrite("AutoReboot", "Failed", ex.ToString(), mode: "Automatic", source: "RebootCoordinator");
            System.Windows.Forms.MessageBox.Show(
                "Could not start the Windows restart countdown.\n\n" + ex.Message +
                "\n\nRestart Windows manually when convenient.",
                "Update Express",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>Abort a pending <c>shutdown /r /t …</c> countdown if one is active (no-op otherwise).</summary>
    internal static void CancelCountdownReboot(string reason = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/a",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            AgentLog.Write("Cancelled reboot countdown." + (string.IsNullOrWhiteSpace(reason) ? "" : " " + reason));
            ActivityLog.TryWrite("AutoReboot", "CountdownCancelled", reason, mode: "Manual", source: "RebootCoordinator");
        }
        catch (Exception ex)
        {
            AgentLog.Write("Failed to cancel reboot countdown: " + ex);
        }
    }
}
