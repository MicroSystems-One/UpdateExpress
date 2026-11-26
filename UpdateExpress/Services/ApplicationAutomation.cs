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
/// Confirmed multi-app queues persist remaining package IDs across reboot and resume automatically
/// when Application automatic is on; reminder auto-reboot is held while a queue is in flight.
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
    private static readonly TimeSpan ResumeThrottle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HelperInFlightGrace = TimeSpan.FromMinutes(25);

    private static int _running;
    private static Action<string, string>? _balloon;
    private const string HelperMutexName = @"Local\MicroSystems.UpdateExpress.ApplicationsInstall";

    internal static void SetBalloon(Action<string, string> balloon) => _balloon = balloon;

    /// <summary>
    /// Reminder / deferred reboot must wait when a confirmed app queue is installing or
    /// when Applications automation is mid scan/prompt (same tray poll race).
    /// </summary>
    internal static bool ShouldHoldExternalReboot()
    {
        if (Volatile.Read(ref _running) != 0)
            return true;

        var queue = CategoryPromptState.ReadInstallQueueState(Category);
        if (queue.UpdateIds.Count == 0)
            return false;

        if (queue.ResumeAfterReboot)
            return true;

        var prompt = CategoryPromptState.Load(Category);
        return string.Equals(prompt.State, "installing", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// After sign-in / wake: relaunch the elevated helper for remaining confirmed packages.
    /// ResumeAfterReboot waits for a real new OS boot; crash recovery may resume same boot
    /// only after the live-helper grace window.
    /// </summary>
    internal static bool TryResumeAfterReboot()
    {
        var policy = UpdatePolicy.Load();
        policy.Normalize();
        if (!policy.WindowsAutomation.Enabled || !policy.AutomaticApplications)
            return false;

        var queue = CategoryPromptState.ReadInstallQueueState(Category);
        if (queue.UpdateIds.Count == 0)
            return false;

        var prompt = CategoryPromptState.Load(Category);
        bool installing = string.Equals(prompt.State, "installing", StringComparison.OrdinalIgnoreCase);
        if (!queue.ResumeAfterReboot && !installing)
            return false;

        DateTime currentBoot = CategoryPromptState.ApproximateBootTime();

        if (queue.ResumeAfterReboot)
        {
            // Wait until Windows actually rebooted after the block stamp.
            if (queue.ResumeBlockedAtBoot is DateTime blocked &&
                CategoryPromptState.IsSameBoot(blocked, currentBoot))
                return false;

            if (queue.ResumeBlockedAtBoot is null &&
                CategoryPromptState.StartedDuringCurrentBoot(prompt.InstallingStartedAt))
                return false;
        }
        else
        {
            // Same-boot crash recovery: never interrupt a live helper that started this boot.
            if (CategoryPromptState.StartedDuringCurrentBoot(prompt.InstallingStartedAt) &&
                prompt.InstallingStartedAt is DateTime started &&
                DateTime.Now - started < HelperInFlightGrace)
                return false;
        }

        if (queue.LastResumeLaunch is DateTime last && DateTime.Now - last < ResumeThrottle)
            return false;

        // Another elevated helper already owns the queue.
        if (IsHelperMutexHeld())
            return false;

        queue.LastResumeLaunch = DateTime.Now;
        CategoryPromptState.WriteInstallQueueState(queue);

        if (!installing)
        {
            prompt.MarkInstalling();
            prompt.Save();
        }

        try
        {
            AgentLog.Write(
                $"Applications automation: resuming {queue.UpdateIds.Count} remaining package(s)" +
                (queue.ResumeAfterReboot ? " after reboot." : " after interrupted helper."));
            ActivityLog.TryWrite(
                "ApplicationsAutomation",
                "ResumeRequested",
                new { Count = queue.UpdateIds.Count, queue.ResumeAfterReboot },
                mode: "Automatic",
                source: "TrayAgent");
            ElevationHelper.RestartElevated(AutomationStatePaths.ElevateArguments(ElevateArg));
            WindowsAutomationStatus.WriteCategory(
                Category,
                "Resuming",
                $"Resuming {queue.UpdateIds.Count} application upgrade(s).",
                NextScanDue(DateTime.Now, policy));
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AgentLog.Write("Applications automation: UAC cancelled for resume.");
            WindowsAutomationStatus.WriteCategory(
                Category, "UAC cancelled", "Administrator approval was cancelled for resume.", DateTime.Now.AddHours(1));
            return false;
        }
        catch (Exception ex)
        {
            AgentLog.Write("Applications automation resume launch failed: " + ex);
            return false;
        }
    }

    private static bool IsHelperMutexHeld()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, HelperMutexName);
            if (!mutex.WaitOne(0))
                return true;
            mutex.ReleaseMutex();
            return false;
        }
        catch (AbandonedMutexException)
        {
            try { mutex?.ReleaseMutex(); } catch { /* ignore */ }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { mutex?.Dispose(); } catch { /* ignore */ }
        }
    }

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

            // Prefer finishing a confirmed queue over scanning / prompting again.
            if (TryResumeAfterReboot())
                return;

            string preferred = AutomationSchedule.PreferredOrDefault(policy.WindowsAutomation.Critical.PreferredInstallTime);
            var prompt = CategoryPromptState.LoadAndReconcile(Category);

            var queue = CategoryPromptState.ReadInstallQueueState(Category);
            if (string.Equals(prompt.State, "installing", StringComparison.OrdinalIgnoreCase))
            {
                // Still installing with remaining IDs: wait for helper / resume path.
                if (queue.UpdateIds.Count > 0)
                    return;
                // Empty queue while installing — reconcile treats abandoned state after timeout.
                return;
            }

            if (prompt.State == "scheduled" &&
                prompt.ScheduledInstallAt is DateTime scheduled &&
                scheduled <= DateTime.Now &&
                prompt.Updates.Count > 0)
            {
                // Unattended «update until clear»: install the confirmed queue without a second dialog.
                if (UnattendedUntilClear.ShouldAutoInstallScheduledApps())
                {
                    if (UnattendedUntilClear.TryDeferForActiveSession(prompt, DisplayName))
                        return;
                    AgentLog.Write("Applications automation: unattended scheduled install — starting without re-prompt.");
                    UnattendedUntilClear.MarkInstalling(DisplayName);
                    await StartElevatedInstallAsync(prompt).ConfigureAwait(false);
                    return;
                }

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

            if (SoftwareUpdatesDialog.IsShowing)
            {
                AgentLog.Write("Applications automation: another updates-ready dialog is open — asking again once it has closed.");
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
                // Prefer policy install window (e.g. Mon–Fri 17:00) when set; else Critical preferred clock.
                DateTime when = MaintenanceSetup.NextInstallOccurrence(policy.InstallDaysMask, policy.InstallTime, DateTime.Now)
                    ?? AutomationSchedule.NextPreferredOccurrence(preferred);
                prompt.State = "scheduled";
                prompt.ScheduledInstallAt = when;
                prompt.SnoozeUntil = when;
                prompt.Save();
                UnattendedUntilClear.Arm(when, Category);
                AutomationStatePaths.PublishSharedScheduleNotice(
                    when, DisplayName,
                    $"Install at {when:HH:mm} — applications (all users / machine scope).");
                WindowsAutomationStatus.WriteCategory(
                    Category, "Scheduled",
                    $"Install scheduled for {when:dd.MM HH:mm}. Unattended until clear after that.", when);
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

        // Hold any armed reminder countdown so it cannot cut the confirmed multi-app queue short.
        try { RebootCoordinator.CancelCountdownReboot("applications install starting"); }
        catch { /* best effort */ }
        var pendingReboot = RebootCoordinator.LoadPending();
        if (pendingReboot is not null &&
            !pendingReboot.Cancelled &&
            string.Equals(pendingReboot.Source, "reminder", StringComparison.OrdinalIgnoreCase))
        {
            RebootCoordinator.SchedulePending(
                "reminder",
                DateTime.Now.AddMinutes(30),
                (pendingReboot.Reason ?? "") + " (waiting for application installs)");
        }

        prompt.MarkInstalling();
        prompt.Save();
        CategoryPromptState.WriteInstallQueueState(new InstallQueueState
        {
            Category = Category,
            CreatedAt = DateTime.Now,
            UpdateIds = ids,
            CompletedIds = new(),
            ResumeAfterReboot = false
        });
        ActivityLog.TryWrite("ApplicationsAutomation", "InstallRequested", new { Count = ids.Count }, mode: "Automatic", source: "TrayAgent");
        AgentLog.Write($"Applications automation: launching elevated WinGet install for {ids.Count} package(s).");

        try
        {
            ElevationHelper.RestartElevated(AutomationStatePaths.ElevateArguments(ElevateArg));
            WindowsAutomationStatus.WriteCategory(
                Category, "Installing",
                "Elevated WinGet installer started. Remaining packages resume after reboot when required.",
                NextScanDue(DateTime.Now));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            prompt.State = "awaitingPrompt";
            prompt.InstallingStartedAt = null;
            prompt.Save();
            CategoryPromptState.ClearInstallQueue(Category);
            WindowsAutomationStatus.WriteCategory(Category, "UAC cancelled", "Administrator approval was cancelled.", DateTime.Now.AddHours(1));
            AgentLog.Write("Applications automation: UAC cancelled for install.");
        }
        catch (Exception ex)
        {
            prompt.State = "awaitingPrompt";
            prompt.InstallingStartedAt = null;
            prompt.Save();
            CategoryPromptState.ClearInstallQueue(Category);
            WindowsAutomationStatus.WriteCategory(Category, "Failed", "Could not start elevated install: " + ex.Message, DateTime.Now.AddHours(1));
            AgentLog.Write("Applications automation elevated launch failed: " + ex);
        }

        return Task.CompletedTask;
    }

    internal static void ApplyQueuedInstall()
    {
        Mutex? helperMutex = null;
        bool ownsMutex = false;
        try
        {
            helperMutex = new Mutex(false, HelperMutexName);
            try { ownsMutex = helperMutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
            {
                System.Windows.MessageBox.Show(
                    "An Applications install helper is already running.",
                    "Update Express",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var queueState = CategoryPromptState.ReadInstallQueueState(Category);
            var ids = queueState.UpdateIds;
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

            bool wasPostRebootResume = queueState.ResumeAfterReboot;
            // Clear resume gate for this elevated pass; re-set if we must reboot mid-queue.
            queueState.ResumeAfterReboot = false;
            queueState.ResumeBlockedAtBoot = null;
            queueState.LastResumeLaunch = null;
            CategoryPromptState.WriteInstallQueueState(queueState);

            // Do not cancel a countdown we just armed for remaining packages; only cancel
            // unrelated reminder countdowns when starting a fresh / post-reboot pass.
            if (!wasPostRebootResume)
            {
                try { RebootCoordinator.CancelCountdownReboot("applications elevated install running"); }
                catch { /* best effort */ }
            }

            var prompt = CategoryPromptState.Load(Category);
            prompt.MarkInstalling();
            prompt.Save();

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
                bool rebootRequired = false;
                string? rebootPackageId = null;
                var remainingAfterReboot = new List<string>();

                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i];
                    manager.BeginItem(i, "Installing " + id + "…");
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
                        // Automated installs: machine scope (all users) — never per-user.
                        var (code, output) = await WingetReader.UpgradeAsync(id, timeout.Token, machineScope: true).ConfigureAwait(true);
                        AgentLog.Write($"Applications install {id}: exit={code}");
                        if (!string.IsNullOrWhiteSpace(output))
                            AgentLog.Write("Applications install output: " + output.Trim());

                        bool osReboot = WingetReader.IndicatesOsRebootRequired(code, output);
                        string itemTitle = queue[i].Name;
                        if (code == 0 || osReboot)
                        {
                            ok++;
                            manager.CompleteItem(i, osReboot ? "Completed (restart needed)" : "Completed", false);
                            CategoryPromptState.MarkInstallQueueItemDone(Category, id, success: true);
                            UnattendedUntilClear.RecordItem(id, itemTitle, DisplayName, success: true,
                                detail: osReboot ? "Completed (restart needed)" : "Completed");
                        }
                        else
                        {
                            bool norwegian = AppLanguage.IsNorwegian;
                            var (state, tip, notice) = WingetReader.DescribeUpgradeFailure(code, id, norwegian);
                            manager.CompleteItem(i, state, true, tip);
                            CategoryPromptState.MarkInstallQueueItemDone(Category, id, success: false);
                            UnattendedUntilClear.RecordItem(id, itemTitle, DisplayName, success: false,
                                detail: state + (string.IsNullOrWhiteSpace(tip) ? "" : " — " + tip));
                            if (!string.IsNullOrWhiteSpace(notice))
                            {
                                manager.SetNotice(notice);
                                manager.SetStage(notice);
                            }
                        }

                        if (osReboot)
                        {
                            rebootRequired = true;
                            rebootPackageId = id;
                            // Remaining packages (not yet attempted) stay queued for post-reboot resume.
                            remainingAfterReboot = ids.Skip(i + 1).ToList();
                            for (int skip = i + 1; skip < ids.Count; skip++)
                                manager.CompleteItem(skip, "Deferred until after restart", false);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        AgentLog.Write("Applications install item failed: " + ex);
                        manager.CompleteItem(i, "Failed", true);
                        CategoryPromptState.MarkInstallQueueItemDone(Category, id, success: false);
                        UnattendedUntilClear.RecordItem(id, queue[i].Name, DisplayName, success: false, detail: ex.Message);
                    }
                }

                var policy = UpdatePolicy.Load();
                policy.Normalize();

                if (rebootRequired && remainingAfterReboot.Count > 0)
                {
                    CategoryPromptState.WriteInstallQueueState(new InstallQueueState
                    {
                        Category = Category,
                        CreatedAt = DateTime.Now,
                        UpdateIds = remainingAfterReboot,
                        CompletedIds = CategoryPromptState.ReadInstallQueueState(Category).CompletedIds,
                        ResumeAfterReboot = true,
                        ResumeBlockedAtBoot = CategoryPromptState.ApproximateBootTime()
                    });
                    var stillInstalling = CategoryPromptState.Load(Category);
                    stillInstalling.MarkInstalling();
                    stillInstalling.Save();

                    WindowsAutomationStatus.WriteCategory(
                        Category,
                        "Installed (restart needed)",
                        $"{ok} completed · {remainingAfterReboot.Count} resume after restart ({rebootPackageId}).",
                        NextScanDue(DateTime.Now, policy));
                    ActivityLog.TryWrite(
                        "ApplicationsAutomation",
                        "Installed",
                        new { ok, Total = ids.Count, rebootHint = true, remaining = remainingAfterReboot.Count, rebootPackageId },
                        mode: "Automatic",
                        source: "ElevatedHelper");
                    try { HistoryArchive.RecordInstallationManagerQueue(queue); }
                    catch (Exception ex) { AgentLog.Write("Applications install: history record failed: " + ex.Message); }

                    manager.Finish();
                    manager.SetStage(
                        policy.WindowsAutomation.AutoReboot.Enabled
                            ? $"Finished — restart required before remaining {remainingAfterReboot.Count} package(s). Auto-reboot policy will run next…"
                            : $"Finished — restart required before remaining {remainingAfterReboot.Count} package(s). Auto-reboot is disabled in policy.");
                    AgentLog.Write(
                        $"Applications: reboot required after {rebootPackageId}; {remainingAfterReboot.Count} package(s) queued for resume.");
                    UnattendedUntilClear.OnInstallHelperFinished(DisplayName, remainingAfterReboot: true);
                    bool rebootArmed = RebootCoordinator.HandleAfterInstall(DisplayName, rebootRequired: true, owner: manager);
                    if (!rebootArmed)
                    {
                        // Declined / auto-reboot off: allow same-boot retry instead of waiting forever.
                        var soften = CategoryPromptState.ReadInstallQueueState(Category);
                        soften.ResumeAfterReboot = false;
                        soften.ResumeBlockedAtBoot = null;
                        CategoryPromptState.WriteInstallQueueState(soften);
                        AgentLog.Write("Applications: reboot not armed; remaining packages may resume this session.");
                        UnattendedUntilClear.OnInstallHelperFinished(DisplayName, remainingAfterReboot: false);
                    }
                    return;
                }

                if (rebootRequired)
                {
                    CategoryPromptState.ClearInstallQueue(Category);
                    CategoryPromptState.Clear(Category);
                    WindowsAutomationStatus.WriteCategory(
                        Category,
                        "Installed (restart needed)",
                        $"{ok}/{ids.Count} completed. Restart may be required.",
                        NextScanDue(DateTime.Now, policy));
                    ActivityLog.TryWrite(
                        "ApplicationsAutomation",
                        "Installed",
                        new { ok, Total = ids.Count, rebootHint = true },
                        mode: "Automatic",
                        source: "ElevatedHelper");
                    try { HistoryArchive.RecordInstallationManagerQueue(queue); }
                    catch (Exception ex) { AgentLog.Write("Applications install: history record failed: " + ex.Message); }
                    manager.Finish();
                    manager.SetStage(
                        policy.WindowsAutomation.AutoReboot.Enabled
                            ? "Finished — restart required. Optional auto-reboot policy will run next…"
                            : "Finished — a restart may be required. Auto-reboot is disabled in policy.");
                    UnattendedUntilClear.OnInstallHelperFinished(DisplayName, remainingAfterReboot: false);
                    RebootCoordinator.HandleAfterInstall(DisplayName, rebootRequired: true, owner: manager);
                    return;
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

                WindowsAutomationStatus.WriteCategory(
                    Category,
                    "Installed",
                    $"{ok}/{ids.Count} completed.",
                    NextScanDue(DateTime.Now, policy));
                ActivityLog.TryWrite("ApplicationsAutomation", "Installed", new { ok, Total = ids.Count, rebootHint = false }, mode: "Automatic", source: "ElevatedHelper");
                try { HistoryArchive.RecordInstallationManagerQueue(queue); }
                catch (Exception ex) { AgentLog.Write("Applications install: history record failed: " + ex.Message); }
                manager.Finish();
                UnattendedUntilClear.OnInstallHelperFinished(DisplayName, remainingAfterReboot: false);
            };

            manager.ShowDialog();
        }
        finally
        {
            if (ownsMutex)
            {
                try { helperMutex?.ReleaseMutex(); } catch { /* ignore */ }
            }
            try { helperMutex?.Dispose(); } catch { /* ignore */ }
        }
    }
}
