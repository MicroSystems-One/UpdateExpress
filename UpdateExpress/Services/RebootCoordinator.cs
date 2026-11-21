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
    /// <summary>
    /// True when the user explicitly authorized this reboot (countdown Postpone /
    /// Update-until-current). Honored even when AutoReboot policy is disabled.
    /// </summary>
    public bool UserAuthorized { get; set; }
}

/// <summary>
/// Optional post-K2/K3 reboot: confirm now, or defer until outside active hours.
/// Default policy keeps auto-reboot off.
/// </summary>
internal static class RebootCoordinator
{
    private static string PendingPath => Path.Combine(AutomationStatePaths.Directory, "pending-reboot.json");

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

    /// <returns>True when a countdown started or a deferred reboot was scheduled.</returns>
    internal static bool HandleAfterInstall(string source, bool rebootRequired, System.Windows.Window? owner = null)
    {
        if (!rebootRequired) return false;

        var policy = UpdatePolicy.Load();
        policy.Normalize();
        var reboot = policy.WindowsAutomation.AutoReboot;
        if (!reboot.Enabled)
        {
            AgentLog.Write($"{source}: restart may be required; auto-reboot is disabled.");
            return false;
        }

        string mode = (reboot.Mode ?? "confirm").Trim();
        if (mode.Equals("outsideActiveHours", StringComparison.OrdinalIgnoreCase))
        {
            if (IsInsideActiveHours(reboot, DateTime.Now))
            {
                var when = NextOutsideActiveHours(reboot, DateTime.Now);
                SchedulePending(source, when, "Deferred until outside active hours.");
                bool norwegian = AppLanguage.IsNorwegian;
                System.Windows.MessageBox.Show(
                    owner,
                    norwegian
                        ? $"Omstart er nødvendig.\n\nAutomatisk omstart er satt til utenfor arbeidstid ({reboot.ActiveHoursStart:00}:00–{reboot.ActiveHoursEnd:00}:00).\nUpdate Express tilbyr/omstarter rundt {when:dd.MM HH:mm} (nedtelling {reboot.CountdownSeconds}s)."
                        : $"A restart is required.\n\nAuto-reboot is set to outside active hours ({reboot.ActiveHoursStart:00}:00–{reboot.ActiveHoursEnd:00}:00).\nUpdate Express will offer/restart around {when:dd.MM HH:mm} (countdown {reboot.CountdownSeconds}s).",
                    norwegian ? "Update Express – omstart planlagt" : "Update Express – restart scheduled",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return true;
            }
            // Already outside hours → start countdown UI (Cancel / Restart now / Postpone).
            return PromptConfirmAndMaybeReboot(source, reboot, owner);
        }

        // confirm (default when enabled)
        return PromptConfirmAndMaybeReboot(source, reboot, owner);
    }

    private static bool PromptConfirmAndMaybeReboot(string source, AutoRebootPolicy reboot, System.Windows.Window? owner)
    {
        // Countdown modal lets any signed-in user Cancel / Restart now / Postpone 30m.
        // False when the user cancels — callers (e.g. Applications mid-queue) must recover.
        return StartCountdownReboot(
            source,
            reboot.CountdownSeconds,
            "Update Express: restart required after Windows updates. Save your work.",
            showUi: true,
            owner: owner);
    }

    internal static void SchedulePending(string source, DateTime earliest, string reason, bool userAuthorized = false)
    {
        var state = new PendingRebootState
        {
            CreatedAt = DateTime.Now,
            EarliestRebootAt = earliest,
            Reason = reason,
            Source = source,
            Cancelled = false,
            UserAuthorized = userAuthorized
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);
            File.WriteAllText(PendingPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            AgentLog.Write($"Pending reboot scheduled for {earliest:O} ({source}): {reason}");
            ActivityLog.TryWrite("AutoReboot", "Scheduled", new { earliest, source, reason, userAuthorized }, mode: "Automatic", source: "RebootCoordinator");
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

        // Do not cut a confirmed Applications queue short (same tray poll race as 2026-11-21).
        // Applications-sourced reboots are allowed through so mid-queue resume can proceed.
        if (!string.Equals(pending.Source, "Applications", StringComparison.OrdinalIgnoreCase) &&
            ApplicationAutomation.ShouldHoldExternalReboot())
        {
            var slip = DateTime.Now.AddMinutes(10);
            SchedulePending(
                pending.Source,
                slip,
                (pending.Reason ?? "") + " (waiting for application installs)");
            AgentLog.Write($"Deferred {pending.Source} reboot until {slip:O} — applications install queue active.");
            return;
        }

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
                    : "Update Express: restart after confirmed maintenance reminder. Save your work.",
                showUi: allowUiPrompt);
            return;
        }

        // Explicit user authorization (e.g. Update-until-current Postpone) survives AutoReboot=off.
        bool userAuthorized =
            pending.UserAuthorized ||
            string.Equals(pending.Source, "Update until current", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pending.Source, "Unattended until clear", StringComparison.OrdinalIgnoreCase);

        if (!reboot.Enabled && !userAuthorized)
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

        ClearPending();
        StartCountdownReboot(pending.Source, countdown,
            "Update Express: deferred restart after Windows updates. Save your work.",
            showUi: true);
    }

    /// <summary>
    /// Start a Windows restart countdown. When <paramref name="showUi"/> is true, show the
    /// centered Avbryt / Omstart nå / Utsett 30 minutter modal for any signed-in user.
    /// Returns false only when the user cancels (so mid-queue resume can recover same-boot).
    /// </summary>
    internal static bool StartCountdownReboot(
        string source,
        int countdownSeconds,
        string comment,
        bool showUi = true,
        System.Windows.Window? owner = null)
    {
        int seconds = Math.Clamp(countdownSeconds, 60, 3600);
        if (showUi)
        {
            try
            {
                return RebootCountdownWindow.RunWithUi(source, seconds, comment, owner);
            }
            catch (Exception ex)
            {
                AgentLog.Write("Reboot countdown UI failed; falling back to silent countdown: " + ex.Message);
            }
        }

        StartCountdownRebootCore(source, seconds, comment);
        return true;
    }

    /// <summary>Issue <c>shutdown /r /t …</c> without UI (used by the countdown modal and fallbacks).</summary>
    internal static void StartCountdownRebootCore(string source, int countdownSeconds, string comment)
    {
        int seconds = Math.Clamp(countdownSeconds, 0, 3600);
        if (seconds < 5) seconds = 5;
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
