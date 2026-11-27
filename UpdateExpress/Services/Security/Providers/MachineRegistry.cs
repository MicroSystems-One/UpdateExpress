using Microsoft.Win32;

namespace UpdateExpress.Services.Security.Providers;

/// <summary>
/// What the checks read from HKEY_LOCAL_MACHINE. Every member throws when the key may not be
/// read, so that a caller can tell "not there" from "not allowed to look".
/// </summary>
internal interface IMachineRegistry
{
    bool KeyExists(string key);
    /// <summary>A number, or null when the key or the value is absent.</summary>
    int? ReadNumber(string key, string name);
}

/// <summary>HKEY_LOCAL_MACHINE in its 64-bit view, opened for reading only.</summary>
internal sealed class MachineRegistry : IMachineRegistry
{
    public bool KeyExists(string key)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var opened = machine.OpenSubKey(key, writable: false);
        return opened is not null;
    }

    public int? ReadNumber(string key, string name)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var opened = machine.OpenSubKey(key, writable: false);
        return opened?.GetValue(name) switch
        {
            int number => number,
            long number => (int)number,
            string text when int.TryParse(text, out int number) => number,
            _ => null
        };
    }

    /// <summary>Whether a failed registry read means "not allowed" or "could not be read", as opposed to a fault in the code.</summary>
    internal static bool IsReadFailure(Exception ex) =>
        ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException;
}
