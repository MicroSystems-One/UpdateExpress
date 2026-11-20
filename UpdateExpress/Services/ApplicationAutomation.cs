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
/// Preferred install time still comes from Critical policy (Install at HH:mm). Scan cadence uses
/// ApplicationsIntervalMinutes (default 240, floor 60) — not the Critical once-per-day snap.
/// </summary>
internal static class ApplicationAutomation
{
    private const string Category = "applications";
    private const string DisplayName = "Applications";
    private const string ElevateArg = "--apply-applications-install";
    /// <summary>Default scan interval when policy has no ApplicationsIntervalMinutes yet.</summary>
    internal const int DefaultIntervalMinutes = 240;
    /// <summary>Hard floor for Applications scan cadence.</summary>
    internal const int AbsoluteFloorMinutes = 60;

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

            string preferred = AutomationSchedule.PreferredOrDefault(policy.WindowsAutomation.Critical.PreferredInstallTime);
            var prompt = CategoryPromptState.LoadAndReconcile(Category);

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
            DateTime nextDue = entry.NextDue ?? NextScanDue(DateTime.Now.AddMinutes(-ResolveIntervalMinutes(policy)));
            if (entry.LastRun is DateTime last && entry.NextDue is null)
                nextDue = NextScanDue(last, policy);
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
                WindowsAutomationStatus.WriteCategory(Category, "Failed", "Scan failed: " + ex.Message, NextScanDue(DateTime.Now, policy));
                ActivityLog.TryWrite("ApplicationsAutomation", "ScanFailed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
                AgentLog.Write("Applications automation scan failed: " + ex);
                return;
            }

            // Full WinGet list for main UI; automation prompt may filter by allow-list.
            WingetAvailableCache.Write(scan.Updates, scan.Raw);

            var allowIds = policy.AutomaticApplicationIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matches = scan.Updates
                .Where(app => allowIds.Count == 0 || allowIds.Contains(app.Id))
                .ToList();

            if (matches.Count == 0)
            {
                CategoryPromptState.Clear(Category);
                WindowsAutomationStatus.WriteCategory(
                    Category, "None pending", "No WinGet application upgrades pending.",
                    NextScanDue(DateTime.Now, policy));
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
                NextScanDue(DateTime.Now, policy));

            await PresentPromptAsync(dispatcher, prompt, preferred,
                $"{matches.Count} application upgrade(s) available via WinGet.").ConfigureAwait(false);
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
        return Math.Max(AbsoluteFloorMinutes, policy.ApplicationsIntervalMinutes);
    }

    /// <summary>Interval-based scan cadence (no preferred-time snap). Preferred time is only for Install at HH:mm.</summary>
    private static DateTime NextScanDue(DateTime from, UpdatePolicy? policy = null) =>
        from.AddMinutes(ResolveIntervalMinutes(policy));

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
                    "Applications automation: updates-ready prompt suppressed " +
                    "(Påminnelse covers pending updates in this Main UI session).");
                return;
            }

            try { _balloon?.Invoke("Update Express – Application updates", balloonText); }
            catch { }

            AgentLog.Write($"Applications automation: presenting confirmation dialog ({prompt.Updates.Count}).");
            var shown = await dispatcher.InvokeAsync(() =>
            {
                if (!SoftwareUpdatesDialog.TryShowModal(
                        DisplayName, prompt.Updates, preferred, prompt.Downloaded, out var decision))
                {
                    AgentLog.Write("Applications automation: updates-ready dialog already open — skip stack.");
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

        switch (decision)
        {
            case SoftwareUpdatesDialog.PromptDecision.InstallNow:
                return StartElevatedInstallAsync(prompt);

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

    private static Task StartElevatedInstallAsync(CategoryPromptState prompt)
    {
        var ids = prompt.Updates.Select(u => u.UpdateId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count == 0)
        {
            AgentLog.Write("Applications automation: install requested but no package IDs.");
            return Task.CompletedTask;
        }

        prompt.MarkInstalling();
        prompt.Save();
        prompt.WriteInstallQueue(ids);
        ActivityLog.TryWrite("ApplicationsAutomation", "InstallRequested", new { Count = ids.Count }, mode: "Automatic", source: "TrayAgent");
        AgentLog.Write($"Applications automation: launching elevated WinGet install for {ids.Count} package(s).");

        try
        {
            ElevationHelper.RestartElevated(AutomationStatePaths.ElevateArguments(ElevateArg));
            WindowsAutomationStatus.WriteCategory(
                Category, "Installing",
                "Elevated WinGet installer started. Update Express will not reboot Windows.",
                NextScanDue(DateTime.Now));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            prompt.State = "awaitingPrompt";
            prompt.InstallingStartedAt = null;
            prompt.Save();
            WindowsAutomationStatus.WriteCategory(Category, "UAC cancelled", "Administrator approval was cancelled.", DateTime.Now.AddHours(1));
            AgentLog.Write("Applications automation: UAC cancelled for install.");
        }
        catch (Exception ex)
        {
            prompt.State = "awaitingPrompt";
            prompt.InstallingStartedAt = null;
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
                    if (code == 0)
                    {
                        ok++;
                        manager.CompleteItem(i, "Completed", false);
                    }
                    else
                    {
                        bool norwegian = AppLanguage.IsNorwegian;
                        var (state, tip, notice) = WingetReader.DescribeUpgradeFailure(code, id, norwegian);
                        manager.CompleteItem(i, state, true, tip);
                        if (!string.IsNullOrWhiteSpace(notice))
                        {
                            manager.SetNotice(notice);
                            manager.SetStage(notice);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AgentLog.Write("Applications install item failed: " + ex);
                    manager.CompleteItem(i, "Failed", true);
                }
            }

            CategoryPromptState.ClearInstallQueue(Category);
            CategoryPromptState.Clear(Category);

            try
            {
                var rescan = await WingetReader.ScanAsync().ConfigureAwait(true);
                WingetAvailableCache.Write(rescan.Updates, rescan.Raw);
            }
            catch (Exception ex)
            {
                // Keep the previous winget-available snapshot — do not publish an empty list on scan failure.
                AgentLog.Write("Applications install helper: post-install WinGet rescan failed: " + ex.Message);
            }

            var policy = UpdatePolicy.Load();
            policy.Normalize();
            WindowsAutomationStatus.WriteCategory(
                Category,
                "Installed",
                $"{ok}/{ids.Count} completed.",
                NextScanDue(DateTime.Now, policy));
            ActivityLog.TryWrite("ApplicationsAutomation", "Installed", new { ok, Total = ids.Count }, mode: "Automatic", source: "ElevatedHelper");
            manager.Finish();
        };

        manager.ShowDialog();
    }
}
