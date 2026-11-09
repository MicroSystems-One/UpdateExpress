using System;
using System.Diagnostics;
using System.Security.Principal;

namespace UpdateExpress.Services;

internal static class ElevationHelper
{
    internal static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal static void RestartElevated(string arguments = "--installed")
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path.");
        var info = new ProcessStartInfo(exe)
        {
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
        };
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Windows did not start the elevated process.");
    }
}
