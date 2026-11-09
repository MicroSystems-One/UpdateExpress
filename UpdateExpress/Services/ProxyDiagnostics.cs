using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace UpdateExpress.Services;

public static class ProxyDiagnostics
{
    // No configuration changes. HKCU always refers to the user running this process.
    public static string ReadOnlyReport()
    {
        var output = new StringBuilder();
        output.AppendLine("Update Express – Proxy diagnostics (READ ONLY)");
        output.AppendLine($"Windows user: {Environment.UserDomainName}\\{Environment.UserName}");
        output.AppendLine();
        output.AppendLine("=== WinHTTP (machine-wide) ===");
        try
        {
            var start = new ProcessStartInfo("netsh.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("winhttp"); start.ArgumentList.Add("show"); start.ArgumentList.Add("proxy");
            using var proc = Process.Start(start) ?? throw new InvalidOperationException("netsh did not start");
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(10000)) { proc.Kill(); output.AppendLine("Timed out reading WinHTTP proxy."); }
            else { output.AppendLine(stdout.GetAwaiter().GetResult().Trim());
                if (proc.ExitCode != 0) output.AppendLine("Error: " + stderr.GetAwaiter().GetResult()); }
        }
        catch (Exception ex) { output.AppendLine("Unable to read WinHTTP: " + ex.Message); }
        output.AppendLine(); output.AppendLine("=== Current-user Windows proxy (HKCU) ===");
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) output.AppendLine("Internet Settings registry key not found.");
            else
            {
                var pac = key.GetValue("AutoConfigURL") as string;
                var enabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0;
                var server = key.GetValue("ProxyServer") as string;
                output.AppendLine("PAC setup script: " + (string.IsNullOrWhiteSpace(pac) ? "Not configured" : pac));
                output.AppendLine("Manual user proxy: " + (enabled ? "Enabled" : "Disabled"));
                output.AppendLine("User proxy server: " + (string.IsNullOrWhiteSpace(server) ? "Not configured" : server));
                output.AppendLine("Auto detect (WPAD): Not evaluated in this read-only view.");
            }
        }
        catch (Exception ex) { output.AppendLine("Unable to read user proxy: " + ex.Message); }
        output.AppendLine();
        output.AppendLine("Note: WinHTTP and user PAC are independent settings. This report does not test PAC routing or change configuration.");
        output.AppendLine("If launched elevated under a DIFFERENT account, HKCU may not be the intended end user.");
        return output.ToString();
    }
}
