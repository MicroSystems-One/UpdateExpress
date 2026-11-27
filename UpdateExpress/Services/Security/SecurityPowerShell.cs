using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Text;

namespace UpdateExpress.Services.Security;

/// <summary>What one PowerShell read came back with.</summary>
/// <param name="Ran">False when PowerShell could not be started at all.</param>
/// <param name="Error">The first line PowerShell wrote as an error, or why it did not start.</param>
internal sealed record PowerShellAnswer(bool Ran, int ExitCode, string Output, string Error);

/// <summary>
/// Runs one read-only PowerShell script for a security check and returns what it printed.
/// The script is passed encoded on the command line; nothing is written to disk. When the
/// caller's token is cancelled, PowerShell and what it started are stopped, so a read that
/// hangs does not stay behind.
/// </summary>
internal static class SecurityPowerShell
{
    /// <summary>Makes PowerShell print UTF-8 without a byte order mark, whatever the console code page is.</summary>
    internal const string Utf8Output = "try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }; ";

    /// <summary>
    /// Windows PowerShell by its full path. The app runs elevated, so the name alone is not
    /// left to the search path.
    /// </summary>
    internal static string ExecutablePath
    {
        get
        {
            string path = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(path) ? path : "powershell.exe";
        }
    }

    internal static async Task<PowerShellAnswer> RunAsync(string script, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo(ExecutablePath, "-NoProfile -NonInteractive -EncodedCommand " + encoded)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new PowerShellAnswer(false, -1, "", ex.GetType().Name + ": " + ex.Message);
        }
        if (process is null) return new PowerShellAnswer(false, -1, "", "PowerShell did not start.");

        using (process)
        {
            // Both streams are read while PowerShell runs; a full pipe would otherwise stop it.
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Stop(process);
                _ = output.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                _ = error.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                throw;
            }
            return new PowerShellAnswer(true, process.ExitCode, await output.ConfigureAwait(false), FirstLine(await error.ConfigureAwait(false)));
        }
    }

    private static void Stop(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* It has ended by itself, or cannot be stopped; either way nothing more to do. */ }
    }

    /// <summary>PowerShell's error text is long and repeats itself; the first line says what went wrong.</summary>
    internal static string FirstLine(string? text)
    {
        foreach (string line in (text ?? "").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            return trimmed.Length > 300 ? trimmed[..300] : trimmed;
        }
        return "";
    }
}
