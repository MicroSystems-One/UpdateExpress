using System;
using System.Globalization;
using System.Threading;

namespace UpdateExpress.Services;

/// <summary>
/// Cross-process coordination for Påminnelse:
/// - MainUi session: while MainWindow is open, tray skips new reminder checks.
/// - ReminderPrompt: while any process shows ReminderDialog, the other must not open another.
/// - Updates-ready prompts: while MainWindow is open and a Påminnelse is due today, the tray
///   skips the Applications/Windows/Drivers "updates ready" dialogs so Påminnelse is the only prompt.
/// </summary>
internal static class MaintenanceReminderPromptGate
{
    private const string MainUiMutexName = @"Local\MicroSystems.UpdateExpress.MainUi";
    private const string ReminderPromptMutexName = @"Local\MicroSystems.UpdateExpress.ReminderPrompt";

    private static Mutex? _mainUiMutex;
    private static bool _ownsMainUiMutex;

    private static Mutex? _reminderPromptMutex;
    private static bool _ownsReminderPrompt;

    /// <summary>Call from MainWindow construction so tray can detect an open UI session.</summary>
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

    /// <summary>Call from MainWindow close so tray can resume presenting Påminnelse.</summary>
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
        return IsMutexHeldByOther(MainUiMutexName);
    }

    /// <summary>
    /// Claim exclusive ownership of the live Påminnelse dialog (cross-process).
    /// Re-entrant in the same process (refresh / nested checks).
    /// </summary>
    internal static bool TryEnterReminderPrompt()
    {
        if (_ownsReminderPrompt) return true;
        try
        {
            _reminderPromptMutex = new Mutex(false, ReminderPromptMutexName);
            bool owned;
            try { owned = _reminderPromptMutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                try { _reminderPromptMutex.Dispose(); } catch { /* ignore */ }
                _reminderPromptMutex = null;
                return false;
            }
            _ownsReminderPrompt = true;
            return true;
        }
        catch (Exception ex)
        {
            AgentLog.Write("Reminder prompt enter failed: " + ex.Message);
            try { _reminderPromptMutex?.Dispose(); } catch { /* ignore */ }
            _reminderPromptMutex = null;
            _ownsReminderPrompt = false;
            return false;
        }
    }

    /// <summary>Release after ShowDialog returns (or if presentation is aborted).</summary>
    internal static void ReleaseReminderPrompt()
    {
        if (!_ownsReminderPrompt || _reminderPromptMutex is null) return;
        try
        {
            try { _reminderPromptMutex.ReleaseMutex(); } catch { /* ignore */ }
            _reminderPromptMutex.Dispose();
        }
        catch (Exception ex)
        {
            AgentLog.Write("Reminder prompt release failed: " + ex.Message);
        }
        finally
        {
            _reminderPromptMutex = null;
            _ownsReminderPrompt = false;
        }
    }

    /// <summary>True when another process currently owns the live Påminnelse dialog.</summary>
    internal static bool IsReminderPromptHeldElsewhere()
    {
        if (_ownsReminderPrompt) return false;
        return IsMutexHeldByOther(ReminderPromptMutexName);
    }

    private static bool IsMutexHeldByOther(string name)
    {
        Mutex? probe = null;
        try
        {
            probe = new Mutex(false, name);
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
    /// True when the Main UI is open and a maintenance Påminnelse is due today (the caller is
    /// about to show a category updates-ready dialog, so pending updates exist).
    /// The caller must have saved its prompt state first and leaves it unchanged: the Main UI
    /// reads it (<see cref="CategoryPromptState.LoadUpdatesWaitingForUser"/>) as the reason
    /// to show Påminnelse, and the prompt comes back once the due window ends or the Main UI
    /// closes.
    /// </summary>
    internal static bool ShouldSuppressCategoryUpdateReadyPrompt(DateTime? now = null)
    {
        if (!IsMainUiSessionActive()) return false;
        return IsMaintenanceReminderDueForPending(now ?? DateTime.Now);
    }

    internal static bool IsMaintenanceReminderDueForPending(DateTime now)
    {
        var schedule = ReminderSchedule.Load();
        var policy = UpdatePolicy.Load();
        policy.Normalize();
        return IsReminderDueWindow(schedule, policy.InstallDaysMask, now);
    }

    /// <summary>
    /// Same day and slot rules as <c>MainWindow.CheckReminder</c>: reminders enabled, today is a
    /// reminder day, the scheduled time has passed and a half-hour slot is still left today,
    /// and today is not confirmed. A snooze or Lukk does not end the window: Påminnelse comes
    /// back at the next half hour and keeps covering the same pending updates.
    /// </summary>
    internal static bool IsReminderDueWindow(ReminderSchedule schedule, int installDaysMask, DateTime now)
    {
        if (!schedule.Enabled) return false;

        if (schedule.ConfirmedOccurrence is DateTime confirmed && confirmed.Date == now.Date)
            return false;

        int mask = MaintenanceSetup.ClampMask(installDaysMask);
        int todayIndex = MaintenanceSetup.TodayMondayBased(now);
        if (mask != 0)
        {
            if (!MaintenanceSetup.HasDay(mask, todayIndex)) return false;
        }
        else if (schedule.Weekday != 7 && todayIndex != schedule.Weekday)
            return false;

        if (!TimeSpan.TryParseExact(schedule.Time, @"hh\:mm", CultureInfo.InvariantCulture, out var at))
            return false;

        return ReminderSchedule.EffectiveReminderSlot(now, at) is not null;
    }
}
