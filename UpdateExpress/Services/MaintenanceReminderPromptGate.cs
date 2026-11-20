using System;
using System.Globalization;
using System.Threading;

namespace UpdateExpress.Services;

/// <summary>
/// Keeps Påminnelse as the single startup prompt when a maintenance reminder is due
/// for pending updates: suppress Applications/Windows/Drivers "updates ready" dialogs
/// in the same Main UI session for that due window.
/// </summary>
internal static class MaintenanceReminderPromptGate
{
    private const string MainUiMutexName = @"Local\MicroSystems.UpdateExpress.MainUi";
    private static Mutex? _mainUiMutex;
    private static bool _ownsMainUiMutex;

    /// <summary>Call from MainWindow construction so tray automation can detect an open UI session.</summary>
    internal static void RegisterMainUiSession()
    {
        if (_mainUiMutex is not null) return;
        try
        {
            _mainUiMutex = new Mutex(true, MainUiMutexName, out bool createdNew);
            _ownsMainUiMutex = createdNew;
            if (!createdNew)
            {
                // Another UI instance already holds the session marker; do not steal it.
                try { _mainUiMutex.Dispose(); } catch { /* ignore */ }
                _mainUiMutex = null;
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write("Main UI session register failed: " + ex.Message);
            _mainUiMutex = null;
            _ownsMainUiMutex = false;
        }
    }

    /// <summary>Call from MainWindow close so tray can resume category update-ready prompts.</summary>
    internal static void UnregisterMainUiSession()
    {
        if (_mainUiMutex is null) return;
        try
        {
            if (_ownsMainUiMutex)
            {
                try { _mainUiMutex.ReleaseMutex(); } catch { /* ignore */ }
            }
            _mainUiMutex.Dispose();
        }
        catch (Exception ex)
        {
            AgentLog.Write("Main UI session unregister failed: " + ex.Message);
        }
        finally
        {
            _mainUiMutex = null;
            _ownsMainUiMutex = false;
        }
    }

    internal static bool IsMainUiSessionActive()
    {
        if (_ownsMainUiMutex) return true;
        Mutex? probe = null;
        try
        {
            probe = new Mutex(false, MainUiMutexName);
            bool owned;
            try { owned = probe.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (owned)
            {
                try { probe.ReleaseMutex(); } catch { /* ignore */ }
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { probe?.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// True when the Main UI is open and a maintenance Påminnelse is due today for pending
    /// updates (caller is about to show a category updates-ready dialog, so pending exists).
    /// Leave category prompt state unchanged; re-present after the due window ends.
    /// </summary>
    internal static bool ShouldSuppressCategoryUpdateReadyPrompt(DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        if (!IsMainUiSessionActive()) return false;
        return IsMaintenanceReminderDueForPending(at);
    }

    /// <summary>
    /// Schedule window aligned with <c>MainWindow.CheckReminder</c> when pending updates exist:
    /// reminders enabled, today is a reminder day, an effective reminder slot is active
    /// (at/after scheduled clock, including half-hour rolls), not confirmed today.
    /// Snooze does not clear suppression — Påminnelse already covers this pending set for the session.
    /// </summary>
    internal static bool IsMaintenanceReminderDueForPending(DateTime now)
    {
        var schedule = ReminderSchedule.Load();
        if (!schedule.Enabled) return false;

        if (schedule.ConfirmedOccurrence is DateTime confirmed && confirmed.Date == now.Date)
            return false;

        var policy = UpdatePolicy.Load();
        policy.Normalize();
        int mask = MaintenanceSetup.ClampMask(policy.InstallDaysMask);
        int todayIndex = MaintenanceSetup.TodayMondayBased(now);
        if (mask != 0)
        {
            if (!MaintenanceSetup.HasDay(mask, todayIndex)) return false;
        }
        else if (schedule.Weekday != 7 && todayIndex != schedule.Weekday)
            return false;

        if (!TimeSpan.TryParseExact(schedule.Time, @"hh\:mm", CultureInfo.InvariantCulture, out var at))
            return false;

        // Same slot rule as CheckReminder: null before due; after due includes half-hour rolls until midnight.
        if (ReminderSchedule.EffectiveReminderSlot(now, at) is null)
            return false;

        // Due window for today: from scheduled time until confirm or next calendar day.
        // (If user already dismissed/snoozed, LastPromptedOccurrence may block another Påminnelse,
        // but category updates-ready stays suppressed for this same pending set.)
        return true;
    }
}
