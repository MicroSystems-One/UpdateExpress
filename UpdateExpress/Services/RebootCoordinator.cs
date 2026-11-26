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
    /// Fixed restart time, set while the countdown notice is live or postponed. The notice
    /// opens at <see cref="EarliestRebootAt"/> and counts down to this. Null = start a full
    /// countdown at <see cref="EarliestRebootAt"/>.
    /// </summary>
    public DateTime? RestartAt { get; set; }
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
            // Already outside hours → start the countdown notice (Stop / Postpone).
            return PromptConfirmAndMaybeReboot(source, reboot, owner);
        }

        // confirm (default when enabled)
        return PromptConfirmAndMaybeReboot(source, reboot, owner);
    }

    private static bool PromptConfirmAndMaybeReboot(string source, AutoRebootPolicy reboot, System.Windows.Window? owner)
    {
        // Countdown notice lets the signed-in user Stop or Postpone 15 minutes.
        // False after Stop: no restart is armed, and the caller must not wait for one.
        return StartCountdownReboot(
            source,
            reboot.CountdownSeconds,
            "Update Express: restart required after Windows updates. Save your work.",
            showUi: true,
            owner: owner);
    }

    internal static void SchedulePending(
        string source,
        DateTime earliest,
        string reason,
        DateTime? restartAt = null,
        bool log = true)
    {
        var state = new PendingRebootState
        {
            CreatedAt = DateTime.Now,
            EarliestRebootAt = earliest,
            Reason = reason,
            Source = source,
            Cancelled = false,
            RestartAt = restartAt
        };
        try
        {
            PreferMachineWideState();
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);
            File.WriteAllText(PendingPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            if (!log) return;
            AgentLog.Write($"Pending reboot scheduled for {earliest:O} ({source}): {reason}");
            ActivityLog.TryWrite("AutoReboot", "Scheduled", new { earliest, restartAt, source, reason }, mode: "Automatic", source: "RebootCoordinator");
        }
        catch (Exception ex) { AgentLog.Write("Could not save pending reboot: " + ex.Message); }
    }

    /// <summary>
    /// A restart request has to outlive the session that made it: once the user signs out only
    /// the session-0 service is left, and it cannot read a file in that user's profile. While
    /// this process still keeps its state per user, move it to the ProgramData directory, which
    /// every session and the service resolve to as soon as it holds the request. When this user
    /// cannot write there, <see cref="AutomationStatePaths.EnsureSharedDirectoryForElevate"/>
    /// changes nothing: the request stays per user and the tray agent runs the restart.
    /// </summary>
    private static void PreferMachineWideState()
    {
        try
        {
            if (!SamePath(AutomationStatePaths.Directory, InstallationManager.InstallDir)) return;
            AutomationStatePaths.EnsureSharedDirectoryForElevate();
        }
        catch (Exception ex)
        {
            AgentLog.Write("Pending reboot stays in the per-user state directory: " + ex.Message);
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A restart the user has already been told about: a confirmed maintenance reminder, or a
    /// countdown notice that is live or postponed. These run without the auto-reboot policy gates.
    /// </summary>
    internal static bool IsCommitted(PendingRebootState pending) =>
        pending.RestartAt is not null ||
        string.Equals(pending.Source, "reminder", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPendingDue() =>
        LoadPending() is { Cancelled: false, EarliestRebootAt: DateTime due } && DateTime.Now >= due;

    private static bool IsSamePending(PendingRebootState expected) =>
        LoadPending() is { Cancelled: false } current &&
        current.EarliestRebootAt == expected.EarliestRebootAt &&
        string.Equals(current.Source, expected.Source, StringComparison.OrdinalIgnoreCase);

    /// <summary>Drop the pending file only if it is still the live notice's own entry.</summary>
    internal static void ClearPendingIfRestartAt(DateTime restartAt)
    {
        if (LoadPending()?.RestartAt is DateTime current &&
            Math.Abs((current - restartAt).TotalSeconds) < 1)
            ClearPending();
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
    /// How long the session-0 service leaves a due, committed restart alone so the signed-in
    /// user's agent can show the Update Express notice. The service cannot show UI itself;
    /// after this it falls back to <c>shutdown /r /t</c>, which shows only the Windows notice.
    /// </summary>
    internal static readonly TimeSpan ServiceGrace = TimeSpan.FromSeconds(60);

    private static DateTime? _graceDue;
    private static string? _graceSource;
    private static DateTime _graceStartedUtc;

    /// <summary>
    /// True while the service should still leave this due restart to a user session. The grace
    /// counts from when this process first saw the entry as due, not from the due time: after
    /// sleep or a late service start the due time is long past, and the agent, waking at the
    /// same moment, needs the same head start.
    /// </summary>
    private static bool InServiceGrace(PendingRebootState pending, DateTime due)
    {
        if (_graceDue != due || !string.Equals(_graceSource, pending.Source, StringComparison.OrdinalIgnoreCase))
        {
            _graceDue = due;
            _graceSource = pending.Source;
            _graceStartedUtc = DateTime.UtcNow;
        }
        return DateTime.UtcNow - _graceStartedUtc < ServiceGrace;
    }

    /// <summary>
    /// Poll pending reboot file. Committed restarts (see <see cref="IsCommitted"/>) run without
    /// requiring AutoReboot.Enabled. With <paramref name="allowUiPrompt"/> (tray agent, main
    /// window) the countdown notice is shown; the Windows service passes false.
    /// </summary>
    internal static void RunDueJob(bool allowUiPrompt = true)
    {
        var pending = LoadPending();
        if (pending is null || pending.Cancelled || pending.EarliestRebootAt is null) return;
        DateTime due = pending.EarliestRebootAt.Value;
        if (DateTime.Now < due) return;

        // A notice-owned restart from before this boot is already satisfied by that boot.
        if (pending.RestartAt is not null && pending.CreatedAt < CategoryPromptState.ApproximateBootTime())
        {
            AgentLog.Write($"Dropped pending {pending.Source} restart from {pending.CreatedAt:O}: the PC has restarted since.");
            ClearPending();
            return;
        }

        var policy = UpdatePolicy.Load();
        policy.Normalize();
        var reboot = policy.WindowsAutomation.AutoReboot;
        int seconds = pending.RestartAt is DateTime restartAt
            ? Math.Clamp((int)Math.Ceiling((restartAt - DateTime.Now).TotalSeconds), 60, 3600)
            : Math.Clamp(reboot.CountdownSeconds, 60, 3600);

        // Do not cut a confirmed Applications queue short (same tray poll race as 2026-11-21).
        // Applications-sourced reboots are allowed through so mid-queue resume can proceed.
        if (!string.Equals(pending.Source, "Applications", StringComparison.OrdinalIgnoreCase) &&
            ApplicationAutomation.ShouldHoldExternalReboot())
        {
            var slip = DateTime.Now.AddMinutes(10);
            SchedulePending(
                pending.Source,
                slip,
                (pending.Reason ?? "") + " (waiting for application installs)",
                pending.RestartAt is null ? null : slip.AddSeconds(seconds));
            AgentLog.Write($"Deferred {pending.Source} reboot until {slip:O} — applications install queue active.");
            return;
        }

        string comment;
        if (IsCommitted(pending))
        {
            // Session 0 cannot show the notice: give the signed-in user's agent first go.
            if (!allowUiPrompt && InServiceGrace(pending, due)) return;

            bool norwegian = string.Equals(ReminderSchedule.Load().Language, "no", StringComparison.OrdinalIgnoreCase);
            comment = !string.Equals(pending.Source, "reminder", StringComparison.OrdinalIgnoreCase)
                ? "Update Express: restart after updates. Save your work."
                : norwegian
                    ? "Update Express: omstart etter bekreftet vedlikeholdspåminnelse. Lagre arbeidet."
                    : "Update Express: restart after confirmed maintenance reminder. Save your work.";
        }
        else
        {
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

            comment = "Update Express: deferred restart after Windows updates. Save your work.";
        }

        if (allowUiPrompt)
        {
            // The notice replaces the pending entry with its own once it owns the session slot,
            // so a second poller (tray + main window) cannot open a second notice.
            StartCountdownReboot(pending.Source, seconds, comment, showUi: true,
                stillWanted: () => IsSamePending(pending));
            return;
        }

        ClearPending();
        StartCountdownReboot(pending.Source, seconds, comment, showUi: false);
    }

    /// <summary>
    /// Count down to a restart. With <paramref name="showUi"/> the Update Express notice runs
    /// the countdown and restarts at 0, so Windows shows no sign-out notice of its own. Without
    /// it (session 0, or the notice failed) Windows runs the countdown and shows its own notice.
    /// <paramref name="owner"/> is unused: the notice is ownerless so it still shows when the
    /// main window is minimised. Returns false only when the user pressed Stop in the notice:
    /// the restart is off, so a caller that parked work until after it has to pick it up again.
    /// </summary>
    internal static bool StartCountdownReboot(
        string source,
        int countdownSeconds,
        string comment,
        bool showUi = true,
        System.Windows.Window? owner = null,
        Func<bool>? stillWanted = null)
    {
        int seconds = Math.Clamp(countdownSeconds, 60, 3600);
        if (showUi)
        {
            try
            {
                RebootCountdownWindow.RunWithUi(source, seconds, comment, out var decision, stillWanted);
                return RebootCountdownWindow.KeepsRestartArmed(decision);
            }
            catch (Exception ex)
            {
                AgentLog.Write("Reboot countdown notice failed; falling back to the Windows countdown: " + ex.Message);
                ClearPending();
            }
        }

        StartCountdownRebootCore(source, seconds, comment);
        return true;
    }

    /// <summary>
    /// Restart now: the notice's countdown ran out, or the user pressed Restart now. <c>/t 0</c>
    /// shows no Windows sign-out notice; <c>/f</c> keeps what <c>shutdown /r /t N</c> (N &gt; 0)
    /// implied before.
    /// </summary>
    internal static void RestartNow(string source, string comment, bool userChoice = false)
    {
        string safeComment = (comment ?? "Update Express restart").Replace("\"", "'");
        try
        {
            AgentLog.Write(userChoice
                ? $"User chose Restart now in the restart notice; restarting for {source}."
                : $"Reboot countdown finished; restarting now for {source}.");
            ActivityLog.TryWrite("AutoReboot", "Restarting", new { source, userChoice },
                mode: userChoice ? "Manual" : "Automatic", source: "RebootCoordinator");
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = $"/r /f /t 0 /c \"{safeComment}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            AgentLog.Write("Failed to restart after countdown: " + ex);
            ActivityLog.TryWrite("AutoReboot", "Failed", ex.ToString(), mode: "Automatic", source: "RebootCoordinator");
            System.Windows.Forms.MessageBox.Show(
                "Could not restart Windows.\n\n" + ex.Message +
                "\n\nRestart Windows manually when convenient.",
                "Update Express",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>Issue <c>shutdown /r /t …</c> without Update Express UI (session 0 and fallbacks).</summary>
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

    /// <summary>
    /// Cancel a restart countdown from outside the notice: close a live notice in this session
    /// without restarting, and abort a Windows countdown if one is armed.
    /// </summary>
    internal static void CancelCountdownReboot(string reason = "")
    {
        RebootCountdownWindow.SignalCancel();
        AbortWindowsCountdown(reason);
    }

    /// <summary>Abort a pending <c>shutdown /r /t …</c> countdown if one is active (no-op otherwise).</summary>
    internal static void AbortWindowsCountdown(string reason = "")
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
