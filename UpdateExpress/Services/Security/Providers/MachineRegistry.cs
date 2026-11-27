using System.Globalization;
using Microsoft.Win32;

namespace UpdateExpress.Services.Security.Providers;

/// <summary>
/// What the checks read from HKEY_LOCAL_MACHINE. Every member throws when the key may not be
/// read, so that a caller can tell "not there" from "not allowed to look".
/// The data of a value that holds a secret is never read; see <see cref="MachineRegistry.HoldsSecret"/>.
/// </summary>
internal interface IMachineRegistry
{
    bool KeyExists(string key);
    /// <summary>A number, or null when the key or the value is absent.</summary>
    int? ReadNumber(string key, string name);
    /// <summary>A text, or a number as text; null when the key or the value is absent or holds something else.</summary>
    string? ReadText(string key, string name);
    /// <summary>The names of the values in a key, without their data; null when the key is absent.</summary>
    IReadOnlyList<string>? ValueNames(string key);
    /// <summary>How many lines that are not empty a value holds; null when the key or the value is absent.</summary>
    int? CountLines(string key, string name);
}

/// <summary>HKEY_LOCAL_MACHINE in its 64-bit view, opened for reading only.</summary>
internal sealed class MachineRegistry : IMachineRegistry
{
    /// <summary>
    /// Values whose data is a secret: the password of the classic automatic logon. Their
    /// names may be listed to see that they are there. Their data is never read, by anyone.
    /// </summary>
    internal static bool HoldsSecret(string? name) =>
        string.Equals((name ?? "").Trim(), "DefaultPassword", StringComparison.OrdinalIgnoreCase);

    public bool KeyExists(string key)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var opened = machine.OpenSubKey(key, writable: false);
        return opened is not null;
    }

    public int? ReadNumber(string key, string name)
    {
        RefuseSecret(name);
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

    public string? ReadText(string key, string name)
    {
        RefuseSecret(name);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var opened = machine.OpenSubKey(key, writable: false);
        return opened?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            string text => text,
            int number => number.ToString(CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    public IReadOnlyList<string>? ValueNames(string key)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var opened = machine.OpenSubKey(key, writable: false);
        return opened?.GetValueNames();
    }

    public int? CountLines(string key, string name)
    {
        RefuseSecret(name);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var opened = machine.OpenSubKey(key, writable: false);
        return opened?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            string[] lines => lines.Count(line => !string.IsNullOrWhiteSpace(line)),
            string text => string.IsNullOrWhiteSpace(text) ? 0 : 1,
            null => null,
            _ => 0
        };
    }

    /// <summary>Whether a failed registry read means "not allowed" or "could not be read", as opposed to a fault in the code.</summary>
    internal static bool IsReadFailure(Exception ex) =>
        ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException;

    private static void RefuseSecret(string name)
    {
        if (HoldsSecret(name))
            throw new InvalidOperationException("The value \"" + name.Trim() + "\" holds a secret and is never read.");
    }
}
