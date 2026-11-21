using System;
using System.Threading;

namespace UpdateExpress.Services;

/// <summary>
/// Marks an open Main UI session so the tray agent does not also present Påminnelse.
/// Main owns the reminder timer while it is open; tray owns it when Main is closed.
/// </summary>
internal static class MaintenanceReminderPromptGate
{
    private const string MainUiMutexName = @"Local\MicroSystems.UpdateExpress.MainUi";
    private static Mutex? _mainUiMutex;
    private static bool _ownsMainUiMutex;

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
}
