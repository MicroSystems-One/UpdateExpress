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
/// Optional drivers automation: scan assigned WUA drivers → download → confirm → elevated install.
/// Firmware stays manual. Never auto-reboots.
/// </summary>
internal static class DriverAutomation
{
    private const string Category = "drivers";
    private const string DisplayName = "Drivers";
    private const string ElevateArg = "--apply-drivers-install";
    /// <summary>Default driver scan interval (1 day).</summary>
    internal const int DefaultIntervalMinutes = 1440;
    /// <summary>Hard floor for driver scan cadence (1 day).</summary>
    internal const int AbsoluteFloorMinutes = 1440;

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
            if (!policy.WindowsAutomation.Enabled || !policy.AutomaticDrivers)
            {
                WindowsAutomationStatus.WriteCategory(Category, "Disabled", "Drivers automation is turned off in policy.", null);
                return;
            }

            var schedule = policy.WindowsAutomation.Critical;
            string preferred = AutomationSchedule.PreferredOrDefault(schedule.PreferredInstallTime);
            int intervalMinutes = ResolveIntervalMinutes(policy);
            var prompt = CategoryPromptState.LoadAndReconcile(Category);

            if (prompt.State == "installing")
                return;

            if (prompt.State == "scheduled" &&
                prompt.ScheduledInstallAt is DateTime scheduled &&
                scheduled <= DateTime.Now &&
                prompt.Updates.Count > 0)
            {
                await PresentPromptAsync(dispatcher, prompt, preferred,
                    $"Scheduled driver updates are due.").ConfigureAwait(false);
                return;
            }

            if (prompt.SnoozeUntil is DateTime snooze && snooze > DateTime.Now)
                return;

            if (prompt.State == "awaitingPrompt" && prompt.Updates.Count > 0)
            {
                await PresentPromptAsync(dispatcher, prompt, preferred,
                    "Driver updates are waiting for confirmation.").ConfigureAwait(false);
                return;
            }

            var status = WindowsAutomationStatus.Load();
            var entry = status.Drivers;
            DateTime nextDue = entry.NextDue ?? AutomationSchedule.NextPreferredOccurrence(preferred);
            if (entry.LastRun is DateTime last && entry.NextDue is null)
                nextDue = NextDue(last, intervalMinutes, preferred);
            if (DateTime.Now < nextDue && entry.LastRun is not null)
                return;

            AgentLog.Write("Drivers automation: starting scheduled scan.");
            ActivityLog.TryWrite("DriversAutomation", "Started", new { Mode = "downloadAndPrompt" }, mode: "Automatic", source: "TrayAgent");

            List<UpdateEntry> pending;
            try
            {
                // Assigned drivers + firmware inventory; filter keeps assigned non-firmware only.
                pending = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(false, false))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WindowsAutomationStatus.WriteCategory(Category, "Failed", "Scan failed: " + ex.Message, NextDue(DateTime.Now, intervalMinutes, preferred));
                ActivityLog.TryWrite("DriversAutomation", "ScanFailed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
                AgentLog.Write("Drivers automation scan failed: " + ex);
                return;
            }

            var matches = pending.Where(UpdateCategoryRules.IsAutomaticDriver).ToList();
            if (matches.Count == 0)
            {
                CategoryPromptState.Clear(Category);
                WindowsAutomationStatus.WriteCategory(
                    Category, "None pending", "No assigned WUA drivers pending.",
                    NextDue(DateTime.Now, intervalMinutes, preferred));
                ActivityLog.TryWrite("DriversAutomation", "NonePending", new { Count = 0 }, mode: "Automatic", source: "TrayAgent");
                AgentLog.Write("Drivers automation: no matching drivers pending.");
                return;
            }

            bool downloaded = false;
            try
            {
                AgentLog.Write($"Drivers automation: downloading {matches.Count} driver(s).");
                int count = await WindowsUpdateReader.RunStaAsync(() =>
                        WindowsUpdateReader.DownloadSelectedDrivers(
                            matches.Select(m => m.UpdateId).ToList(),
                            msg => AgentLog.Write("Drivers automation: " + msg)))
                    .ConfigureAwait(false);
                downloaded = count > 0;
            }
            catch (Exception ex)
            {
                AgentLog.Write("Drivers automation download failed (will still prompt): " + ex);
                ActivityLog.TryWrite("DriversAutomation", "DownloadFailed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
            }

            prompt = new CategoryPromptState
            {
                Category = Category,
                CreatedAt = DateTime.Now,
                Downloaded = downloaded,
                State = "awaitingPrompt",
                Updates = matches.Select(m => new PromptUpdateItem
                {
                    UpdateId = m.UpdateId,
                    KB = m.KB,
                    Title = m.Title,
                    Priority = m.Priority,
                    HardwareId = m.DriverHardwareId
                }).ToList()
            };
            prompt.Save();

            WindowsAutomationStatus.WriteCategory(
                Category,
                "Awaiting confirmation",
                $"{matches.Count} driver(s) ready · downloaded={downloaded}",
                NextDue(DateTime.Now, intervalMinutes, preferred));

            await PresentPromptAsync(dispatcher, prompt, preferred,
                $"{matches.Count} assigned driver update(s) available.").ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    internal static int ResolveIntervalMinutes(UpdatePolicy? policy = null)
    {
        policy ??= UpdatePolicy.Load();
        policy.Normalize();
        return Math.Max(AbsoluteFloorMinutes, policy.DriversIntervalMinutes);
    }

    private static DateTime NextDue(DateTime from, int intervalMinutes, string preferred) =>
        AutomationSchedule.ComputeNextDue(from, intervalMinutes, preferred, AbsoluteFloorMinutes);

    private static async Task PresentPromptAsync(
        Dispatcher dispatcher,
        CategoryPromptState prompt,
        string preferred,
        string balloonText)
    {
        try
        {
            if (MaintenanceReminderPromptGate.ShouldSuppressCategoryUpdateReadyPrompt())
            {
                AgentLog.Write(
                    "Drivers automation: updates-ready prompt suppressed " +
                    "(Påminnelse covers pending updates in this Main UI session).");
                return;
            }

            try { _balloon?.Invoke("Update Express – Driver updates", balloonText); }
            catch { }

            AgentLog.Write($"Drivers automation: presenting confirmation dialog ({prompt.Updates.Count}).");
            var shown = await dispatcher.InvokeAsync(() =>
            {
                if (!SoftwareUpdatesDialog.TryShowModal(
                        DisplayName, prompt.Updates, preferred, prompt.Downloaded, out var decision))
                {
                    AgentLog.Write("Drivers automation: updates-ready dialog already open — skip stack.");
                    return (Shown: false, Decision: SoftwareUpdatesDialog.PromptDecision.Later);
                }
                return (Shown: true, Decision: decision);
            }).Task.ConfigureAwait(false);

            if (!shown.Shown)
                return;

            await HandleDecisionAsync(shown.Decision, prompt, preferred).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentLog.Write("Drivers automation prompt failed: " + ex);
            ActivityLog.TryWrite("DriversAutomation", "PromptFailed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
        }
    }

    private static Task HandleDecisionAsync(
        SoftwareUpdatesDialog.PromptDecision decision,
        CategoryPromptState prompt,
        string preferred)
    {
        var policy = UpdatePolicy.Load();
        policy.Normalize();
        var schedule = policy.WindowsAutomation.Critical;

        switch (decision)
        {
            case SoftwareUpdatesDialog.PromptDecision.InstallNow:
                return StartElevatedInstallAsync(prompt, preferred, schedule);

            case SoftwareUpdatesDialog.PromptDecision.InstallAtPreferred:
            {
                DateTime when = AutomationSchedule.NextPreferredOccurrence(preferred);
                prompt.State = "scheduled";
                prompt.ScheduledInstallAt = when;
                prompt.SnoozeUntil = when;
                prompt.Save();
                WindowsAutomationStatus.WriteCategory(
                    Category, "Scheduled",
                    $"Install scheduled for {when:dd.MM HH:mm}. No automatic restart.", when);
                ActivityLog.TryWrite("DriversAutomation", "Scheduled", new { At = when }, mode: "Automatic", source: "TrayAgent");
                AgentLog.Write("Drivers automation: install scheduled for " + when.ToString("O"));
                break;
            }

            case SoftwareUpdatesDialog.PromptDecision.RemindOneHour:
            {
                var until = DateTime.Now.AddHours(1);
                prompt.State = "snoozed";
                prompt.SnoozeUntil = until;
                prompt.ScheduledInstallAt = null;
                prompt.Save();
                WindowsAutomationStatus.WriteCategory(Category, "Snoozed", $"Remind at {until:dd.MM HH:mm}", until);
                break;
            }

            case SoftwareUpdatesDialog.PromptDecision.RemindTomorrow:
            {
                var until = DateTime.Now.AddDays(1);
                prompt.State = "snoozed";
                prompt.SnoozeUntil = until;
                prompt.ScheduledInstallAt = null;
                prompt.Save();
                WindowsAutomationStatus.WriteCategory(Category, "Snoozed", $"Remind at {until:dd.MM HH:mm}", until);
                break;
            }

            default:
            {
                var until = DateTime.Now.AddHours(6);
                prompt.State = "snoozed";
                prompt.SnoozeUntil = until;
                prompt.ScheduledInstallAt = null;
                prompt.Save();
                WindowsAutomationStatus.WriteCategory(Category, "Deferred", $"Ask again after {until:dd.MM HH:mm}", until);
                break;
            }
        }

        return Task.CompletedTask;
    }

    private static Task StartElevatedInstallAsync(
        CategoryPromptState prompt,
        string preferred,
        CategoryAutomationPolicy schedule)
    {
        var ids = prompt.Updates.Select(u => u.UpdateId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count == 0)
        {
            AgentLog.Write("Drivers automation: install requested but no update IDs.");
            return Task.CompletedTask;
        }

        prompt.MarkInstalling();
        prompt.Save();
        prompt.WriteInstallQueue(ids);
        ActivityLog.TryWrite("DriversAutomation", "InstallRequested", new { Count = ids.Count }, mode: "Automatic", source: "TrayAgent");
        AgentLog.Write($"Drivers automation: launching elevated install for {ids.Count} driver(s).");

        try
        {
            ElevationHelper.RestartElevated(AutomationStatePaths.ElevateArguments(ElevateArg));
            WindowsAutomationStatus.WriteCategory(
                Category, "Installing",
                "Elevated driver installer started. Update Express will not reboot Windows.",
                NextDue(DateTime.Now, ResolveIntervalMinutes(), preferred));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            prompt.State = "awaitingPrompt";
            prompt.InstallingStartedAt = null;
            prompt.Save();
            WindowsAutomationStatus.WriteCategory(Category, "UAC cancelled", "Administrator approval was cancelled.", DateTime.Now.AddHours(1));
            AgentLog.Write("Drivers automation: UAC cancelled for install.");
        }
        catch (Exception ex)
        {
            prompt.State = "awaitingPrompt";
            prompt.InstallingStartedAt = null;
            prompt.Save();
            WindowsAutomationStatus.WriteCategory(Category, "Failed", "Could not start elevated install: " + ex.Message, DateTime.Now.AddHours(1));
            AgentLog.Write("Drivers automation elevated launch failed: " + ex);
        }

        return Task.CompletedTask;
    }

    internal static void ApplyQueuedInstall()
    {
        var ids = CategoryPromptState.ReadInstallQueue(Category);
        if (ids.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "No driver updates were queued for installation.",
                "Update Express",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        if (!ElevationHelper.IsElevated)
        {
            System.Windows.MessageBox.Show(
                "Administrator privileges are required to install driver updates.",
                "Update Express",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var prompt = CategoryPromptState.Load(Category);
        var selected = ids.Select(id =>
        {
            var meta = prompt.Updates.FirstOrDefault(u =>
                u.UpdateId.Equals(id, StringComparison.OrdinalIgnoreCase));
            return new UpdateEntry
            {
                UpdateId = id,
                Title = meta?.Title ?? id,
                Category = "Driver / firmware",
                DriverHardwareId = meta?.HardwareId ?? "",
                KB = meta?.KB ?? "",
                Priority = meta?.Priority ?? ""
            };
        }).Where(e => !string.IsNullOrWhiteSpace(e.DriverHardwareId)).ToList();

        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "Queued drivers are missing verified hardware IDs. No installation was attempted.",
                "Update Express",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var queue = selected.Select(d => new InstallationQueueItem("Driver", d.Title) { UpdateId = d.UpdateId }).ToList();
        AgentLog.Write($"Drivers install helper: installing {selected.Count} queued driver(s) with progress UI.");
        var manager = new InstallationManagerWindow(queue)
        {
            Title = "Update Express – Drivers installation",
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
        };

        manager.Loaded += async (_, _) =>
        {
            manager.PrepareRunning();
            int ok = 0;
            bool rebootHint = false;
            for (int i = 0; i < selected.Count; i++)
            {
                var d = selected[i];
                manager.BeginItem(i, "Rechecking / installing " + d.Title + "…");
                try
                {
                    var results = await WindowsUpdateReader.RunStaAsync(() =>
                            WindowsUpdateReader.InstallSelectedDriver(d, stage =>
                            {
                                try { manager.Dispatcher.BeginInvoke(new Action(() => manager.SetStage(stage))); }
                                catch { }
                                AgentLog.Write("Drivers install: " + stage);
                            }, allowInteractive: false))
                        .ConfigureAwait(true);

                    var driverResult = results.FirstOrDefault();
                    string state = driverResult == null ? "Not verified" :
                        driverResult.Status is "Installert" or "Already up to date" ? "Completed" :
                        driverResult.Status is "Delvis fullført" or "Verification unavailable" ? "Not verified" :
                        "Failed" + (string.IsNullOrWhiteSpace(driverResult.ErrorCode) ? "" : " " + driverResult.ErrorCode);
                    if (state == "Completed") ok++;
                    if (driverResult?.Source?.Contains("Restart", StringComparison.OrdinalIgnoreCase) == true)
                        rebootHint = true;
                    if (driverResult is not null &&
                        driverResult.Status == "Installert" &&
                        (driverResult.Source ?? "").Contains("Restart required", StringComparison.OrdinalIgnoreCase))
                        DriverRebootHoldStore.Remember(d, "Restart required after WUA driver install");
                    manager.CompleteItem(i, state, state.StartsWith("Failed", StringComparison.Ordinal));
                }
                catch (Exception ex)
                {
                    AgentLog.Write("Drivers install item failed: " + ex);
                    manager.CompleteItem(i, "Failed", true);
                }
            }

            CategoryPromptState.ClearInstallQueue(Category);
            CategoryPromptState.Clear(Category);

            var policy = UpdatePolicy.Load();
            policy.Normalize();
            string preferred = AutomationSchedule.PreferredOrDefault(policy.WindowsAutomation.Critical.PreferredInstallTime);
            WindowsAutomationStatus.WriteCategory(
                Category,
                rebootHint ? "Installed (restart needed)" : "Installed",
                rebootHint
                    ? $"{ok}/{selected.Count} completed. Restart may be required."
                    : $"{ok}/{selected.Count} completed.",
                NextDue(DateTime.Now, ResolveIntervalMinutes(policy), preferred));
            ActivityLog.TryWrite("DriversAutomation", "Installed", new { ok, Total = selected.Count, rebootHint }, mode: "Automatic", source: "ElevatedHelper");
            try { HistoryArchive.RecordInstallationManagerQueue(queue); }
            catch (Exception ex) { AgentLog.Write("Drivers install: history record failed: " + ex.Message); }

            manager.Finish();
            if (rebootHint)
            {
                // Auto-reboot policy is K2/K3 only; drivers never schedule a reboot.
                manager.SetStage("Finished — a restart may be required. Auto-reboot applies only to Critical/General (K2/K3).");
                AgentLog.Write("Drivers: restart may be required; auto-reboot is not used for drivers.");
                System.Windows.MessageBox.Show(
                    manager,
                    "Driver installation finished.\n\nA restart may be required. Restart Windows when convenient.\n\nOptional auto-reboot applies only to Critical/General updates.",
                    "Update Express",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
        };

        manager.ShowDialog();
    }
}
