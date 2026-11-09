using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateExpress.Services;

// DNS, TCP and optional ICMP checks are diagnostic only; WUA status is reported separately.
internal sealed class ConnectivityMonitor : IDisposable
{
    public sealed record Probe(bool Ok, string Message, string Detail);
    public event Action<Probe, Probe, string>? Updated;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private static readonly string[] Hosts =
    {
        "download.windowsupdate.com",
        "sls.update.microsoft.com",
        "dl.delivery.mp.microsoft.com"
    };
    public void Start() => _worker ??= Task.Run(() => RunAsync(_stop.Token));
    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var internet = await TestHostAsync("www.microsoft.com", new[] { 443 }, token);
                var results = new List<(string Host, Probe Result)>();
                foreach (var host in Hosts)
                {
                    results.Add((host, await TestHostAsync(host,
                        host == "download.windowsupdate.com" ? new[] { 443, 80 } : new[] { 443 }, token)));
                }
                int good = 0;
                var details = new List<string>();
                foreach (var result in results)
                {
                    if (result.Result.Ok) good++;
                    details.Add(result.Result.Detail);
                }
                var update = new Probe(good > 0,
                    good == Hosts.Length ? "TCP OK" : good > 0 ? $"Partial ({good}/{Hosts.Length})" : "TCP failed",
                    string.Join(Environment.NewLine, details) +
                    Environment.NewLine + "ICMP is diagnostic only. TCP connectivity does not confirm WUA or TLS.");
                Updated?.Invoke(internet, update, ProxyDescription());
                await Task.Delay(internet.Ok && good == Hosts.Length ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Updated?.Invoke(new Probe(false, "Error", ex.Message),
                    new Probe(false, "Error", ex.ToString()), ProxyDescription());
                try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
    private static string ProxyDescription()
    {
        try
        {
            var url = new Uri("https://download.windowsupdate.com/");
            var proxy = HttpClientDefaultProxy();
            if (proxy.IsBypassed(url)) return "Proxy: Direct (.NET)";
            var endpoint = proxy.GetProxy(url);
            return endpoint is null || endpoint == url ? "Proxy: Direct (.NET)" : "Proxy: " + endpoint.Host + ":" + endpoint.Port + " (.NET)";
        }
        catch { return "Proxy: Unknown"; }
    }
    private static IWebProxy HttpClientDefaultProxy() => System.Net.Http.HttpClient.DefaultProxy;
    private static async Task<Probe> TestHostAsync(string host, int[] ports, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host, token); }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            if (token.IsCancellationRequested) throw;
            return new Probe(false, "DNS error", host + ": " + ex.Message);
        }
        if (addresses.Length == 0) return new Probe(false, "DNS error", host + ": no addresses");
        bool connected = false;
        int connectedPort = 0;
        foreach (var port in ports)
        {
            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(host, port, timeout.Token);
                connected = true; connectedPort = port; break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException) { }
        }
        string pingStatus;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(900), Array.Empty<byte>(), null, token);
            pingStatus = reply.Status == IPStatus.Success ? $"ICMP OK ({reply.RoundtripTime} ms)" : "ICMP " + reply.Status;
        }
        catch (Exception ex) when (ex is PingException or SocketException or OperationCanceledException)
        {
            if (token.IsCancellationRequested) throw;
            pingStatus = "ICMP unavailable";
        }
        string detail = $"{host}: DNS {addresses[0]}; " +
            (connected ? $"TCP {connectedPort} OK" : "TCP failed") +
            $"; {pingStatus}; {watch.ElapsedMilliseconds} ms";
        return new Probe(connected, connected ? "Connected" : "TCP failed", detail);
    }
    public void Dispose()
    {
        _stop.Cancel();
        // Do not dispose the token source while an in-flight asynchronous probe still uses it.
        // The worker exits when its cancellation token is observed.
    }
}
