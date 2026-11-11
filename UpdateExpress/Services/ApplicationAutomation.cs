using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace UpdateExpress.Services;

/// <summary>
/// Optional WinGet applications automation: scan upgrades → ask before install → elevated winget upgrade.
/// Mirrors Critical/General download+prompt timing (preferred time from Critical policy). No auto-reboot.
/// </summary>
internal static class ApplicationAutomation
{
    private const string Category = "applications";
    private const string DisplayName = "Applications";
    private const string ElevateArg = "--apply-applications-install";
    private const int MinIntervalMinutes = 1440;

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
            if (!policy.WindowsAutomation.Enabled || !policy.AutomaticApplications)
            {
                WindowsAutomationStatus.WriteCategory(Category, "Disabled", "Application automation is turned off in policy.", null);
                return;
            }

            var schedule = policy.WindowsAutomation.Critical;
            string preferred = AutomationSchedule.PreferredOrDefault(schedule.PreferredInstallTime);
            var prompt = CategoryPromptState.Load(Category);

            if (prompt.State == "installing")
                return;

            if (prompt.State == "scheduled" &&
                prompt.ScheduledInstallAt is DateTime scheduled &&
                scheduled <= DateTime.Now &&
                prompt.Updates.Count > 0)
            {
                await PresentPromptAsync(dispatcher, prompt, preferred,
                    "Scheduled application updates are due.").ConfigureAwait(false);
                return;
            }

            if (prompt.SnoozeUntil is DateTime snooze && snooze > DateTime.Now)
                return;

            if (prompt.State == "awaitingPrompt" && prompt.Updates.Count > 0)
            {
                await PresentPromptAsync(dispatcher, prompt, preferred,
                    "Application updates are waiting for confirmation.").ConfigureAwait(false);
                return;
            }

            var status = WindowsAutomationStatus.Load();
            var entry = status.Applications;
            DateTime nextDue = entry.NextDue ?? AutomationSchedule.NextPreferredOccurrence(preferred);
            if (entry.LastRun is DateTime last && entry.NextDue is null)
                nextDue = NextDue(last, schedule, preferred);
            if (DateTime.Now < nextDue && entry.LastRun is not null)
                return;

            AgentLog.Write("Applications automation: starting scheduled WinGet scan.");
            ActivityLog.TryWrite("ApplicationsAutomation", "Started", new { Mode = "downloadAndPrompt" }, mode: "Automatic", source: "TrayAgent");

            WingetScanResult scan;
            try
            {
                scan = await WingetReader.ScanAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WindowsAutomationStatus.WriteCategory(Category, "Failed", "Scan failed: " + ex.Message, NextDue(DateTime.Now, schedule, preferred));
                ActivityLog.TryWrite("ApplicationsAutomation", "ScanFailed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
                AgentLog.Write("Applications automation scan failed: " + ex);
                return;
            }

            var allowIds = policy.AutomaticApplicationIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matches = scan.Updates
                .Where(app => allowIds.Count == 0 || allowIds.Contains(app.Id))
                .ToList();

            if (matches.Count == 0)
            {
                CategoryPromptState.Clear(Category);
                WindowsAutomationStatus.WriteCategory(
                    Category, "None pending", "No WinGet application upgrades pending.",
                    NextDue(DateTime.Now, schedule, preferred));
                ActivityLog.TryWrite("ApplicationsAutomation", "NonePending", new { Count = 0 }, mode: "Automatic", source: "TrayAgent");
                AgentLog.Write("Applications automation: no matching application upgrades pending.");
                return;
            }

            // WinGet has no separate pre-download phase; install downloads when confirmed.
            prompt = new CategoryPromptState
            {
                Category = Category,
                CreatedAt = DateTime.Now,
                Downloaded = false,
                State = "awaitingPrompt",
                Updates = matches.Select(m => new PromptUpdateItem
                {
                    UpdateId = m.Id,
                    KB = "",
                    Title = $"{m.DisplayName} ({m.Installed} → {m.Available})",
                    Priority = m.Priority
                }).ToList()
            };
            prompt.Save();

            WindowsAutomationStatus.WriteCategory(
                Category,
                "Awaiting confirmation",
                $"{matches.Count} application upgrade(s) ready",
                NextDue(DateTime.Now, schedule, preferred));

            await PresentPromptAsync(dispatcher, prompt, preferred,
                $"{matches.Count} application upgrade(s) available via WinGet.").ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static DateTime NextDue(DateTime from, CategoryAutomationPolicy cat, string preferred) =>
        AutomationSchedule.ComputeNextDue(from, cat.IntervalMinutes, preferred, MinIntervalMinutes);

    private static async Task PresentPromptAsync(
        Dispatcher dispatcher,
        CategoryPromptState prompt,
        string preferred,
        string balloonText)
    {
        try
        {
            try { _balloon?.Invoke("Update Express – Application updates", balloonText); }
            catch { }

            AgentLog.Write($"Applications automation: presenting confirmation dialog ({prompt.Updates.Count}).");
            var decision = await dispatcher.InvokeAsync(() =>
            {
                var dialog = new SoftwareUpdatesDialog(DisplayName, prompt.Updates, preferred, prompt.Downloaded);
                dialog.ShowDialog();
                return dialog.Decision;
            }).Task.ConfigureAwait(false);

            await HandleDecisionAsync(decision, prompt, preferred).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentLog.Write("Applications automation prompt failed: " + ex);
            ActivityLog.TryWrite("ApplicationsAutomation", "PromptFailed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
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
                ActivityLog.TryWrite("ApplicationsAutomation", "Scheduled", new { At = when }, mode: "Automatic", source: "TrayAgent");
                AgentLog.Write("Applications automation: install scheduled for " + when.ToString("O"));
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
            AgentLog.Write("Applications automation: install requested but no package IDs.");
            return Task.CompletedTask;
        }

        prompt.State = "installing";
        prompt.Save();
        prompt.WriteInstallQueue(ids);
        ActivityLog.TryWrite("ApplicationsAutomation", "InstallRequested", new { Count = ids.Count }, mode: "Automatic", source: "TrayAgent");
        AgentLog.Write($"Applications automation: launching elevated WinGet install for {ids.Count} package(s).");

        try
        {
            ElevationHelper.RestartElevated(ElevateArg);
            WindowsAutomationStatus.WriteCategory(
                Category, "Installing",
                "Elevated WinGet installer started. Update Express will not reboot Windows.",
                NextDue(DateTime.Now, schedule, preferred));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            prompt.State = "awaitingPrompt";
            prompt.Save();
            WindowsAutomationStatus.WriteCategory(Category, "UAC cancelled", "Administrator approval was cancelled.", DateTime.Now.AddHours(1));
            AgentLog.Write("Applications automation: UAC cancelled for install.");
        }
        catch (Exception ex)
        {
            prompt.State = "awaitingPrompt";
            prompt.Save();
            WindowsAutomationStatus.WriteCategory(Category, "Failed", "Could not start elevated install: " + ex.Message, DateTime.Now.AddHours(1));
            AgentLog.Write("Applications automation elevated launch failed: " + ex);
        }

        return Task.CompletedTask;
    }

    internal static void ApplyQueuedInstall()
    {
        var ids = CategoryPromptState.ReadInstallQueue(Category);
        if (ids.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "No application updates were queued for installation.",
                "Update Express",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        if (!ElevationHelper.IsElevated)
        {
            System.Windows.MessageBox.Show(
                "Administrator privileges are required to install application updates.",
                "Update Express",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var prompt = CategoryPromptState.Load(Category);
        var queue = ids.Select(id =>
        {
            var meta = prompt.Updates.FirstOrDefault(u =>
                u.UpdateId.Equals(id, StringComparison.OrdinalIgnoreCase));
            string title = meta?.Title ?? id;
            return new InstallationQueueItem("WinGet", title);
        }).ToList();

        AgentLog.Write($"Applications install helper: installing {ids.Count} queued WinGet package(s).");
        var manager = new InstallationManagerWindow(queue)
        {
            Title = "Update Express – Applications installation",
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
        };

        manager.Loaded += async (_, _) =>
        {
            manager.PrepareRunning();
            int ok = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                manager.BeginItem(i, "Installing " + id + "…");
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
                    var (code, output) = await WingetReader.UpgradeAsync(id, timeout.Token).ConfigureAwait(true);
                    AgentLog.Write($"Applications install {id}: exit={code}");
                    if (!string.IsNullOrWhiteSpace(output))
                        AgentLog.Write("Applications install output: " + output.Trim());
                    if (code == 0) ok++;
                    manager.CompleteItem(i, code == 0 ? "Completed" : $"Failed (exit {code})", code != 0);
                }
                catch (Exception ex)
                {
                    AgentLog.Write("Applications install item failed: " + ex);
                    manager.CompleteItem(i, "Failed", true);
                }
            }

            CategoryPromptState.ClearInstallQueue(Category);
            CategoryPromptState.Clear(Category);

            var policy = UpdatePolicy.Load();
            policy.Normalize();
            var schedule = policy.WindowsAutomation.Critical;
            string preferred = AutomationSchedule.PreferredOrDefault(schedule.PreferredInstallTime);
            WindowsAutomationStatus.WriteCategory(
                Category,
                "Installed",
                $"{ok}/{ids.Count} completed.",
                NextDue(DateTime.Now, schedule, preferred));
            ActivityLog.TryWrite("ApplicationsAutomation", "Installed", new { ok, Total = ids.Count }, mode: "Automatic", source: "ElevatedHelper");
            manager.Finish();
        };

        manager.ShowDialog();
    }
}
