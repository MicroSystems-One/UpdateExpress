using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

/// <summary>
/// Category 2 (phase D): daily critical/important scan → download → user prompt.
/// Never auto-installs and never reboots Windows.
/// </summary>
internal static class CriticalAutomation
{
    private static int _running;
    private static Action<string, string>? _balloon;

    internal static void SetBalloon(Action<string, string> balloon) => _balloon = balloon;

    internal static async Task RunDueJobAsync(Dispatcher dispatcher)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            var policy = UpdatePolicy.Load();
            policy.Normalize();
            var critical = policy.WindowsAutomation.Critical;
            if (!policy.WindowsAutomation.Enabled || !critical.Enabled ||
                string.Equals(critical.Mode, "off", StringComparison.OrdinalIgnoreCase))
            {
                WindowsAutomationStatus.WriteCategory("critical", "Disabled", "Critical automation is turned off in policy.", null);
                return;
            }

            string preferred = string.IsNullOrWhiteSpace(critical.PreferredInstallTime)
                ? "20:00"
                : critical.PreferredInstallTime.Trim();
            var prompt = CriticalPromptState.Load();

            // Elevated helper owns the install; do not scan/prompt again until it finishes or fails back.
            if (prompt.State == "installing")
                return;

            if (prompt.State == "scheduled" &&
                prompt.ScheduledInstallAt is DateTime scheduled &&
                scheduled <= DateTime.Now &&
                prompt.Updates.Count > 0)
            {
                await PresentPromptAsync(dispatcher, prompt, preferred,
                    "Scheduled critical updates are due.").ConfigureAwait(false);
                return;
            }

            if (prompt.SnoozeUntil is DateTime snooze && snooze > DateTime.Now)
                return;

            if (prompt.State == "awaitingPrompt" && prompt.Updates.Count > 0)
            {
                await PresentPromptAsync(dispatcher, prompt, preferred,
                    "Critical updates are waiting for confirmation.").ConfigureAwait(false);
                return;
            }

            var status = WindowsAutomationStatus.Load();
            DateTime nextDue = status.Critical.NextDue ?? ComputeFirstDue(preferred);
            if (status.Critical.LastRun is DateTime last && status.Critical.NextDue is null)
                nextDue = ComputeNextDue(last, critical.IntervalMinutes, preferred);
            if (DateTime.Now < nextDue && status.Critical.LastRun is not null)
                return;

            AgentLog.Write("Critical automation: starting scheduled scan.");
            ActivityLog.TryWrite("CriticalAutomation", "Started", new { Mode = critical.Mode }, mode: "Automatic", source: "TrayAgent");

            List<UpdateEntry> pending;
            try
            {
                pending = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(false, false))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WindowsAutomationStatus.WriteCategory(
                    "critical",
                    "Failed",
                    "Scan failed: " + ex.Message,
                    ComputeNextDue(DateTime.Now, critical.IntervalMinutes, preferred));
                ActivityLog.TryWrite("CriticalAutomation", "ScanFailed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
                AgentLog.Write("Critical automation scan failed: " + ex);
                return;
            }

            var matches = pending.Where(UpdateCategoryRules.IsCriticalSoftware).ToList();
            if (matches.Count == 0)
            {
                CriticalPromptState.Clear();
                WindowsAutomationStatus.WriteCategory(
                    "critical",
                    "None pending",
                    "No MSRC Critical/Important software updates pending.",
                    ComputeNextDue(DateTime.Now, critical.IntervalMinutes, preferred));
                ActivityLog.TryWrite("CriticalAutomation", "NonePending", new { Count = 0 }, mode: "Automatic", source: "TrayAgent");
                AgentLog.Write("Critical automation: no Critical/Important updates pending.");
                return;
            }

            bool downloaded = false;
            try
            {
                AgentLog.Write($"Critical automation: downloading {matches.Count} update(s).");
                int count = await WindowsUpdateReader.RunStaAsync(() =>
                        WindowsUpdateReader.DownloadSelected(
                            matches.Select(m => m.UpdateId).ToList(),
                            msg => AgentLog.Write("Critical automation: " + msg)))
                    .ConfigureAwait(false);
                downloaded = count > 0;
            }
            catch (Exception ex)
            {
                AgentLog.Write("Critical automation download failed (will still prompt): " + ex);
                ActivityLog.TryWrite("CriticalAutomation", "DownloadFailed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
            }

            prompt = new CriticalPromptState
            {
                CreatedAt = DateTime.Now,
                Downloaded = downloaded,
                State = "awaitingPrompt",
                Updates = matches.Select(m => new CriticalPromptUpdate
                {
                    UpdateId = m.UpdateId,
                    KB = m.KB,
                    Title = m.Title,
                    Priority = m.Priority
                }).ToList()
            };
            prompt.Save();

            WindowsAutomationStatus.WriteCategory(
                "critical",
                "Awaiting confirmation",
                $"{matches.Count} update(s) ready · downloaded={downloaded}",
                ComputeNextDue(DateTime.Now, critical.IntervalMinutes, preferred));

            await PresentPromptAsync(dispatcher, prompt, preferred,
                $"{matches.Count} critical/important Windows update(s) available.").ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static async Task PresentPromptAsync(
        Dispatcher dispatcher,
        CriticalPromptState prompt,
        string preferred,
        string balloonText)
    {
        try
        {
            try { _balloon?.Invoke("Update Express – Critical updates", balloonText); }
            catch { }

            AgentLog.Write("Critical automation: presenting confirmation dialog (" + prompt.Updates.Count + ").");
            var decision = await dispatcher.InvokeAsync(() =>
            {
                var dialog = new CriticalUpdatesDialog(prompt.Updates, preferred, prompt.Downloaded);
                dialog.ShowDialog();
                return dialog.Decision;
            }).Task.ConfigureAwait(false);

            await HandleDecisionAsync(decision, prompt, preferred).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentLog.Write("Critical automation prompt failed: " + ex);
            ActivityLog.TryWrite("CriticalAutomation", "PromptFailed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
        }
    }

    private static Task HandleDecisionAsync(
        CriticalUpdatesDialog.PromptDecision decision,
        CriticalPromptState prompt,
        string preferred)
    {
        switch (decision)
        {
            case CriticalUpdatesDialog.PromptDecision.InstallNow:
                return StartElevatedInstallAsync(prompt, preferred);

            case CriticalUpdatesDialog.PromptDecision.InstallTonight:
            {
                DateTime when = NextPreferredOccurrence(preferred);
                prompt.State = "scheduled";
                prompt.ScheduledInstallAt = when;
                prompt.SnoozeUntil = when;
                prompt.Save();
                WindowsAutomationStatus.WriteCategory(
                    "critical",
                    "Scheduled",
                    $"Install scheduled for {when:dd.MM HH:mm}. No automatic restart.",
                    when);
                ActivityLog.TryWrite("CriticalAutomation", "Scheduled", new { At = when }, mode: "Automatic", source: "TrayAgent");
                AgentLog.Write("Critical automation: install scheduled for " + when.ToString("O"));
                break;
            }

            case CriticalUpdatesDialog.PromptDecision.RemindOneHour:
            {
                var until = DateTime.Now.AddHours(1);
                prompt.State = "snoozed";
                prompt.SnoozeUntil = until;
                prompt.ScheduledInstallAt = null;
                prompt.Save();
                WindowsAutomationStatus.WriteCategory("critical", "Snoozed", $"Remind at {until:dd.MM HH:mm}", until);
                break;
            }

            case CriticalUpdatesDialog.PromptDecision.RemindTomorrow:
            {
                var until = DateTime.Now.AddDays(1);
                prompt.State = "snoozed";
                prompt.SnoozeUntil = until;
                prompt.ScheduledInstallAt = null;
                prompt.Save();
                WindowsAutomationStatus.WriteCategory("critical", "Snoozed", $"Remind at {until:dd.MM HH:mm}", until);
                break;
            }

            default:
            {
                var until = DateTime.Now.AddHours(6);
                prompt.State = "snoozed";
                prompt.SnoozeUntil = until;
                prompt.ScheduledInstallAt = null;
                prompt.Save();
                WindowsAutomationStatus.WriteCategory("critical", "Deferred", $"Ask again after {until:dd.MM HH:mm}", until);
                break;
            }
        }

        return Task.CompletedTask;
    }

    private static Task StartElevatedInstallAsync(CriticalPromptState prompt, string preferred)
    {
        var ids = prompt.Updates.Select(u => u.UpdateId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count == 0)
        {
            AgentLog.Write("Critical automation: install requested but no update IDs.");
            return Task.CompletedTask;
        }

        prompt.State = "installing";
        prompt.Save();
        CriticalPromptState.WriteInstallQueue(ids);
        ActivityLog.TryWrite("CriticalAutomation", "InstallRequested", new { Count = ids.Count }, mode: "Automatic", source: "TrayAgent");
        AgentLog.Write($"Critical automation: launching elevated install for {ids.Count} update(s).");

        try
        {
            ElevationHelper.RestartElevated("--apply-critical-install");
            WindowsAutomationStatus.WriteCategory(
                "critical",
                "Installing",
                "Elevated installer started. Update Express will not reboot Windows.",
                ComputeNextDue(DateTime.Now, 1440, preferred));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            prompt.State = "awaitingPrompt";
            prompt.Save();
            WindowsAutomationStatus.WriteCategory("critical", "UAC cancelled", "Administrator approval was cancelled.", DateTime.Now.AddHours(1));
            AgentLog.Write("Critical automation: UAC cancelled for install.");
        }
        catch (Exception ex)
        {
            prompt.State = "awaitingPrompt";
            prompt.Save();
            WindowsAutomationStatus.WriteCategory("critical", "Failed", "Could not start elevated install: " + ex.Message, DateTime.Now.AddHours(1));
            AgentLog.Write("Critical automation elevated launch failed: " + ex);
        }

        return Task.CompletedTask;
    }

    internal static void ApplyQueuedInstall()
    {
        var ids = CriticalPromptState.ReadInstallQueue();
        if (ids.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "No critical updates were queued for installation.",
                "Update Express",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        if (!ElevationHelper.IsElevated)
        {
            System.Windows.MessageBox.Show(
                "Administrator privileges are required to install critical Windows updates.",
                "Update Express",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        AgentLog.Write($"Critical install helper: installing {ids.Count} queued update(s).");
        try
        {
            var results = WindowsUpdateReader.InstallSelected(
                ids.Select(id => new UpdateEntry { UpdateId = id }).ToList(),
                msg => AgentLog.Write("Critical install: " + msg));

            CriticalPromptState.ClearInstallQueue();
            CriticalPromptState.Clear();

            int ok = results.Count(r => r.Status is "Installert" or "Already up to date");
            bool rebootHint = results.Any(r => (r.Source ?? "").Contains("Restart", StringComparison.OrdinalIgnoreCase));
            string detail = string.Join("\n", results.Select(r =>
                $"{r.KB} {r.Title}: {r.Status}" + (string.IsNullOrWhiteSpace(r.ErrorCode) ? "" : $" ({r.ErrorCode})")));

            var policy = UpdatePolicy.Load();
            policy.Normalize();
            string preferred = string.IsNullOrWhiteSpace(policy.WindowsAutomation.Critical.PreferredInstallTime)
                ? "20:00"
                : policy.WindowsAutomation.Critical.PreferredInstallTime.Trim();
            WindowsAutomationStatus.WriteCategory(
                "critical",
                rebootHint ? "Installed (restart needed)" : "Installed",
                $"{ok}/{results.Count} completed. Update Express did not restart Windows.",
                ComputeNextDue(DateTime.Now, policy.WindowsAutomation.Critical.IntervalMinutes, preferred));
            ActivityLog.TryWrite("CriticalAutomation", "Installed", new { ok, Total = results.Count, rebootHint }, mode: "Automatic", source: "ElevatedHelper");

            System.Windows.MessageBox.Show(
                $"Critical update installation finished.\n\n{detail}\n\n" +
                (rebootHint
                    ? "A restart may be required. Update Express never restarts Windows automatically — please restart when convenient."
                    : "No automatic restart was performed."),
                "Update Express",
                System.Windows.MessageBoxButton.OK,
                rebootHint ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AgentLog.Write("Critical install helper failed: " + ex);
            ActivityLog.TryWrite("CriticalAutomation", "InstallFailed", ex.ToString(), mode: "Automatic", source: "ElevatedHelper");
            WindowsAutomationStatus.WriteCategory("critical", "Failed", ex.Message, DateTime.Now.AddHours(1));
            var prompt = CriticalPromptState.Load();
            prompt.State = "awaitingPrompt";
            prompt.Save();
            System.Windows.MessageBox.Show(
                "Could not install critical updates.\n\n" + ex.Message + "\n\nSee agent.log for details.",
                "Update Express",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    internal static DateTime ComputeFirstDue(string preferredHhMm) => NextPreferredOccurrence(preferredHhMm);

    internal static DateTime ComputeNextDue(DateTime from, int intervalMinutes, string preferredHhMm)
    {
        int minutes = Math.Max(60, intervalMinutes);
        DateTime raw = from.AddMinutes(minutes);
        if (!TimeSpan.TryParseExact(preferredHhMm, @"hh\:mm", CultureInfo.InvariantCulture, out var preferred))
            return raw;

        DateTime day = raw.Date + preferred;
        if (day < raw) day = day.AddDays(1);
        return day;
    }

    internal static DateTime NextPreferredOccurrence(string preferredHhMm)
    {
        if (!TimeSpan.TryParseExact(preferredHhMm, @"hh\:mm", CultureInfo.InvariantCulture, out var preferred))
            preferred = new TimeSpan(20, 0, 0);
        DateTime candidate = DateTime.Today + preferred;
        if (candidate <= DateTime.Now.AddMinutes(2))
            candidate = candidate.AddDays(1);
        return candidate;
    }
}
