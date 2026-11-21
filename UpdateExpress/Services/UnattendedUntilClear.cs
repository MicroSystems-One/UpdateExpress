using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

/// <summary>
/// Unattended «update until clear»: after the user confirms Install at HH:mm,
/// install the confirmed queue at the scheduled time, resume across reboot,
/// rescan for newly offered updates, and continue until none remain (or only
/// failures recorded in this run's skip list). Shows a result summary when the
/// user returns.
/// </summary>
internal static class UnattendedUntilClear
{
    internal const int DefaultMaxRounds = 12;
    internal const int SoftIdleMinutes = 10;
    internal const int ActiveDeferMinutes = 15;
    internal const int ResultQuietMinutes = 30;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static int _continueRunning;
    private static int _resultShowing;

    private static string PathFile =>
        Path.Combine(InstallationManager.InstallDir, "unattended-until-clear.json");

    internal static UnattendedUntilClearState Load()
    {
        try
        {
            if (!File.Exists(PathFile)) return new UnattendedUntilClearState();
            return JsonSerializer.Deserialize<UnattendedUntilClearState>(File.ReadAllText(PathFile))
                   ?? new UnattendedUntilClearState();
        }
        catch
        {
            return new UnattendedUntilClearState();
        }
    }

    internal static void Save(UnattendedUntilClearState state)
    {
        Directory.CreateDirectory(InstallationManager.InstallDir);
        File.WriteAllText(PathFile, JsonSerializer.Serialize(state, Json));
    }

    internal static bool IsRunActive(UnattendedUntilClearState? state = null)
    {
        state ??= Load();
        if (!state.Active) return false;
        return state.Phase is "armed" or "installing" or "clearing" or "reboot";
    }

    /// <summary>
    /// Applications scheduled due should elevate immediately (no second dialog)
    /// while an unattended run is armed or clearing.
    /// </summary>
    internal static bool ShouldAutoInstallScheduledApps(UnattendedUntilClearState? state = null)
    {
        state ??= Load();
        return IsRunActive(state);
    }

    internal static bool IsSkipped(string? id, UnattendedUntilClearState? state = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        state ??= Load();
        return state.SkipIds.Any(s => s.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Arm or refresh the overnight run when the user chooses Install at preferred time.</summary>
    internal static void Arm(DateTime scheduledAt, string sourceCategory)
    {
        var state = Load();
        bool fresh = !state.Active || state.Phase is "complete" or "stopped" or "idle" || state.ResultPending;

        if (fresh)
        {
            state = new UnattendedUntilClearState
            {
                Active = true,
                Phase = "armed",
                ArmedAt = DateTime.Now,
                ScheduledStartAt = scheduledAt,
                MaxRounds = DefaultMaxRounds,
                Round = 0,
                SkipIds = new(),
                Successful = new(),
                Failed = new(),
                ConfirmedCategories = new(),
                ResultPending = false,
                ShowResultAfter = null,
                CompletedAt = null,
                Message = $"Armed for {scheduledAt:dd.MM HH:mm} ({sourceCategory}).",
                ResumeAfterReboot = false,
                LastContinueAt = null,
                SoftDeferCount = 0
            };
        }
        else
        {
            if (state.ScheduledStartAt is null || scheduledAt < state.ScheduledStartAt)
                state.ScheduledStartAt = scheduledAt;
            state.Message = $"Armed for {state.ScheduledStartAt:dd.MM HH:mm} (+{sourceCategory}).";
        }

        if (!state.ConfirmedCategories.Any(c => c.Equals(sourceCategory, StringComparison.OrdinalIgnoreCase)))
            state.ConfirmedCategories.Add(sourceCategory);

        Save(state);
        AgentLog.Write($"Unattended until-clear: armed ({sourceCategory}) for {scheduledAt:O}.");
        ActivityLog.TryWrite(
            "UnattendedUntilClear",
            "Armed",
            new { At = scheduledAt, Category = sourceCategory, state.ConfirmedCategories },
            mode: "Automatic",
            source: "TrayAgent");
    }

    internal static void MarkInstalling(string category)
    {
        var state = Load();
        if (!IsRunActive(state)) return;
        state.Phase = "installing";
        state.Message = $"Installing {category}…";
        Save(state);
    }

    /// <summary>
    /// After a confirmed or clearing install helper finishes. If packages remain for
    /// reboot resume, stay in reboot/installing; otherwise enter clearing rescans.
    /// </summary>
    internal static void OnInstallHelperFinished(string category, bool remainingAfterReboot)
    {
        var state = Load();
        if (!IsRunActive(state)) return;

        if (remainingAfterReboot)
        {
            state.Phase = "reboot";
            state.ResumeAfterReboot = true;
            state.Message = $"{category}: restart required; remaining packages resume after reboot.";
            Save(state);
            return;
        }

        state.ResumeAfterReboot = false;
        state.Phase = "clearing";
        state.Message = "Confirmed queue finished — rescanning until clear.";
        // Allow an immediate continue on the next tray tick.
        state.LastContinueAt = null;
        Save(state);
        AgentLog.Write($"Unattended until-clear: {category} finished → clearing.");
    }

    internal static void RecordItem(string id, string title, string source, bool success, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var state = Load();
        if (!IsRunActive(state) && !state.ResultPending) return;

        // De-dupe by id within the list we append to.
        if (success)
        {
            state.Failed.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (!state.Successful.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                state.Successful.Add(new UnattendedRunItem
                {
                    Id = id,
                    Title = title,
                    Source = source,
                    Detail = detail ?? "",
                    At = DateTime.Now
                });
            }
            // A later success clears a prior skip for this id (unusual but keep ledger consistent).
            state.SkipIds.RemoveAll(s => s.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            if (!state.SkipIds.Any(s => s.Equals(id, StringComparison.OrdinalIgnoreCase)))
                state.SkipIds.Add(id);
            state.Successful.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (!state.Failed.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                state.Failed.Add(new UnattendedRunItem
                {
                    Id = id,
                    Title = title,
                    Source = source,
                    Detail = detail ?? "Failed",
                    At = DateTime.Now
                });
            }
        }

        Save(state);
    }

    internal static void Complete(string message)
    {
        var state = Load();
        if (!state.Active && !state.ResultPending) return;

        state.Active = false;
        state.Phase = "complete";
        state.ResumeAfterReboot = false;
        state.Message = message;
        state.CompletedAt = DateTime.Now;
        state.ResultPending = true;
        // Prefer next calendar morning; never sooner than a quiet window after finish.
        var tomorrowMorning = DateTime.Today.AddDays(1).AddHours(7);
        var quiet = DateTime.Now.AddMinutes(ResultQuietMinutes);
        state.ShowResultAfter = tomorrowMorning > quiet ? tomorrowMorning : quiet;
        Save(state);
        AgentLog.Write("Unattended until-clear complete: " + message);
        ActivityLog.TryWrite(
            "UnattendedUntilClear",
            "Complete",
            new
            {
                state.Round,
                Ok = state.Successful.Count,
                Failed = state.Failed.Count,
                Skipped = state.SkipIds.Count,
                ShowAfter = state.ShowResultAfter
            },
            mode: "Automatic",
            source: "TrayAgent");
    }

    internal static void Stop(string message)
    {
        var state = Load();
        state.Active = false;
        state.Phase = "stopped";
        state.ResumeAfterReboot = false;
        state.Message = message;
        state.CompletedAt = DateTime.Now;
        if (state.Successful.Count > 0 || state.Failed.Count > 0)
        {
            state.ResultPending = true;
            state.ShowResultAfter = DateTime.Now.AddMinutes(ResultQuietMinutes);
        }
        Save(state);
        AgentLog.Write("Unattended until-clear stopped: " + message);
    }

    /// <summary>
    /// Soft session gate: recent keyboard/mouse activity → defer scheduled start.
    /// Not a full empty-desktop detector.
    /// </summary>
    internal static bool IsSessionQuiet(TimeSpan? idleFor = null)
    {
        idleFor ??= TimeSpan.FromMinutes(SoftIdleMinutes);
        try
        {
            var info = new LASTINPUTINFO { CbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info))
                return true; // fail open — do not block unattended
            uint ticks = unchecked((uint)Environment.TickCount) - info.DwTime;
            return TimeSpan.FromMilliseconds(ticks) >= idleFor.Value;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// When a scheduled install is due but the user is still active, push the schedule
    /// forward once per category call. Returns true if the caller should abort this tick.
    /// </summary>
    internal static bool TryDeferForActiveSession(CategoryPromptState prompt, string displayName)
    {
        var state = Load();
        if (!IsRunActive(state)) return false;
        if (state.Phase is not ("armed" or "installing")) return false;
        if (IsSessionQuiet()) return false;
        if (state.SoftDeferCount >= 8) return false; // ~2h of 15m slips — proceed anyway

        DateTime when = DateTime.Now.AddMinutes(ActiveDeferMinutes);
        prompt.State = "scheduled";
        prompt.ScheduledInstallAt = when;
        prompt.SnoozeUntil = when;
        prompt.Save();

        state.SoftDeferCount++;
        state.ScheduledStartAt = when;
        state.Message = $"Deferred {ActiveDeferMinutes}m — session still active ({displayName}).";
        Save(state);
        AgentLog.Write($"Unattended until-clear: soft-defer {displayName} to {when:O} (session active).");
        ActivityLog.TryWrite(
            "UnattendedUntilClear",
            "SoftDefer",
            new { Category = displayName, At = when, state.SoftDeferCount },
            mode: "Automatic",
            source: "TrayAgent");
        return true;
    }

    /// <summary>
    /// Tray poll: after confirmed queues finish, rescan Windows (IsInCycle) + Apps,
    /// skip failures from this run, and elevate the next batch without prompting.
    /// </summary>
    internal static async Task TryContinueClearingAsync(Dispatcher dispatcher)
    {
        if (Interlocked.CompareExchange(ref _continueRunning, 1, 0) != 0)
            return;

        try
        {
            var state = Load();
            if (!IsRunActive(state)) return;
            if (state.Phase is not ("clearing" or "armed"))
                return;

            // Let confirmed scheduled installs / reboot resumes finish first.
            if (HasInFlightConfirmedWork())
                return;

            // Armed: wait until scheduled start; category jobs install the confirmed queues.
            if (state.Phase == "armed")
            {
                if (state.ScheduledStartAt is DateTime start && start > DateTime.Now)
                    return;
                // If nothing was confirmed and schedule passed with no queues, begin clearing anyway.
                state.Phase = "clearing";
                state.Message = "Scheduled window reached — clearing rescans.";
                Save(state);
            }

            if (state.LastContinueAt is DateTime last && DateTime.Now - last < TimeSpan.FromMinutes(2))
                return;

            if (ApplicationAutomation.ShouldHoldExternalReboot())
                return;

            if (RebootPendingReader.IsRebootPending())
            {
                state.Phase = "reboot";
                state.ResumeAfterReboot = true;
                state.Message = "Restart pending before clearing can continue.";
                Save(state);
                var policy = UpdatePolicy.Load();
                policy.Normalize();
                if (policy.WindowsAutomation.AutoReboot.Enabled)
                    RebootCoordinator.HandleAfterInstall("Unattended until clear", rebootRequired: true, owner: null);
                return;
            }

            state.LastContinueAt = DateTime.Now;
            if (state.Round >= state.MaxRounds)
            {
                Complete($"Stopped after {state.MaxRounds} clearing rounds.");
                return;
            }

            state.Round++;
            state.Phase = "clearing";
            state.Message = $"Clearing round {state.Round}/{state.MaxRounds}: scanning…";
            Save(state);

            AgentLog.Write($"Unattended until-clear: clearing round {state.Round}.");
            ActivityLog.TryWrite(
                "UnattendedUntilClear",
                "ClearingRound",
                new { state.Round },
                mode: "Automatic",
                source: "TrayAgent");

            var policyLive = UpdatePolicy.Load();
            policyLive.Normalize();

            // --- Windows Critical / General / Defender signatures (same scope as Update until current) ---
            List<UpdateEntry> windowsMatches = new();
            try
            {
                var pending = await WindowsUpdateReader.RunStaAsync(() => WindowsUpdateReader.AdvancedPending(false, false))
                    .ConfigureAwait(false);
                windowsMatches = pending
                    .Where(UpdateUntilCurrent.IsInCycle)
                    .Where(e => !IsSkipped(e.UpdateId, state))
                    .ToList();
            }
            catch (Exception ex)
            {
                AgentLog.Write("Unattended until-clear: Windows scan failed: " + ex.Message);
            }

            if (windowsMatches.Count > 0)
            {
                // Prefer Critical + Defender first; then General (same split as automation categories).
                var critical = windowsMatches.Where(UpdateCategoryRules.IsCriticalSoftware).ToList();
                var defender = windowsMatches
                    .Where(e => UpdateCategoryRules.IsDefenderSignature(e) && !string.IsNullOrWhiteSpace(e.UpdateId))
                    .ToList();
                var general = windowsMatches.Where(UpdateCategoryRules.IsGeneralSoftware).ToList();

                if (critical.Count + defender.Count > 0)
                {
                    QueueWindowsBatch("critical", critical.Concat(defender).DistinctBy(e => e.UpdateId).ToList());
                    return;
                }

                if (general.Count > 0)
                {
                    QueueWindowsBatch("general", general);
                    return;
                }
            }

            // --- Applications (WinGet), when automatic apps are enabled ---
            if (policyLive.WindowsAutomation.Enabled && policyLive.AutomaticApplications)
            {
                try
                {
                    var scan = await WingetReader.ScanAsync().ConfigureAwait(false);
                    WingetAvailableCache.Write(scan.Updates, scan.Raw);
                    var allowIds = policyLive.AutomaticApplicationIds
                                   ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var apps = scan.Updates
                        .Where(app => allowIds.Count == 0 || allowIds.Contains(app.Id))
                        .Where(app => !IsSkipped(app.Id, state))
                        .ToList();

                    if (apps.Count > 0)
                    {
                        QueueApplicationsBatch(apps);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AgentLog.Write("Unattended until-clear: WinGet scan failed: " + ex.Message);
                }
            }

            // Fall through when scans found nothing.

            // Nothing left (or only skips).
            int skipped = state.SkipIds.Count;
            Complete(skipped > 0
                ? $"Finished. No further updates to install ({skipped} skipped after failure)."
                : "Finished. No further updates to install.");
        }
        catch (Exception ex)
        {
            AgentLog.Write("Unattended until-clear continue failed: " + ex);
        }
        finally
        {
            Interlocked.Exchange(ref _continueRunning, 0);
        }
    }

    private static bool HasInFlightConfirmedWork()
    {
        foreach (var cat in new[] { "critical", "general", "applications" })
        {
            var prompt = CategoryPromptState.Load(cat);
            if (string.Equals(prompt.State, "installing", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(prompt.State, "scheduled", StringComparison.OrdinalIgnoreCase) &&
                prompt.ScheduledInstallAt is DateTime when &&
                when <= DateTime.Now.AddMinutes(1) &&
                prompt.Updates.Count > 0)
                return true;

            if (cat == "applications")
            {
                var queue = CategoryPromptState.ReadInstallQueueState(cat);
                if (queue.UpdateIds.Count > 0 &&
                    (queue.ResumeAfterReboot ||
                     string.Equals(prompt.State, "installing", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            else
            {
                var ids = CategoryPromptState.ReadInstallQueue(cat);
                if (ids.Count > 0 &&
                    string.Equals(prompt.State, "installing", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static void QueueWindowsBatch(string category, List<UpdateEntry> matches)
    {
        string displayName = category == "general" ? "General" : "Critical";
        string elevateArg = category == "general" ? "--apply-general-install" : "--apply-critical-install";

        var prompt = new CategoryPromptState
        {
            Category = category,
            CreatedAt = DateTime.Now,
            Downloaded = false,
            State = "installing",
            Updates = matches.Select(m => new PromptUpdateItem
            {
                UpdateId = m.UpdateId,
                KB = m.KB,
                Title = m.Title,
                Priority = m.Priority
            }).ToList()
        };
        prompt.MarkInstalling();
        prompt.Save();
        prompt.WriteInstallQueue(matches.Select(m => m.UpdateId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList());

        MarkInstalling(displayName);
        AgentLog.Write($"Unattended until-clear: queuing {matches.Count} {displayName} update(s).");
        ActivityLog.TryWrite(
            "UnattendedUntilClear",
            "QueueWindows",
            new { Category = category, Count = matches.Count },
            mode: "Automatic",
            source: "TrayAgent");

        try
        {
            ElevationHelper.RestartElevated(AutomationStatePaths.ElevateArguments(elevateArg));
            WindowsAutomationStatus.WriteCategory(
                category, "Installing",
                $"Unattended clearing: {matches.Count} update(s).",
                DateTime.Now.AddMinutes(30));
        }
        catch (Exception ex)
        {
            AgentLog.Write($"Unattended until-clear: could not elevate {displayName}: " + ex.Message);
            foreach (var m in matches)
                RecordItem(m.UpdateId, m.Title, displayName, success: false, detail: "Elevate failed: " + ex.Message);
            // Stay in clearing; next tick will skip those ids.
            var state = Load();
            state.Phase = "clearing";
            state.LastContinueAt = null;
            Save(state);
        }
    }

    private static void QueueApplicationsBatch(List<AppUpdate> apps)
    {
        var prompt = new CategoryPromptState
        {
            Category = "applications",
            CreatedAt = DateTime.Now,
            Downloaded = false,
            State = "installing",
            Updates = apps.Select(m => new PromptUpdateItem
            {
                UpdateId = m.Id,
                KB = "",
                Title = $"{m.DisplayName} ({m.Installed} → {m.Available})",
                Priority = m.Priority
            }).ToList()
        };
        prompt.MarkInstalling();
        prompt.Save();
        CategoryPromptState.WriteInstallQueueState(new InstallQueueState
        {
            Category = "applications",
            CreatedAt = DateTime.Now,
            UpdateIds = apps.Select(a => a.Id).ToList(),
            CompletedIds = new(),
            ResumeAfterReboot = false
        });

        MarkInstalling("Applications");
        AgentLog.Write($"Unattended until-clear: queuing {apps.Count} application upgrade(s).");
        ActivityLog.TryWrite(
            "UnattendedUntilClear",
            "QueueApplications",
            new { Count = apps.Count },
            mode: "Automatic",
            source: "TrayAgent");

        try
        {
            try { RebootCoordinator.CancelCountdownReboot("unattended apps clearing starting"); }
            catch { /* best effort */ }
            ElevationHelper.RestartElevated(AutomationStatePaths.ElevateArguments("--apply-applications-install"));
            WindowsAutomationStatus.WriteCategory(
                "applications", "Installing",
                $"Unattended clearing: {apps.Count} package(s).",
                DateTime.Now.AddMinutes(30));
        }
        catch (Exception ex)
        {
            AgentLog.Write("Unattended until-clear: could not elevate Applications: " + ex.Message);
            foreach (var a in apps)
                RecordItem(a.Id, a.DisplayName, "Applications", success: false, detail: "Elevate failed: " + ex.Message);
            CategoryPromptState.ClearInstallQueue("applications");
            CategoryPromptState.Clear("applications");
            var state = Load();
            state.Phase = "clearing";
            state.LastContinueAt = null;
            Save(state);
        }
    }

    /// <summary>Show the overnight result when the user is back and the quiet window has passed.</summary>
    internal static void TryShowPendingResult(Dispatcher dispatcher)
    {
        if (Interlocked.CompareExchange(ref _resultShowing, 1, 0) != 0)
            return;

        try
        {
            var state = Load();
            if (!state.ResultPending)
            {
                Interlocked.Exchange(ref _resultShowing, 0);
                return;
            }

            if (state.ShowResultAfter is DateTime after && DateTime.Now < after)
            {
                Interlocked.Exchange(ref _resultShowing, 0);
                return;
            }

            // Prefer an interactive user; after 12h past ShowResultAfter, show even if idle.
            bool userPresent = !IsSessionQuiet(TimeSpan.FromMinutes(3));
            bool overdue = state.ShowResultAfter is DateTime showAfter &&
                           DateTime.Now >= showAfter.AddHours(12);
            if (!userPresent && !overdue)
            {
                Interlocked.Exchange(ref _resultShowing, 0);
                return;
            }

            bool norwegian = AppLanguage.IsNorwegian;
            var snapshot = state;
            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var dlg = new UnattendedRunResultWindow(snapshot, norwegian);
                    dlg.ShowDialog();
                    var done = Load();
                    done.ResultPending = false;
                    done.Phase = done.Phase == "complete" ? "idle" : done.Phase;
                    done.Message = "Result acknowledged.";
                    Save(done);
                    ActivityLog.TryWrite(
                        "UnattendedUntilClear",
                        "ResultShown",
                        new { Ok = snapshot.Successful.Count, Failed = snapshot.Failed.Count },
                        mode: "Automatic",
                        source: "TrayAgent");
                }
                catch (Exception ex)
                {
                    AgentLog.Write("Unattended until-clear result window failed: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _resultShowing, 0);
                }
            }));
        }
        catch
        {
            Interlocked.Exchange(ref _resultShowing, 0);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint CbSize;
        public uint DwTime;
    }
}

internal sealed class UnattendedUntilClearState
{
    public bool Active { get; set; }
    public string Phase { get; set; } = "idle";
    public DateTime? ArmedAt { get; set; }
    public DateTime? ScheduledStartAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ShowResultAfter { get; set; }
    public DateTime? LastContinueAt { get; set; }
    public bool ResultPending { get; set; }
    public bool ResumeAfterReboot { get; set; }
    public int Round { get; set; }
    public int MaxRounds { get; set; } = UnattendedUntilClear.DefaultMaxRounds;
    public int SoftDeferCount { get; set; }
    public string Message { get; set; } = "";
    public List<string> ConfirmedCategories { get; set; } = new();
    public List<string> SkipIds { get; set; } = new();
    public List<UnattendedRunItem> Successful { get; set; } = new();
    public List<UnattendedRunItem> Failed { get; set; } = new();
}

internal sealed class UnattendedRunItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime At { get; set; }
}
