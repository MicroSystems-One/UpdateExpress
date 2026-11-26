using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace UpdateExpress.Services;

/// <summary>
/// Tray/background path for scheduled Påminnelse when Main UI is closed.
/// MainWindow keeps its own timer while open; this host skips while MainUi is registered.
/// Preserves single-instance ReminderDialog + 30-minute AFK soft-dismiss.
/// </summary>
internal static class MaintenanceReminderHost
{
    private static readonly TimeSpan LongUptimeThreshold = TimeSpan.FromDays(7);
    private static int _presentQueued;
    private static ReminderPresentArgs? _pending;

    private sealed class ReminderPresentArgs
    {
        public bool IsTest;
        public DateTime Occurrence;
        public bool Norwegian;
        public bool ShowMissed;
        public string ReasonKey = "";
        public IReadOnlyList<string>? PendingLines;
    }

    /// <summary>
    /// Called from the tray agent on a short interval. No-ops when Main UI owns reminders.
    /// </summary>
    internal static void CheckFromTray(Dispatcher dispatcher)
    {
        if (MaintenanceReminderPromptGate.IsMainUiSessionActive()) return;
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                try { CheckAndPresent(); }
                catch (Exception ex)
                {
                    AgentLog.Write("Tray reminder check failed: " + ex.Message);
                    ReminderSchedule.Log("tray_reminder_error", ex.Message);
                }
            }));
        }
        catch (Exception ex)
        {
            AgentLog.Write("Tray reminder dispatch failed: " + ex.Message);
        }
    }

    private static void CheckAndPresent()
    {
        if (MaintenanceReminderPromptGate.IsMainUiSessionActive()) return;
        // Main (or this process) already showing — do not open a second dialog.
        if (MaintenanceReminderPromptGate.IsReminderPromptHeldElsewhere()) return;
        if (ReminderDialog.IsShowing) return;

        var schedule = ReminderSchedule.Load();
        if (!schedule.Enabled) return;

        var now = DateTime.Now;
        if (schedule.SnoozedUntil is DateTime snooze && now < snooze) return;

        var policy = UpdatePolicy.Load();
        policy.Normalize();
        int todayIndex = ((int)now.DayOfWeek + 6) % 7;
        int mask = MaintenanceSetup.ClampMask(policy.InstallDaysMask);
        if (mask != 0)
        {
            if (!MaintenanceSetup.HasDay(mask, todayIndex)) return;
        }
        else if (schedule.Weekday != 7 && todayIndex != schedule.Weekday) return;

        if (!TimeSpan.TryParseExact(schedule.Time, @"hh\:mm", CultureInfo.InvariantCulture, out var at))
            return;

        if (schedule.ConfirmedOccurrence is DateTime confirmed && confirmed.Date == now.Date)
            return;

        var due = now.Date.Add(at);
        var slot = ReminderSchedule.EffectiveReminderSlot(now, at);
        if (slot is null)
        {
            if (now >= due
                && ReminderSchedule.NextHalfHourSlot(now) is null
                && schedule.ConfirmedOccurrence?.Date != now.Date)
            {
                MarkMissedUnconfirmedDay(schedule, now.Date);
            }
            return;
        }

        if (schedule.LastPromptedOccurrence == slot.Value) return;
        if (!TryResolveReason(schedule, out string reasonKey, out var pendingLines))
            return;

        Present(schedule, isTest: false, slot.Value, reasonKey, pendingLines);
    }

    /// <summary>
    /// Reason sources available without MainWindow memory: monitor-status, winget cache,
    /// category prompt queues, reboot-pending registry, and uptime.
    /// </summary>
    private static bool TryResolveReason(
        ReminderSchedule schedule,
        out string reasonKey,
        out List<string> pendingLines)
    {
        reasonKey = "";
        pendingLines = new List<string>();
        const int maxLines = 12;
        bool norwegian = string.Equals(schedule.Language, "no", StringComparison.OrdinalIgnoreCase);

        var monitor = MonitorStatus.Read();
        int windowsPending = monitor?.Pending ?? 0;
        string[] winTitles = monitor?.Updates ?? Array.Empty<string>();

        List<AppUpdate> apps = new();
        if (WingetAvailableCache.TryRead(out _, out var cachedApps, out _))
            apps = cachedApps;

        // Also count automation prompt queues (download+ask) as pending work.
        int promptPending = 0;
        foreach (var category in new[] { "critical", "general", "drivers", "applications" })
        {
            try
            {
                var prompt = CategoryPromptState.Load(category);
                if (string.Equals(prompt.State, "awaitingPrompt", StringComparison.OrdinalIgnoreCase)
                    && prompt.Updates.Count > 0)
                {
                    promptPending += prompt.Updates.Count;
                    foreach (var item in prompt.Updates)
                    {
                        if (pendingLines.Count >= maxLines) break;
                        var parts = new List<string>();
                        if (!string.IsNullOrWhiteSpace(item.KB)) parts.Add(item.KB.Trim());
                        if (!string.IsNullOrWhiteSpace(item.Title)) parts.Add(item.Title.Trim());
                        if (parts.Count == 0) continue;
                        string line = "•  " + string.Join("  ·  ", parts);
                        if (!pendingLines.Contains(line, StringComparer.OrdinalIgnoreCase))
                            pendingLines.Add(line);
                    }
                }
            }
            catch { /* ignore per-category */ }
        }

        foreach (var title in winTitles)
        {
            if (pendingLines.Count >= maxLines) break;
            string text = (title ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            string line = text.StartsWith('•') ? text : "•  " + text;
            if (!pendingLines.Contains(line, StringComparer.OrdinalIgnoreCase))
                pendingLines.Add(line);
        }

        foreach (var app in apps)
        {
            if (pendingLines.Count >= maxLines) break;
            string name = (app.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            string ver = string.IsNullOrWhiteSpace(app.Available)
                ? ""
                : (string.IsNullOrWhiteSpace(app.Installed)
                    ? app.Available.Trim()
                    : app.Installed.Trim() + " → " + app.Available.Trim());
            pendingLines.Add(string.IsNullOrWhiteSpace(ver)
                ? "•  " + name
                : "•  " + name + "  ·  " + ver);
        }

        int total = Math.Max(windowsPending, winTitles.Length) + apps.Count + promptPending;
        if (total > pendingLines.Count && pendingLines.Count > 0)
        {
            pendingLines.Add(norwegian
                ? $"… og {total - pendingLines.Count} til."
                : $"… and {total - pendingLines.Count} more.");
        }

        if (total > 0 || pendingLines.Count > 0)
        {
            reasonKey = "pending";
            return true;
        }

        if (RebootPendingReader.IsRebootPending())
        {
            reasonKey = "reboot";
            return true;
        }

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (up < TimeSpan.Zero) up = TimeSpan.Zero;
        if (up >= LongUptimeThreshold)
        {
            reasonKey = "uptime";
            return true;
        }

        return false;
    }

    private static void Present(
        ReminderSchedule schedule,
        bool isTest,
        DateTime occurrence,
        string reasonKey,
        IReadOnlyList<string>? pendingLines)
    {
        bool norwegian = string.Equals(schedule.Language, "no", StringComparison.OrdinalIgnoreCase);
        bool showMissed = !isTest
            && schedule.MissedUnconfirmedDate is DateTime missed
            && missed.Date < DateTime.Now.Date;

        _pending = new ReminderPresentArgs
        {
            IsTest = isTest,
            Occurrence = occurrence,
            Norwegian = norwegian,
            ShowMissed = showMissed,
            ReasonKey = reasonKey ?? "",
            PendingLines = pendingLines
        };

        if (ReminderDialog.IsShowing && ReminderDialog.Current is ReminderDialog open)
        {
            if (!isTest)
            {
                schedule.LastPromptedOccurrence = occurrence;
                schedule.SnoozedUntil = null;
                schedule.Save();
            }
            open.ApplyContent(isTest, occurrence, norwegian, showMissed, reasonKey, pendingLines);
            ReminderSchedule.Log(isTest ? "tray_test_reminder_refresh" : "tray_reminder_refresh",
                occurrence.ToString("o") + (string.IsNullOrEmpty(reasonKey) ? "" : "|" + reasonKey));
            return;
        }

        if (Interlocked.CompareExchange(ref _presentQueued, 1, 0) != 0)
            return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Interlocked.Exchange(ref _presentQueued, 0);
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                // Main may have opened between Check and Present — leave the slot for Main.
                if (MaintenanceReminderPromptGate.IsMainUiSessionActive())
                    return;

                var args = _pending ?? new ReminderPresentArgs
                {
                    IsTest = isTest,
                    Occurrence = occurrence,
                    Norwegian = norwegian,
                    ShowMissed = showMissed,
                    ReasonKey = reasonKey ?? "",
                    PendingLines = pendingLines
                };

                if (ReminderDialog.IsShowing && ReminderDialog.Current is ReminderDialog existing)
                {
                    if (!args.IsTest)
                    {
                        schedule.LastPromptedOccurrence = args.Occurrence;
                        schedule.SnoozedUntil = null;
                        schedule.Save();
                    }
                    existing.ApplyContent(
                        args.IsTest, args.Occurrence, args.Norwegian, args.ShowMissed,
                        args.ReasonKey, args.PendingLines);
                    ReminderSchedule.Log(args.IsTest ? "tray_test_reminder_refresh" : "tray_reminder_refresh",
                        args.Occurrence.ToString("o") + (string.IsNullOrEmpty(args.ReasonKey) ? "" : "|" + args.ReasonKey));
                    return;
                }

                // Main (or another tray) may already own the live dialog.
                if (!MaintenanceReminderPromptGate.TryEnterReminderPrompt())
                {
                    ReminderSchedule.Log(
                        args.IsTest ? "tray_test_reminder_skipped_owned" : "tray_reminder_skipped_owned",
                        args.Occurrence.ToString("o"));
                    return;
                }

                try
                {
                    if (!args.IsTest)
                    {
                        schedule.LastPromptedOccurrence = args.Occurrence;
                        schedule.SnoozedUntil = null;
                        schedule.Save();
                    }

                    var dialog = new ReminderDialog(
                        args.IsTest, args.Occurrence, args.Norwegian, args.ShowMissed,
                        args.ReasonKey, args.PendingLines)
                    {
                        Owner = null,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen
                    };
                    dialog.ShowDialog();

                    bool decidedIsTest = dialog.IsTest;
                    var decidedOccurrence = dialog.Occurrence;
                    string reasonForLog = "";
                    if (!decidedIsTest && !string.IsNullOrEmpty(args.ReasonKey))
                        reasonForLog = "|" + args.ReasonKey;

                    ReminderSchedule.Log(
                        decidedIsTest ? "tray_test_reminder" : "tray_reminder",
                        dialog.Decision + reasonForLog);

                    if (!decidedIsTest)
                        ApplyDecision(schedule, dialog, decidedOccurrence);
                }
                finally
                {
                    MaintenanceReminderPromptGate.ReleaseReminderPrompt();
                }
            }
            catch (Exception ex)
            {
                ReminderSchedule.Log(isTest ? "tray_test_reminder_error" : "tray_reminder_error", ex.Message);
                AgentLog.Write("Tray reminder present failed: " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref _presentQueued, 0);
            }
        }));
    }

    private static void ApplyDecision(ReminderSchedule schedule, ReminderDialog dialog, DateTime occurrence)
    {
        // Reload in case Main or another writer touched schedule.json during ShowDialog.
        schedule = ReminderSchedule.Load();

        if (dialog.ShowMissedPreviousDay)
            schedule.MissedUnconfirmedDate = null;

        if (dialog.Decision == "confirmed")
        {
            schedule.ConfirmedOccurrence = occurrence;
            schedule.SnoozedUntil = null;
            schedule.Save();

            bool norwegian = string.Equals(schedule.Language, "no", StringComparison.OrdinalIgnoreCase);
            string reason = norwegian
                ? "Bekreftet vedlikeholdspåminnelse."
                : "Confirmed maintenance reminder.";
            var when = occurrence <= DateTime.Now ? DateTime.Now : occurrence;
            RebootCoordinator.SchedulePending("reminder", when, reason);
            ReminderSchedule.Log("confirmed_reboot_armed", when.ToString("o"));
            // Tray already polls RebootCoordinator.RunDueJob; fire immediately when due now.
            try { RebootCoordinator.RunDueJob(allowUiPrompt: true); }
            catch (Exception ex) { AgentLog.Write("Tray reminder reboot fire failed: " + ex.Message); }
            return;
        }

        if (dialog.Decision == "snoozed")
        {
            schedule.SnoozedUntil = DateTime.Now.AddMinutes(30);
            schedule.Save();
            return;
        }

        // Lukk / AFK auto-close: soft dismiss — roll forward; never confirm restart.
        var next = ReminderSchedule.NextHalfHourSlot(DateTime.Now);
        if (next is DateTime rolled)
        {
            schedule.SnoozedUntil = rolled;
            ReminderSchedule.Log("reminder_rolled", rolled.ToString("o"));
        }
        else
        {
            MarkMissedUnconfirmedDay(schedule, occurrence.Date);
        }
        schedule.Save();
    }

    private static void MarkMissedUnconfirmedDay(ReminderSchedule schedule, DateTime day)
    {
        var date = day.Date;
        if (schedule.MissedUnconfirmedDate?.Date == date) return;
        schedule.MissedUnconfirmedDate = date;
        schedule.Save();
        ReminderSchedule.Log("reminder_deferred_next_day", date.ToString("yyyy-MM-dd"));
    }
}
