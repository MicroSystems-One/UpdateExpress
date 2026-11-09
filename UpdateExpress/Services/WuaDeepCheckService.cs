using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace UpdateExpress.Services;

internal sealed class WuaDeepCheckFinding
{
    public string Area { get; init; } = "";
    public string Status { get; init; } = ""; // OK / WARN / FAIL / INFO
    public string Detail { get; init; } = "";
    public string? Hint { get; init; }
}

internal sealed class WuaDeepCheckReport
{
    public DateTimeOffset Started { get; init; }
    public DateTimeOffset Finished { get; set; }
    public string Machine { get; init; } = Environment.MachineName;
    public string Os { get; init; } = Environment.OSVersion.ToString();
    public string AppVersion { get; init; } = ProductVersion.Display;
    public bool Elevated { get; init; }
    public List<WuaDeepCheckFinding> Findings { get; init; } = new();
    public bool Completed { get; set; }

    internal static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MicroSystems", "Update Express", "Logs");
    internal static string LastJsonPath => Path.Combine(DirectoryPath, "wua-deep-check-last.json");
    internal static string LastTextPath => Path.Combine(DirectoryPath, "wua-deep-check-last.txt");

    internal int FailCount => Findings.Count(f => f.Status == "FAIL");
    internal int WarnCount => Findings.Count(f => f.Status == "WARN");

    internal string BuildAiPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are helping troubleshoot Windows Update Agent (WUA) when Quick/Medium/Deep fix did not help.");
        sb.AppendLine("Analyze the deep-check findings below. Prefer safe, ordered next steps. Do not invent facts not in the log.");
        sb.AppendLine();
        sb.AppendLine("=== Context ===");
        sb.AppendLine($"App: Update Express {AppVersion}");
        sb.AppendLine($"Machine: {Machine}");
        sb.AppendLine($"OS: {Os}");
        sb.AppendLine($"Elevated: {Elevated}");
        sb.AppendLine($"Started: {Started:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Finished: {Finished:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Summary: FAIL={FailCount}, WARN={WarnCount}, total={Findings.Count}");
        sb.AppendLine();
        sb.AppendLine("=== Findings ===");
        foreach (var f in Findings)
        {
            sb.AppendLine($"- [{f.Status}] {f.Area}: {f.Detail}");
            if (!string.IsNullOrWhiteSpace(f.Hint))
                sb.AppendLine($"  Hint: {f.Hint}");
        }
        sb.AppendLine();
        sb.AppendLine("=== Ask ===");
        sb.AppendLine("1) What are the most likely blockers for Windows Update on this PC?");
        sb.AppendLine("2) What should a technician try next (ordered, safe)?");
        sb.AppendLine("3) Is this more likely network/proxy, service, policy, or component-store related?");
        return sb.ToString();
    }

    internal void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(LastJsonPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.WriteAllText(LastTextPath, BuildAiPrompt(), new UTF8Encoding(false));
    }

    internal static string? LoadLatestPrompt()
    {
        try
        {
            if (File.Exists(LastTextPath)) return File.ReadAllText(LastTextPath);
            if (!File.Exists(LastJsonPath)) return null;
            return JsonSerializer.Deserialize<WuaDeepCheckReport>(File.ReadAllText(LastJsonPath))?.BuildAiPrompt();
        }
        catch { return null; }
    }
}

/// <summary>
/// Read-only deep health check for WUA when repair levels did not resolve the issue.
/// Estimated runtime: about 2–4 minutes depending on network timeouts.
/// </summary>
internal static class WuaDeepCheckService
{
    internal static readonly TimeSpan EstimatedDuration = TimeSpan.FromMinutes(3);

    private static readonly (string Host, int[] Ports)[] Endpoints =
    [
        ("www.microsoft.com", [443]),
        ("download.windowsupdate.com", [443, 80]),
        ("sls.update.microsoft.com", [443]),
        ("dl.delivery.mp.microsoft.com", [443]),
        ("update.microsoft.com", [443]),
        ("fe2.update.microsoft.com", [443])
    ];

    internal static async Task<WuaDeepCheckReport> RunAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        var report = new WuaDeepCheckReport
        {
            Started = DateTimeOffset.Now,
            Elevated = ElevationHelper.IsElevated
        };

        void Add(string area, string status, string detail, string? hint = null)
        {
            report.Findings.Add(new WuaDeepCheckFinding { Area = area, Status = status, Detail = detail, Hint = hint });
            progress?.Report($"[{status}] {area}");
        }

        progress?.Report("Collecting system context…");
        Add("System", "INFO",
            $"OS={Environment.OSVersion}; 64-bit process={Environment.Is64BitProcess}; User={Environment.UserDomainName}\\{Environment.UserName}",
            report.Elevated ? null : "Some WUA operations require administrator. Re-run elevated if installs fail.");

        try
        {
            var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var di = new DriveInfo(drive);
            long freeGb = di.AvailableFreeSpace / (1024 * 1024 * 1024);
            bool low = freeGb < 10;
            Add("Disk space", low ? "WARN" : "OK",
                $"{drive} free ≈ {freeGb} GB (total {di.TotalSize / (1024 * 1024 * 1024)} GB)",
                low ? "Low free space can block feature/cumulative updates. Free at least 10–20 GB." : null);
        }
        catch (Exception ex) { Add("Disk space", "WARN", ex.Message); }

        progress?.Report("Checking network adapters…");
        bool netUp = NetworkInterface.GetIsNetworkAvailable();
        Add("Network available", netUp ? "OK" : "FAIL",
            netUp ? "Windows reports a network is available." : "No network appears available.",
            netUp ? null : "Connect Ethernet/Wi-Fi before retrying Windows Update.");

        progress?.Report("Checking proxy settings…");
        try
        {
            string proxy = ProxyDiagnostics.ReadOnlyReport();
            Add("Proxy (read-only)", "INFO", Trim(proxy, 1200),
                "WinHTTP and user PAC can block WUA independently. Misconfigured proxy often shows as 0x8024402C / timeouts.");
        }
        catch (Exception ex) { Add("Proxy", "WARN", ex.Message); }

        progress?.Report("Checking Windows Update related services…");
        foreach (var name in new[] { "wuauserv", "bits", "cryptsvc", "msiserver", "UsoSvc", "DoSvc" })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string mode = "unknown";
                using (var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}"))
                {
                    mode = key?.GetValue("Start") switch
                    {
                        2 => "Automatic",
                        3 => "Manual",
                        4 => "Disabled",
                        _ => Convert.ToString(key?.GetValue("Start")) ?? "unknown"
                    };
                }
                var (code, output) = await RunCmdAsync($"sc query {name}", ct).ConfigureAwait(false);
                bool running = output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
                bool disabled = mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
                bool critical = name is "wuauserv" or "bits" or "cryptsvc";
                string status = disabled ? "FAIL" : (!running && critical ? "WARN" : (code == 0 || running ? "OK" : "WARN"));
                string state = running ? "Running" : (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ? "Stopped" : $"sc exit {code}");
                Add($"Service {name}", status, $"State={state}; Start={mode}",
                    disabled ? $"Enable and start {name}, then retry updates." :
                    !running && critical ? $"Start {name} (services.msc or net start {name})." : null);
            }
            catch (Exception ex)
            {
                Add($"Service {name}", "WARN", "Unable to query: " + ex.Message);
            }
        }

        progress?.Report("Probing Microsoft / Windows Update endpoints…");
        foreach (var (host, ports) in Endpoints)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"DNS/TCP: {host}…");
            var finding = await ProbeHostAsync(host, ports, ct).ConfigureAwait(false);
            report.Findings.Add(finding);
            progress?.Report($"[{finding.Status}] {finding.Area}");
        }

        progress?.Report("Checking reboot-pending indicators…");
        CheckRebootPending(Add);

        progress?.Report("Querying Windows Update Agent (read-only)…");
        await CheckWuaAsync(Add, ct).ConfigureAwait(false);

        progress?.Report("Checking update policy keys…");
        CheckPolicies(Add);

        report.Finished = DateTimeOffset.Now;
        report.Completed = true;
        try { report.Save(); } catch { }
        ActivityLog.TryWrite("WuaDeepCheck", report.FailCount > 0 ? "CompletedWithFailures" : "Completed",
            new { report.FailCount, report.WarnCount });
        return report;
    }

    private static async Task<WuaDeepCheckFinding> ProbeHostAsync(string host, int[] ports, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            return new WuaDeepCheckFinding
            {
                Area = $"Endpoint {host}",
                Status = "FAIL",
                Detail = $"DNS failed: {ex.Message}",
                Hint = "DNS/proxy/WSUS name resolution problem. Check network DNS and 0x8024402C guidance."
            };
        }
        if (addresses.Length == 0)
            return new WuaDeepCheckFinding { Area = $"Endpoint {host}", Status = "FAIL", Detail = "DNS returned no addresses.", Hint = "Check DNS." };

        bool connected = false;
        int okPort = 0;
        var errors = new List<string>();
        foreach (var port in ports)
        {
            try
            {
                using var client = new TcpClient();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(4));
                await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
                connected = true;
                okPort = port;
                break;
            }
            catch (Exception ex)
            {
                errors.Add($"TCP {port}: {ex.GetType().Name}");
            }
        }

        string icmp = "ICMP n/a";
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 900).ConfigureAwait(false);
            icmp = reply.Status == IPStatus.Success ? $"ICMP {reply.RoundtripTime}ms" : $"ICMP {reply.Status}";
        }
        catch { icmp = "ICMP blocked/unavailable"; }

        return new WuaDeepCheckFinding
        {
            Area = $"Endpoint {host}",
            Status = connected ? "OK" : "FAIL",
            Detail = $"DNS={addresses[0]}; {(connected ? $"TCP {okPort} OK" : "TCP failed (" + string.Join(", ", errors) + ")")}; {icmp}; {watch.ElapsedMilliseconds} ms",
            Hint = connected ? null : "Cannot reach this Microsoft update endpoint. Check firewall, proxy, TLS inspection, or offline network."
        };
    }

    private static void CheckRebootPending(Action<string, string, string, string?> add)
    {
        foreach (var path in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
            @"SYSTEM\CurrentControlSet\Control\Session Manager"
        })
        {
            try
            {
                if (path.EndsWith("Session Manager", StringComparison.Ordinal))
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path);
                    var pending = key?.GetValue("PendingFileRenameOperations");
                    bool has = pending is string[] arr && arr.Length > 0;
                    add("Reboot pending (file renames)", has ? "WARN" : "OK",
                        has ? "PendingFileRenameOperations present." : "No pending file renames.",
                        has ? "Restart Windows, then retry updates (0x80240016 often needs reboot)." : null);
                }
                else
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path);
                    bool has = key is not null;
                    add("Reboot pending (" + path[(path.LastIndexOf('\\') + 1)..] + ")", has ? "WARN" : "OK",
                        has ? "Indicator present." : "Not present.",
                        has ? "Restart Windows before further update attempts." : null);
                }
            }
            catch (Exception ex) { add("Reboot pending", "WARN", path + ": " + ex.Message, null); }
        }
    }

    private static async Task CheckWuaAsync(Action<string, string, string, string?> add, CancellationToken ct)
    {
        try
        {
            await WindowsUpdateReader.RunStaAsync(() =>
            {
                dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")
                    ?? throw new InvalidOperationException("Microsoft.Update.Session ProgID missing"))!;
                dynamic searcher = session.CreateUpdateSearcher();
                string online = Convert.ToString(searcher.Online) ?? "?";
                string serverSel = Convert.ToString(searcher.ServerSelection) ?? "?";
                add("WUA session", "OK", $"Online={online}; ServerSelection={serverSel}", null);
                try
                {
                    // Lightweight read-only search; may take time on broken agents.
                    dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
                    int count = (int)result.Updates.Count;
                    add("WUA search (software pending)", "OK",
                        $"Search succeeded. Pending software updates visible to WUA: {count}.",
                        count == 0 ? "Empty pending list can be normal, or WUA may not see Microsoft/WSUS offers." : null);
                }
                catch (Exception ex)
                {
                    add("WUA search (software pending)", "FAIL", ex.Message,
                        "WUA search failed. Prefer Quick fix, then Deep if component store corruption is suspected.");
                }
                return 0;
            }).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            add("WUA session", "FAIL", ex.Message,
                "Windows Update Agent COM is unavailable or hung. Restart wuauserv / reboot, then Quick fix.");
        }
    }

    private static void CheckPolicies(Action<string, string, string, string?> add)
    {
        foreach (var path in new[]
        {
            @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
            @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"
        })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key is null)
                {
                    add("Policy " + path, "OK", "No policy key (defaults).", null);
                    continue;
                }
                var names = key.GetValueNames();
                var parts = names.Select(n => n + "=" + FormatReg(key.GetValue(n))).ToList();
                add("Policy " + path, "INFO", parts.Count == 0 ? "(empty key)" : string.Join("; ", parts),
                    "WSUS/policy settings can redirect or block Microsoft Update. Verify intranet update location if used.");
            }
            catch (Exception ex) { add("Policy", "WARN", path + ": " + ex.Message, null); }
        }
    }

    private static string FormatReg(object? value) => value switch
    {
        string[] arr => string.Join(",", arr),
        null => "",
        _ => Convert.ToString(value) ?? ""
    };

    private static string Trim(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private static async Task<(int ExitCode, string Output)> RunCmdAsync(string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/c " + command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start: " + command);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        string text = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        return (process.ExitCode, text);
    }
}
