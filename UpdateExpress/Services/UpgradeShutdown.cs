using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateExpress.Services;

/// <summary>
/// Coordinates closing the elevated main UI before self-update.
/// The tray agent is medium-IL; MainWindow is elevated, so Kill/MainModule from the
/// non-elevated maintenance helper fails with Access Denied. The UI must exit itself.
/// </summary>
internal static class UpgradeShutdown
{
    internal const string EventName = @"Local\MicroSystems.UpdateExpress.ShutdownForUpgrade";

    private const int ProcessQueryLimitedInformation = 0x1000;

    internal static void RequestUiClose()
    {
        try
        {
            using var ev = new EventWaitHandle(false, EventResetMode.ManualReset, EventName);
            ev.Set();
            AgentLog.Write("Requested Update Express UI close for upgrade.");
        }
        catch (Exception ex)
        {
            AgentLog.Write("Could not signal UI close for upgrade: " + ex.Message);
        }
    }

    internal static void ClearPendingRequest()
    {
        try
        {
            using var ev = new EventWaitHandle(false, EventResetMode.ManualReset, EventName);
            ev.Reset();
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Listen for a shutdown-for-upgrade signal. Returns a CTS that stops the listener.
    /// </summary>
    internal static CancellationTokenSource Listen(Action onRequested)
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        ClearPendingRequest();
        _ = Task.Run(() =>
        {
            try
            {
                using var ev = new EventWaitHandle(false, EventResetMode.ManualReset, EventName);
                while (!token.IsCancellationRequested)
                {
                    if (!ev.WaitOne(500)) continue;
                    AgentLog.Write("UI received shutdown-for-upgrade signal.");
                    try { onRequested(); }
                    catch (Exception ex) { AgentLog.Write("Upgrade shutdown callback failed: " + ex.Message); }
                    return;
                }
            }
            catch (Exception ex)
            {
                AgentLog.Write("Upgrade shutdown listener failed: " + ex.Message);
            }
        }, token);
        return cts;
    }

    /// <summary>
    /// Ask open Update Express UI to close and wait until it is gone.
    /// Falls back to a Retry dialog for builds that do not listen for the signal.
    /// </summary>
    internal static bool EnsureUiClosedForUpgrade()
    {
        RequestUiClose();
        TryClosePeerMainWindows();

        if (WaitUntilNoPeerUi(TimeSpan.FromSeconds(15)))
        {
            ClearPendingRequest();
            return true;
        }

        while (HasPeerUiProcess())
        {
            var choice = System.Windows.Forms.MessageBox.Show(
                "Update Express is still open.\n\n" +
                "Close the Update Express window, then click Retry to continue the upgrade.\n\n" +
                "(The update window runs elevated, so it must close before files can be replaced.)",
                "Update Express",
                System.Windows.Forms.MessageBoxButtons.RetryCancel,
                System.Windows.Forms.MessageBoxIcon.Warning);
            if (choice != System.Windows.Forms.DialogResult.Retry)
            {
                ClearPendingRequest();
                return false;
            }
            RequestUiClose();
            TryClosePeerMainWindows();
            WaitUntilNoPeerUi(TimeSpan.FromSeconds(10));
        }

        ClearPendingRequest();
        return true;
    }

    private static void TryClosePeerMainWindows()
    {
        int self = Environment.ProcessId;
        foreach (var proc in Process.GetProcessesByName("UpdateExpress"))
        {
            using (proc)
            {
                try
                {
                    if (proc.Id == self) continue;
                    if (proc.MainWindowHandle == IntPtr.Zero) continue;
                    proc.CloseMainWindow();
                }
                catch (Exception ex)
                {
                    AgentLog.Write($"Could not send close to PID {proc.Id}: {ex.Message}");
                }
            }
        }
    }

    private static bool WaitUntilNoPeerUi(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!HasPeerUiProcess()) return true;
            Thread.Sleep(400);
        }
        return !HasPeerUiProcess();
    }

    internal static bool HasPeerUiProcess()
    {
        int self = Environment.ProcessId;
        foreach (var proc in Process.GetProcessesByName("UpdateExpress"))
        {
            using (proc)
            {
                try
                {
                    if (proc.Id == self) continue;
                    if (proc.MainWindowHandle != IntPtr.Zero)
                        return true;
                }
                catch { /* ignore individual process */ }
            }
        }
        return false;
    }

    internal static bool TryGetProcessPath(int processId, out string? path)
    {
        path = null;
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageName(handle, 0, sb, ref size)) return false;
            path = Path.GetFullPath(sb.ToString());
            return !string.IsNullOrWhiteSpace(path);
        }
        catch { return false; }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
}
