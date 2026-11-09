using System;
using Microsoft.Win32;

namespace UpdateExpress.Services;

/// <summary>
/// Read-only detection of common Windows reboot-pending indicators.
/// </summary>
internal static class RebootPendingReader
{
    internal static bool IsRebootPending()
    {
        try
        {
            if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                return true;
            if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                return true;
            using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (sm?.GetValue("PendingFileRenameOperations") is string[] renames && renames.Length > 0)
                return true;
        }
        catch { }
        return false;
    }

    internal static string Detail()
    {
        var parts = new System.Collections.Generic.List<string>();
        try
        {
            if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                parts.Add("Windows Update RebootRequired");
            if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                parts.Add("CBS RebootPending");
            using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (sm?.GetValue("PendingFileRenameOperations") is string[] renames && renames.Length > 0)
                parts.Add("PendingFileRenameOperations");
        }
        catch (Exception ex) { return "Could not read reboot indicators: " + ex.Message; }
        return parts.Count == 0 ? "No reboot pending indicators." : string.Join("; ", parts);
    }

    private static bool KeyExists(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key is not null;
        }
        catch { return false; }
    }
}
