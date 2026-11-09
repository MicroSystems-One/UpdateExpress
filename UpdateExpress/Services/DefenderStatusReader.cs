using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateExpress.Services;

public sealed record DefenderState(string Level, string Label, string Details, DateTime CheckedAt);

public static class DefenderStatusReader
{
    public static Task<DefenderState> ReadAsync() => Task.Run(Read);

    private static DefenderState Read()
    {
        var checkedAt = DateTime.Now;
        try
        {
            // Read-only Microsoft Defender cmdlet; do not change AV settings or signatures.
            const string command = "$ErrorActionPreference='Stop'; $mp=$null; try {$mp=Get-MpComputerStatus | Select-Object AntivirusEnabled,RealTimeProtectionEnabled,AMServiceEnabled,AntivirusSignatureLastUpdated,AntivirusSignatureAge,AntivirusSignatureVersion,AMRunningMode} catch {}; $products=@(); try {$products=@(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntivirusProduct | Select-Object displayName,productState)} catch {}; @{ Defender=$mp; Products=$products } | ConvertTo-Json -Compress -Depth 4";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
            var start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + encoded)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell could not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(12000))
            {
                process.Kill(entireProcessTree: true);
                return new("unknown", "Unknown", "Defender check timed out.", checkedAt);
            }
            var output = stdout.GetAwaiter().GetResult();
            var error = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return new("unknown", "Unknown", "Cannot read Defender: " + Trim(error), checkedAt);
            using var json = JsonDocument.Parse(output);
            var envelope = json.RootElement;
            var root = envelope.TryGetProperty("Defender", out var defender) && defender.ValueKind == JsonValueKind.Object ? defender : envelope;
            var thirdParty = new List<string>();
            if (envelope.TryGetProperty("Products", out var products))
            {
                var list = products.ValueKind == JsonValueKind.Array ? products.EnumerateArray().ToArray() : products.ValueKind == JsonValueKind.Object ? new[] { products } : Array.Empty<JsonElement>();
                foreach (var product in list)
                {
                    var name = Str(product, "displayName");
                    if (!string.IsNullOrWhiteSpace(name) && !name.Contains("Defender", StringComparison.OrdinalIgnoreCase)) thirdParty.Add(name);
                }
            }
            bool? enabled = Bool(root, "AntivirusEnabled");
            bool? realtime = Bool(root, "RealTimeProtectionEnabled");
            bool? service = Bool(root, "AMServiceEnabled");
            var mode = Str(root, "AMRunningMode");
            var version = Str(root, "AntivirusSignatureVersion");
            var dateString = Str(root, "AntivirusSignatureLastUpdated");
            DateTime? updated = DateTime.TryParse(dateString, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;
            int? age = Int(root, "AntivirusSignatureAge");
            // The cmdlet's age field is whole days. Use timestamp for the 48-hour boundary.
            var ageSpan = updated.HasValue ? checkedAt - updated.Value : (age.HasValue ? TimeSpan.FromDays(age.Value) : (TimeSpan?)null);
            var detail = $"Signature: {version}\nLast updated: {(updated.HasValue ? updated.Value.ToString("dd.MM.yyyy HH:mm") : "Unknown")}\nReal-time protection: {(realtime == true ? "On" : "Off/unknown")}\nChecked: {checkedAt:dd.MM.yyyy HH:mm}";
            if (mode.Contains("Passive", StringComparison.OrdinalIgnoreCase) || mode.Contains("EDR Block", StringComparison.OrdinalIgnoreCase) || (enabled == false && thirdParty.Count > 0))
                return new("unknown", thirdParty.Count > 0 ? string.Join(", ", thirdParty) : "Defender passive", detail + "\nThird-party antivirus detected. Protection health could not be independently verified.", checkedAt);
            if (enabled == false || realtime == false || service == false)
                return new("red", "Protection disabled", detail + "\nCheck Defender services.", checkedAt);
            if (enabled != true || realtime != true || service != true || !ageSpan.HasValue || ageSpan.Value < TimeSpan.Zero)
                return new("unknown", "Unknown", detail, checkedAt);
            if (ageSpan.Value >= TimeSpan.FromDays(7)) return new("red", "Critical: 7+ days", detail, checkedAt);
            if (ageSpan.Value >= TimeSpan.FromDays(2)) return new("orange", "Warning: 2–6 days", detail, checkedAt);
            return new("green", "Updated", detail, checkedAt);
        }
        catch (Exception ex) { return new("unknown", "Unknown", "Check failed: " + Trim(ex.Message), checkedAt); }
    }
    private static string Trim(string s) => s.Length > 180 ? s[..180] + "…" : s;
    private static string Str(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
    private static bool? Bool(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
    private static int? Int(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.TryGetInt32(out var n) ? n : null;
}
