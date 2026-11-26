using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace UpdateExpress.Services;

/// <summary>
/// Soft empty-desktop / quiet-session checks for unattended start.
/// Combines last-input idle with visible interactive window heuristics
/// (not a fragile UI scrape).
/// </summary>
internal static class QuietSessionHeuristics
{
    private static readonly HashSet<string> IgnoredProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "shellexperiencehost", "startmenuexperiencehost", "searchhost",
        "searchapp", "textinputhost", "applicationframehost", "systemsettings",
        "lockapp", "logonui", "dwm", "sihost", "taskhostw", "runtimebroker",
        "securityhealthsystray", "securityhealthservice", "widgets", "widgetservice",
        "phoneexperiencehost", "crossdeviceresume", "updateexpress",
        "anydesk", "msrdc", "mstsc", "rdpclip", "rdpinput"
    };

    /// <summary>
    /// True when the session looks quiet enough to start unattended install:
    /// idle keyboard/mouse for <paramref name="idleFor"/> AND no meaningful
    /// interactive user apps with visible top-level windows.
    /// </summary>
    internal static bool IsQuietEnoughForUnattended(TimeSpan? idleFor = null)
    {
        if (!UnattendedUntilClear.IsSessionQuiet(idleFor ?? TimeSpan.FromMinutes(UnattendedUntilClear.SoftIdleMinutes)))
            return false;
        return !HasInteractiveUserApps();
    }

    /// <summary>
    /// Visible top-level windows belonging to non-shell user processes in this session.
    /// </summary>
    internal static bool HasInteractiveUserApps()
    {
        try
        {
            int currentSession = Process.GetCurrentProcess().SessionId;
            int count = 0;
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
                    long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
                    if ((style & WsCaption) == 0 && (style & WsPopup) != 0) return true; // tool/popup
                    if (IsIconic(hwnd)) return true;

                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == 0) return true;
                    using var proc = Process.GetProcessById((int)pid);
                    if (proc.SessionId != currentSession) return true;
                    string name = proc.ProcessName ?? "";
                    if (IgnoredProcessNames.Contains(name)) return true;

                    // Skip tiny / nameless chrome.
                    var title = new StringBuilder(256);
                    GetWindowText(hwnd, title, title.Capacity);
                    if (title.Length == 0) return true;

                    count++;
                }
                catch
                {
                    /* ignore access-denied / exited processes */
                }
                return count < 8; // enough signal
            }, IntPtr.Zero);
            return count > 0;
        }
        catch
        {
            return false; // fail open — do not block unattended forever
        }
    }

    private const int GwOwner = 4;
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;
    private const long WsPopup = 0x80000000;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));
}
