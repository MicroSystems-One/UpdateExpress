using System.Diagnostics;

namespace UpdateExpress.Services;

public static class PmeServiceControl
{
    public record ServiceState(bool Exists, string State, string StartMode);

    public static ServiceState ReadState(string serviceName)
    {
        var query = RunScRead("query", serviceName);
        if (query.ExitCode != 0) return new ServiceState(false, "Not found", "Not found");
        var config = RunScRead("qc", serviceName);
        var state = query.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ? "Running" :
                    query.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ? "Stopped" : "Unknown";
        var start = config.Output.Contains("DISABLED", StringComparison.OrdinalIgnoreCase) ? "Disabled" :
                    config.Output.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase) ? "Automatic" :
                    config.Output.Contains("DEMAND_START", StringComparison.OrdinalIgnoreCase) ? "Manual" : "Unknown";
        return new ServiceState(true, state, start);
    }

    public static void SetEnabled(string serviceName, bool enable)
    {
        // Stop before disabling; set startup to Automatic before starting.
        if (enable)
        {
            RunSc("config", serviceName, "start=", "auto");
            RunSc("start", serviceName);
        }
        else
        {
            RunSc("stop", serviceName);
            RunSc("config", serviceName, "start=", "disabled");
        }
    }

    private static (int ExitCode, string Output) RunScRead(string action, string serviceName)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(action);
        psi.ArgumentList.Add(serviceName);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to query service.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error);
    }

    private static void RunSc(params string[] arguments)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start Windows Service Control.");
        process.WaitForExit();
        // sc.exe returns 1056 if already running and 1062 if already stopped; do not treat these as fatal.
        if (process.ExitCode != 0 && process.ExitCode != 1056 && process.ExitCode != 1062)
            throw new InvalidOperationException($"sc.exe failed ({arguments[0]}): exit code {process.ExitCode}. No registry policies were changed.");
    }
}
