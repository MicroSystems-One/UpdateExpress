using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UpdateExpress.Services;

/// <summary>
/// Active LAN adapter addressing for the PC details dialog.
/// Prefers the routed (default-gateway) adapter over virtual/up-but-unrouted NICs.
/// UI-only — do not write these values into logs.
/// </summary>
internal static class LocalNetworkReader
{
    internal sealed record Info(
        string LocalIp,
        string SubnetMask,
        string Gateway,
        string PrimaryDns,
        string SecondaryDns);

    internal static Info Read()
    {
        try
        {
            Info? bestRouted = null;
            Info? bestAny = null;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var props = nic.GetIPProperties();
                UnicastIPAddressInformation? ipv4 = null;
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(uni.Address)) continue;
                    // Prefer non-link-local when choosing among IPv4 addresses on one NIC.
                    if (IsApipa(uni.Address) && ipv4 is not null) continue;
                    if (ipv4 is null || (IsApipa(ipv4.Address) && !IsApipa(uni.Address)))
                        ipv4 = uni;
                }
                if (ipv4 is null) continue;

                string gateway = FirstGateway(props) ?? "—";
                var (primary, secondary) = DnsPair(props);

                var info = new Info(
                    Show(ipv4.Address.ToString()),
                    Show(ipv4.IPv4Mask?.ToString()),
                    Show(gateway),
                    Show(primary),
                    Show(secondary));

                bool routed = gateway != "—";
                bool apipa = IsApipa(ipv4.Address);
                if (routed && !apipa)
                {
                    // Prefer the first up adapter that has a default route and a real LAN address.
                    bestRouted = info;
                    break;
                }

                if (routed)
                    bestRouted ??= info;
                else
                    bestAny ??= info;
            }

            return bestRouted ?? bestAny ?? new("—", "—", "—", "—", "—");
        }
        catch
        {
            return new("—", "—", "—", "—", "—");
        }
    }

    private static string? FirstGateway(IPInterfaceProperties props)
    {
        string? v4 = null;
        string? v6 = null;
        foreach (var g in props.GatewayAddresses)
        {
            if (IPAddress.IsLoopback(g.Address)) continue;
            if (g.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                v4 ??= g.Address.ToString();
                break;
            }
            if (g.Address.AddressFamily == AddressFamily.InterNetworkV6)
                v6 ??= g.Address.ToString();
        }
        return v4 ?? v6;
    }

    /// <summary>Configured DNS servers in order, any address family (IPv4 or IPv6).</summary>
    private static (string Primary, string Secondary) DnsPair(IPInterfaceProperties props)
    {
        string primary = "—";
        string secondary = "—";
        int index = 0;
        foreach (var dns in props.DnsAddresses)
        {
            if (IPAddress.IsLoopback(dns)) continue;
            if (dns.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                continue;
            if (index == 0) primary = dns.ToString();
            else if (index == 1) { secondary = dns.ToString(); break; }
            index++;
        }
        return (primary, secondary);
    }

    private static bool IsApipa(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static string Show(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "—" ? "—" : value.Trim();
}
