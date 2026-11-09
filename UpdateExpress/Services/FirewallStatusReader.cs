using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace UpdateExpress.Services;
public static class FirewallStatusReader
{
    public static async Task<Dictionary<string, bool?>> ReadAsync()
    {
        var result = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string script = "$ErrorActionPreference='Stop'; @(Get-NetFirewallProfile | ForEach-Object { [pscustomobject]@{ Name=[string]$_.Name; Enabled=([string]$_.Enabled -eq 'True') } }) | ConvertTo-Json -Compress";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + encoded) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException();
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { } return result; }
            var json = await output; _ = await errors;
            if (process.ExitCode != 0) return result;
            using var document = JsonDocument.Parse(json);
            var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : new[] { document.RootElement };
            foreach (var item in items)
            {
                if (!item.TryGetProperty("Name", out var name) || !item.TryGetProperty("Enabled", out var enabled)) continue;
                result[name.GetString() ?? ""] = enabled.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.String when bool.TryParse(enabled.GetString(), out var parsed) => parsed, JsonValueKind.Number when enabled.TryGetInt32(out var number) => number != 0, _ => null };
            }
        }
        catch { /* Unknown is safer than a false green. */ }
        return result;
    }
}
