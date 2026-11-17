using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UpdateExpress.Services;

/// <summary>Create / stop / delete the Update Express Windows service via sc.exe.</summary>
internal static class WindowsServiceInstaller
{
    internal static void InstallOrUpdate(string targetExe)
    {
        if (string.IsNullOrWhiteSpace(targetExe) || !File.Exists(targetExe))
            throw new FileNotFoundException("Cannot register Windows service; EXE missing.", targetExe);

        string binPath = $"\"{targetExe}\" --service";
        var existing = PmeServiceControl.ReadState(WindowsServiceHost.ServiceName);
        if (existing.Exists)
        {
            AgentLog.WriteInstall("Windows service already registered; updating binPath and restarting.");
            RunScQuiet("stop", WindowsServiceHost.ServiceName);
            // config binPath= must be one argument after binPath=
            RunScQuiet("config", WindowsServiceHost.ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem");
        }
        else
        {
            AgentLog.WriteInstall("Creating Windows service " + WindowsServiceHost.ServiceName);
            RunScRequired(
                "create",
                WindowsServiceHost.ServiceName,
                "binPath=", binPath,
                "start=", "auto",
                "obj=", "LocalSystem",
                "DisplayName=", WindowsServiceHost.DisplayName);
            RunScQuiet("description", WindowsServiceHost.ServiceName,
                "Update Express background automation (MicroSystems). UI remains in the user session.");
        }

        var start = RunScQuiet("start", WindowsServiceHost.ServiceName);
        AgentLog.WriteInstall($"Windows service start exit={start.ExitCode}; output={Trim(start.Output)}");
        if (start.ExitCode != 0 && start.ExitCode != 1056)
            throw new InvalidOperationException(
                "Windows service was registered but failed to start (sc exit " + start.ExitCode + "). " + Trim(start.Output));
    }

    internal static void StopAndDelete()
    {
        var existing = PmeServiceControl.ReadState(WindowsServiceHost.ServiceName);
        if (!existing.Exists)
        {
            AgentLog.WriteInstall("Windows service not present; nothing to remove.");
            return;
        }

        AgentLog.WriteInstall("Stopping and deleting Windows service " + WindowsServiceHost.ServiceName);
        RunScQuiet("stop", WindowsServiceHost.ServiceName);
        // Give SCM a moment after stop before delete.
        System.Threading.Thread.Sleep(800);
        var del = RunScQuiet("delete", WindowsServiceHost.ServiceName);
        AgentLog.WriteInstall($"Windows service delete exit={del.ExitCode}; output={Trim(del.Output)}");
        if (del.ExitCode != 0 && del.ExitCode != 1060)
            throw new InvalidOperationException(
                "Could not delete Windows service (sc exit " + del.ExitCode + "). " + Trim(del.Output));
    }

    private static (int ExitCode, string Output) RunScQuiet(params string[] arguments)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in arguments)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start sc.exe.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        return (process.ExitCode, output);
    }

    private static void RunScRequired(params string[] arguments)
    {
        var result = RunScQuiet(arguments);
        // 1073 = service already exists
        if (result.ExitCode != 0 && result.ExitCode != 1073)
            throw new InvalidOperationException(
                "sc.exe " + arguments[0] + " failed (exit " + result.ExitCode + "): " + Trim(result.Output));
    }

    private static string Trim(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }
}
