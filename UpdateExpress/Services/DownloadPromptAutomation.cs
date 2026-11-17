using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

/// <summary>
/// Shared K2/K3 engine: scan → download → confirm → elevated install. Never auto-reboots.
/// </summary>
internal static class DownloadPromptAutomation
{
    private static int _criticalRunning;
    private static int _generalRunning;
    private static Action<string, string>? _balloon;

    internal static void SetBalloon(Action<string, string> balloon) => _balloon = balloon;

    internal static Task RunCriticalDueJobAsync(Dispatcher dispatcher) =>
        RunDueJobAsync(
            dispatcher,
            category: "critical",
            getPolicy: p => p.WindowsAutomation.Critical,
            filter: UpdateCategoryRules.IsCriticalSoftware,
            nonePendingDetail: "No MSRC Critical/Important software updates pending.",
            displayName: "Critical",
            balloonTitle: "Update Express – Critical updates",
            elevateArg: "--apply-critical-install",
            minIntervalMinutes: 60,
            useWeeklySchedule: false);

    internal static Task RunGeneralDueJobAsync(Dispatcher dispatcher) =>
        RunDueJobAsync(
            dispatcher,
            category: "general",
            getPolicy: p => p.WindowsAutomation.General,
            filter: UpdateCategoryRules.IsGeneralSoftware,
            nonePendingDetail: "No general Windows software updates pending.",
            displayName: "General",
            balloonTitle: "Update Express – General updates",
            elevateArg: "--apply-general-install",
            minIntervalMinutes: 1440,
            useWeeklySchedule: true);

    private static async Task RunDueJobAsync(
        Dispatcher dispatcher,
        string category,
        Func<UpdatePolicy, CategoryAutomationPolicy> getPolicy,
        Func<UpdateEntry, bool> filter,
        string nonePendingDetail,
        string displayName,
        string balloonTitle,
        string elevateArg,
        int minIntervalMinutes,
        bool useWeeklySchedule)
    {
        if (category == "general")
        {
            if (Interlocked.CompareExchange(ref _generalRunning, 1, 0) != 0) return;
        }
        else if (Interlocked.CompareExchange(ref _criticalRunning, 1, 0) != 0) return;

        try
        {
            var policy = UpdatePolicy.Load();
            policy.Normalize();
            var cat = getPolicy(policy);
            if (!policy.WindowsAutomation.Enabled || !cat.Enabled ||
                string.Equals(cat.Mode, "off", StringComparison.OrdinalIgnoreCase))
            {
                WindowsAutomationStatus.WriteCategory(category, "Disabled", displayName + " automation is turned off in policy.", null);
                return;
            }

            string preferred = AutomationSchedule.PreferredOrDefault(cat.PreferredInstallTime);
            var prompt = CategoryPromptState.LoadAndReconcile(category);

            if (prompt.State == "installing")
                return;

            if (prompt.State == "scheduled" &&
                prompt.ScheduledInstallAt is DateTime scheduled &&
                scheduled <= DateTime.Now &&
                prompt.Updates.Count > 0)
            {
                // Missed/overdue schedule (sleep, power-off): re-check the live maintenance
                // window instead of installing mid-day on an unselected day.
                int mask = MaintenanceSetup.ClampMask(policy.InstallDaysMask);
                string windowTime = MaintenanceSetup.NormalizeTime(
                    string.IsNullOrWhiteSpace(policy.InstallTime) ? preferred : policy.InstallTime,
                    preferred);
                if (mask != 0 && !MaintenanceSetup.IsWithinInstallWindow(mask, windowTime, DateTime.Now))
                {
                    AgentLog.Write($"{displayName} automation: overdue schedule outside install window — rescheduling.");
                    await MaybeDeferOrPresentAsync(dispatcher, policy, prompt, preferred, displayName, balloonTitle,
                        $"Scheduled {displayName.ToLowerInvariant()} updates are due.", elevateArg, cat, useWeeklySchedule, minIntervalMinutes)
                        .ConfigureAwait(false);
                    return;
                }
                // Install window reached: start elevated install (reboot still follows AutoReboot policy).
                AgentLog.Write($"{displayName} automation: scheduled install window reached — starting install.");
                await StartElevatedInstallAsync(prompt, preferred, displayName, elevateArg, cat, useWeeklySchedule, minIntervalMinutes)
                    .ConfigureAwait(false);
                return;
            }

            if (prompt.SnoozeUntil is DateTime snooze && snooze > DateTime.Now)
                return;

            if (prompt.State == "awaitingPrompt" && prompt.Updates.Count > 0)
            {
                await MaybeDeferOrPresentAsync(dispatcher, policy, prompt, preferred, displayName, balloonTitle,
                    $"{displayName} updates are waiting for confirmation.", elevateArg, cat, useWeeklySchedule, minIntervalMinutes)
                    .ConfigureAwait(false);
                return;
            }

            var status = WindowsAutomationStatus.Load();
            var entry = category == "general" ? status.General : status.Critical;
            DateTime nextDue = entry.NextDue ?? AutomationSchedule.NextPreferredOccurrence(preferred);
            if (entry.LastRun is DateTime last && entry.NextDue is null)
                nextDue = NextDue(last, cat, preferred, useWeeklySchedule, minIntervalMinutes);
            if (DateTime.Now < nextDue && entry.LastRun is not null)
                return;

            AgentLog.Write($"{displayName} automation: starting scheduled scan.");
            ActivityLog.TryWrite(displayName + "Automation", "Started", new { Mode = cat.Mode }, mode: "Automatic", source: "TrayAgent");

            List<UpdateEntry> pending;
            try
            {
                pending = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(false, false))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WindowsAutomationStatus.WriteCategory(
                    category, "Failed", "Scan failed: " + ex.Message,
                    NextDue(DateTime.Now, cat, preferred, useWeeklySchedule, minIntervalMinutes));
                ActivityLog.TryWrite(displayName + "Automation", "ScanFailed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
                AgentLog.Write($"{displayName} automation scan failed: " + ex);
                return;
            }

            var matches = pending.Where(filter).ToList();
            if (matches.Count == 0)
            {
                CategoryPromptState.Clear(category);
                WindowsAutomationStatus.WriteCategory(
                    category, "None pending", nonePendingDetail,
                    NextDue(DateTime.Now, cat, preferred, useWeeklySchedule, minIntervalMinutes));
                ActivityLog.TryWrite(displayName + "Automation", "NonePending", new { Count = 0 }, mode: "Automatic", source: "TrayAgent");
                AgentLog.Write($"{displayName} automation: no matching updates pending.");
                return;
            }

            bool downloaded = false;
            try
            {
                AgentLog.Write($"{displayName} automation: downloading {matches.Count} update(s).");
                int count = await WindowsUpdateReader.RunStaAsync(() =>
                        WindowsUpdateReader.DownloadSelected(
                            matches.Select(m => m.UpdateId).ToList(),
                            msg => AgentLog.Write($"{displayName} automation: " + msg)))
                    .ConfigureAwait(false);
                downloaded = count > 0;
            }
            catch (Exception ex)
            {
                AgentLog.Write($"{displayName} automation download failed (will still prompt): " + ex);
                ActivityLog.TryWrite(displayName + "Automation", "DownloadFailed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
            }

            prompt = new CategoryPromptState
            {
                Category = category,
                CreatedAt = DateTime.Now,
                Downloaded = downloaded,
                State = "awaitingPrompt",
                Updates = matches.Select(m => new PromptUpdateItem
                {
                    UpdateId = m.UpdateId,
                    KB = m.KB,
                    Title = m.Title,
                    Priority = m.Priority
                }).ToList()
            };
            prompt.Save();

            WindowsAutomationStatus.WriteCategory(
                category,
                "Awaiting confirmation",
                $"{matches.Count} update(s) ready · downloaded={downloaded}",
                NextDue(DateTime.Now, cat, preferred, useWeeklySchedule, minIntervalMinutes, policy));

            await MaybeDeferOrPresentAsync(dispatcher, policy, prompt, preferred, displayName, balloonTitle,
                $"{matches.Count} {displayName.ToLowerInvariant()} Windows update(s) available.",
                elevateArg, cat, useWeeklySchedule, minIntervalMinutes).ConfigureAwait(false);
        }
        finally
        {
            if (category == "general") Interlocked.Exchange(ref _generalRunning, 0);
            else Interlocked.Exchange(ref _criticalRunning, 0);
        }
    }

    private static DateTime NextDue(
        DateTime from,
        CategoryAutomationPolicy cat,
        string preferred,
        bool useWeeklySchedule,
        int minIntervalMinutes,
        UpdatePolicy? policy = null)
    {
        policy ??= UpdatePolicy.Load();
        int mask = MaintenanceSetup.ClampMask(policy.InstallDaysMask);
        string time = MaintenanceSetup.NormalizeTime(
            string.IsNullOrWhiteSpace(policy.InstallTime) ? preferred : policy.InstallTime,
            preferred);
        if (mask != 0 && mask != MaintenanceSetup.AllDaysMask)
            return AutomationSchedule.ComputeNextInstallWindowDue(from, cat.IntervalMinutes, time, mask, minIntervalMinutes);
        return useWeeklySchedule
            ? AutomationSchedule.ComputeNextWeeklyDue(from, cat.IntervalMinutes, preferred, cat.Weekday)
            : AutomationSchedule.ComputeNextDue(from, cat.IntervalMinutes, preferred, minIntervalMinutes);
    }

    /// <summary>
    /// Outside the install window: schedule + evening notice. Inside window: normal confirmation dialog.
    /// </summary>
    private static async Task MaybeDeferOrPresentAsync(
        Dispatcher dispatcher,
        UpdatePolicy policy,
        CategoryPromptState prompt,
        string preferred,
        string displayName,
        string balloonTitle,
        string balloonText,
        string elevateArg,
        CategoryAutomationPolicy cat,
        bool useWeeklySchedule,
        int minIntervalMinutes)
    {
        policy.Normalize();
        int mask = MaintenanceSetup.ClampMask(policy.InstallDaysMask);
        string time = MaintenanceSetup.NormalizeTime(
            string.IsNullOrWhiteSpace(policy.InstallTime) ? preferred : policy.InstallTime,
            preferred);
        bool norwegian = MaintenanceSetup.IsNorwegian();
        bool autoReboot = policy.WindowsAutomation.AutoReboot.Enabled;

        if (mask != 0 &&
            !MaintenanceSetup.IsWithinInstallWindow(mask, time, DateTime.Now) &&
            MaintenanceSetup.NextInstallOccurrence(mask, time, DateTime.Now) is DateTime when &&
            when > DateTime.Now.AddMinutes(5))
        {
            prompt.State = "scheduled";
            prompt.ScheduledInstallAt = when;
            prompt.SnoozeUntil = when;
            prompt.Save();
            WindowsAutomationStatus.WriteCategory(
                prompt.Category, "Scheduled",
                norwegian
                    ? $"Installasjon satt til {when:dd.MM HH:mm}. Lagre åpne filer før du går."
                    : $"Install scheduled for {when:dd.MM HH:mm}. Save open files before you leave.",
                when);
            ActivityLog.TryWrite(displayName + "Automation", "ScheduledWindow", new { At = when }, mode: "Automatic", source: "TrayAgent");

            string title = MaintenanceSetup.EveningNoticeTitle(norwegian);
            string body = when.Date == DateTime.Today
                ? MaintenanceSetup.EveningNoticeBody(when, norwegian, autoReboot)
                : (norwegian
                    ? $"Nye Windows-oppdateringer er lastet ned og klare.\nInstallasjon er satt til {when:ddd dd.MM} kl. {when:HH:mm}.\n\nTips: Lagre åpne dokumenter før PC-en installerer og eventuelt restarter."
                    : $"New Windows updates are downloaded and ready.\nInstallation is set for {when:ddd dd MMM} at {when:HH:mm}.\n\nTip: Save open files before the PC installs and possibly restarts.");

            try { _balloon?.Invoke(title, MaintenanceSetup.EveningBalloon(when, norwegian)); }
            catch { }

            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show(body, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }).Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AgentLog.Write($"{displayName} automation scheduled notice failed: " + ex.Message);
            }
            return;
        }

        await PresentPromptAsync(dispatcher, prompt, preferred, displayName, balloonTitle, balloonText,
            elevateArg, cat, useWeeklySchedule, minIntervalMinutes).ConfigureAwait(false);
    }

    private static async Task PresentPromptAsync(
        Dispatcher dispatcher,
        CategoryPromptState prompt,
        string preferred,
        string displayName,
        string balloonTitle,
        string balloonText,
        string elevateArg,
        CategoryAutomationPolicy cat,
        bool useWeeklySchedule,
        int minIntervalMinutes)
    {
        try
        {
            try { _balloon?.Invoke(balloonTitle, balloonText); }
            catch { }

            AgentLog.Write($"{displayName} automation: presenting confirmation dialog ({prompt.Updates.Count}).");
            var decision = await dispatcher.InvokeAsync(() =>
            {
                var dialog = new SoftwareUpdatesDialog(displayName, prompt.Updates, preferred, prompt.Downloaded);
                dialog.ShowDialog();
                return dialog.Decision;
            }).Task.ConfigureAwait(false);

            await HandleDecisionAsync(decision, prompt, preferred, displayName, elevateArg, cat, useWeeklySchedule, minIntervalMinutes)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentLog.Write($"{displayName} automation prompt failed: " + ex);
            ActivityLog.TryWrite(displayName + "Automation", "PromptFailed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
        }
    }

    private static Task HandleDecisionAsync(
        SoftwareUpdatesDialog.PromptDecision decision,
        CategoryPromptState prompt,
        string preferred,
        string displayName,
        string elevateArg,
        CategoryAutomationPolicy cat,
        bool useWeeklySchedule,
        int minIntervalMinutes)
    {
        switch (decision)
        {
            case SoftwareUpdatesDialog.PromptDecision.InstallNow:
                return StartElevatedInstallAsync(prompt, preferred, displayName, elevateArg, cat, useWeeklySchedule, minIntervalMinutes);

            case SoftwareUpdatesDialog.PromptDecision.InstallAtPreferred:
            {
                var pol = UpdatePolicy.Load();
                pol.Normalize();
                DateTime when = MaintenanceSetup.NextInstallOccurrence(pol.InstallDaysMask, pol.InstallTime, DateTime.Now)
                    ?? AutomationSchedule.NextPreferredOccurrence(preferred);
                prompt.State = "scheduled";
                prompt.ScheduledInstallAt = when;
                prompt.SnoozeUntil = when;
                prompt.Save();
                WindowsAutomationStatus.WriteCategory(
                    prompt.Category, "Scheduled",
                    $"Install scheduled for {when:dd.MM HH:mm}. No automatic restart.", when);
                ActivityLog.TryWrite(displayName + "Automation", "Scheduled", new { At = when }, mode: "Automatic", source: "TrayAgent");
                AgentLog.Write($"{displayName} automation: install scheduled for " + when.ToString("O"));
                break;
            }

            case SoftwareUpdatesDialog.PromptDecision.RemindOneHour:
            {
                var until = DateTime.Now.AddHours(1);
                prompt.State = "snoozed";
                prompt.SnoozeUntil = until;
                prompt.ScheduledInstallAt = null;
                prompt.Save();
                WindowsAutomationStatus.WriteCategory(prompt.Category, "Snoozed", $"Remind at {until:dd.MM HH:mm}", until);
                break;
            }

            case SoftwareUpdatesDialog.PromptDecision.RemindTomorrow:
            {
                var until = DateTime.Now.AddDays(1);
                prompt.State = "snoozed";
                prompt.SnoozeUntil = until;
                prompt.ScheduledInstallAt = null;
                prompt.Save();
                WindowsAutomationStatus.WriteCategory(prompt.Category, "Snoozed", $"Remind at {until:dd.MM HH:mm}", until);
                break;
            }

            default:
            {
                var until = DateTime.Now.AddHours(6);
                prompt.State = "snoozed";
                prompt.SnoozeUntil = until;
                prompt.ScheduledInstallAt = null;
                prompt.Save();
                WindowsAutomationStatus.WriteCategory(prompt.Category, "Deferred", $"Ask again after {until:dd.MM HH:mm}", until);
                break;
            }
        }

        return Task.CompletedTask;
    }

    private static Task StartElevatedInstallAsync(
        CategoryPromptState prompt,
        string preferred,
        string displayName,
        string elevateArg,
        CategoryAutomationPolicy cat,
        bool useWeeklySchedule,
        int minIntervalMinutes)
    {
        var ids = prompt.Updates.Select(u => u.UpdateId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count == 0)
        {
            AgentLog.Write($"{displayName} automation: install requested but no update IDs.");
            return Task.CompletedTask;
        }

        prompt.MarkInstalling();
        prompt.Save();
        prompt.WriteInstallQueue(ids);
        ActivityLog.TryWrite(displayName + "Automation", "InstallRequested", new { Count = ids.Count }, mode: "Automatic", source: "TrayAgent");
        AgentLog.Write($"{displayName} automation: launching elevated install for {ids.Count} update(s).");

        try
        {
            ElevationHelper.RestartElevated(AutomationStatePaths.ElevateArguments(elevateArg));
            WindowsAutomationStatus.WriteCategory(
                prompt.Category, "Installing",
                "Elevated installer started. Update Express will not reboot Windows.",
                NextDue(DateTime.Now, cat, preferred, useWeeklySchedule, minIntervalMinutes));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            prompt.State = "awaitingPrompt";
            prompt.InstallingStartedAt = null;
            prompt.Save();
            WindowsAutomationStatus.WriteCategory(prompt.Category, "UAC cancelled", "Administrator approval was cancelled.", DateTime.Now.AddHours(1));
            AgentLog.Write($"{displayName} automation: UAC cancelled for install.");
        }
        catch (Exception ex)
        {
            prompt.State = "awaitingPrompt";
            prompt.InstallingStartedAt = null;
            prompt.Save();
            WindowsAutomationStatus.WriteCategory(prompt.Category, "Failed", "Could not start elevated install: " + ex.Message, DateTime.Now.AddHours(1));
            AgentLog.Write($"{displayName} automation elevated launch failed: " + ex);
        }

        return Task.CompletedTask;
    }

    internal static void ApplyQueuedInstall(string category, string displayName)
    {
        var ids = CategoryPromptState.ReadInstallQueue(category);
        if (ids.Count == 0)
        {
            System.Windows.MessageBox.Show(
                $"No {displayName.ToLowerInvariant()} updates were queued for installation.",
                "Update Express",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        if (!ElevationHelper.IsElevated)
        {
            System.Windows.MessageBox.Show(
                $"Administrator privileges are required to install {displayName.ToLowerInvariant()} Windows updates.",
                "Update Express",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var prompt = CategoryPromptState.Load(category);
        var queue = ids.Select(id =>
        {
            var meta = prompt.Updates.FirstOrDefault(u =>
                u.UpdateId.Equals(id, StringComparison.OrdinalIgnoreCase));
            string title = meta?.Title ?? id;
            if (!string.IsNullOrWhiteSpace(meta?.KB))
                title = meta!.KB + " — " + title;
            return new InstallationQueueItem("Windows Update", title);
        }).ToList();

        AgentLog.Write($"{displayName} install helper: installing {ids.Count} queued update(s) with progress UI.");
        var manager = new InstallationManagerWindow(queue)
        {
            Title = $"Update Express – {displayName} installation",
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
        };

        manager.Loaded += async (_, _) =>
        {
            manager.PrepareRunning();
            bool norwegian = string.Equals(ReminderSchedule.Load().Language, "no", StringComparison.OrdinalIgnoreCase);
            manager.SetNotice(WuaProgressText.PleaseWait(norwegian));
            manager.SetStage(norwegian ? "Forbereder nedlasting…" : "Preparing download…");
            try
            {
                var selected = ids.Select(id => new UpdateEntry { UpdateId = id }).ToList();
                IProgress<WuaActivity> activity = new Progress<WuaActivity>(update =>
                {
                    try { manager.ApplyActivity(update, norwegian); }
                    catch { }
                    AgentLog.Write($"{displayName} install: " + WuaProgressText.Stage(norwegian, update.Phase, update.Kb, update.Number, update.Total, update.Percent));
                });
                IProgress<string> progress = new Progress<string>(stage => AgentLog.Write($"{displayName} install: " + stage));

                var results = await WindowsUpdateReader.RunStaAsync(() =>
                        WindowsUpdateReader.InstallSelected(selected, progress.Report, activity.Report))
                    .ConfigureAwait(true);

                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i];
                    var requested = selected[i];
                    var result = WindowsUpdateReader.MatchInstallResult(results, requested, i, ids.Count)
                                 ?? results.FirstOrDefault(x =>
                                     !string.IsNullOrWhiteSpace(x.UpdateId) &&
                                     x.UpdateId.Equals(id, StringComparison.OrdinalIgnoreCase));
                    string state = result == null ? "Not verified" :
                        result.Status is "Installert" or "Already up to date" ? "Completed" :
                        result.Status is "Delvis fullført" ? "Not verified" :
                        "Failed" + (string.IsNullOrWhiteSpace(result.ErrorCode) ? "" : " " + result.ErrorCode);
                    manager.CompleteItem(i, state, state.StartsWith("Failed", StringComparison.Ordinal));
                }

                CategoryPromptState.ClearInstallQueue(category);
                CategoryPromptState.Clear(category);

                int ok = results.Count(r => r.Status is "Installert" or "Already up to date");
                bool rebootHint = results.Any(r => (r.Source ?? "").Contains("Restart", StringComparison.OrdinalIgnoreCase));
                var policy = UpdatePolicy.Load();
                policy.Normalize();
                var cat = category == "general" ? policy.WindowsAutomation.General : policy.WindowsAutomation.Critical;
                string preferred = AutomationSchedule.PreferredOrDefault(cat.PreferredInstallTime);
                bool weekly = category == "general";
                int minInterval = weekly ? 1440 : 60;
                WindowsAutomationStatus.WriteCategory(
                    category,
                    rebootHint ? "Installed (restart needed)" : "Installed",
                    rebootHint
                        ? $"{ok}/{results.Count} completed. Restart may be required."
                        : $"{ok}/{results.Count} completed.",
                    NextDue(DateTime.Now, cat, preferred, weekly, minInterval));
                ActivityLog.TryWrite(displayName + "Automation", "Installed", new { ok, Total = results.Count, rebootHint }, mode: "Automatic", source: "ElevatedHelper");

                manager.Finish();
                if (rebootHint)
                {
                    bool autoReboot = policy.WindowsAutomation.AutoReboot.Enabled;
                    manager.SetStage(autoReboot
                        ? "Finished — restart required. Optional auto-reboot policy will run next…"
                        : "Finished — a restart may be required. Auto-reboot is disabled in policy.");
                    RebootCoordinator.HandleAfterInstall(displayName, rebootRequired: true, owner: manager);
                }
            }
            catch (Exception ex)
            {
                AgentLog.Write($"{displayName} install helper failed: " + ex);
                ActivityLog.TryWrite(displayName + "Automation", "InstallFailed", ex.ToString(), mode: "Automatic", source: "ElevatedHelper");
                WindowsAutomationStatus.WriteCategory(category, "Failed", ex.Message, DateTime.Now.AddHours(1));
                var retry = CategoryPromptState.Load(category);
                retry.State = "awaitingPrompt";
                retry.InstallingStartedAt = null;
                retry.Save();
                for (int i = 0; i < queue.Count; i++)
                    manager.CompleteItem(i, "Failed", true);
                manager.Finish();
                manager.SetStage("Failed: " + ex.Message);
                System.Windows.MessageBox.Show(
                    manager,
                    $"Could not install {displayName.ToLowerInvariant()} updates.\n\n" + ex.Message + "\n\nSee agent.log for details.",
                    "Update Express",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        };

        manager.ShowDialog();
    }
}
