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

    /// <summary>One up, non-loopback adapter as Windows reports it, in enumeration order.</summary>
    internal sealed record Adapter(
        IReadOnlyList<(IPAddress Address, IPAddress? Mask)> Unicast,
        IReadOnlyList<IPAddress> Gateways,
        IReadOnlyList<IPAddress> Dns);

    private static readonly Info Empty = new("—", "—", "—", "—", "—");

    internal static Info Read()
    {
        try
        {
            var adapters = new List<Adapter>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var props = nic.GetIPProperties();
                adapters.Add(new Adapter(
                    props.UnicastAddresses
                        .Select(u => (u.Address, u.Address.AddressFamily == AddressFamily.InterNetwork ? u.IPv4Mask : null))
                        .ToList(),
                    props.GatewayAddresses.Select(g => g.Address).ToList(),
                    props.DnsAddresses.ToList()));
            }

            return Pick(adapters);
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>
    /// First adapter with a default gateway and a real (non-169.254) IPv4 address;
    /// else the first with a gateway; else the first with any IPv4 address.
    /// </summary>
    internal static Info Pick(IEnumerable<Adapter> adapters)
    {
        Info? bestRouted = null;
        Info? bestAny = null;

        foreach (var adapter in adapters)
        {
            (IPAddress Address, IPAddress? Mask)? ipv4 = null;
            foreach (var uni in adapter.Unicast)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(uni.Address)) continue;
                if (ipv4 is null || (IsApipa(ipv4.Value.Address) && !IsApipa(uni.Address)))
                    ipv4 = uni;
            }
            if (ipv4 is null) continue;

            string? gateway = FirstGateway(adapter.Gateways);
            var (primary, secondary) = DnsPair(adapter.Dns);

            var info = new Info(
                Show(ipv4.Value.Address.ToString()),
                Show(ipv4.Value.Mask?.ToString()),
                Show(gateway),
                Show(primary),
                Show(secondary));

            bool routed = gateway is not null;
            if (routed && !IsApipa(ipv4.Value.Address))
                return info;

            if (routed)
                bestRouted ??= info;
            else
                bestAny ??= info;
        }

        return bestRouted ?? bestAny ?? Empty;
    }

    /// <summary>IPv4 gateway when the adapter has one, otherwise its IPv6 gateway.</summary>
    private static string? FirstGateway(IReadOnlyList<IPAddress> gateways)
    {
        string? v6 = null;
        foreach (var g in gateways)
        {
            // Some adapters report 0.0.0.0 / :: as a gateway; that is not a default route.
            if (IPAddress.IsLoopback(g) || g.Equals(IPAddress.Any) || g.Equals(IPAddress.IPv6Any)) continue;
            if (g.AddressFamily == AddressFamily.InterNetwork)
                return g.ToString();
            if (g.AddressFamily == AddressFamily.InterNetworkV6)
                v6 ??= g.ToString();
        }
        return v6;
    }

    /// <summary>Configured DNS servers in order, any address family (IPv4 or IPv6).</summary>
    private static (string? Primary, string? Secondary) DnsPair(IReadOnlyList<IPAddress> servers)
    {
        string? primary = null;
        foreach (var dns in servers)
        {
            if (dns.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                continue;
            // 127.0.0.1 / ::1 stay: a local resolver is a server the user configured.
            if (IsPlaceholderDns(dns)) continue;
            if (primary is null) primary = dns.ToString();
            else return (primary, dns.ToString());
        }
        return (primary, null);
    }

    /// <summary>
    /// fec0:0:0:ffff::1, ::2 and ::3: Windows lists these on an adapter that has
    /// IPv6 but no DNS server configured. They are not servers anyone set up.
    /// </summary>
    private static bool IsPlaceholderDns(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var b = address.GetAddressBytes();
        if (b[0] != 0xfe || b[1] != 0xc0 || b[6] != 0xff || b[7] != 0xff) return false;
        for (int i = 2; i < 15; i++)
        {
            if (i is 6 or 7) continue;
            if (b[i] != 0) return false;
        }
        return b[15] is >= 1 and <= 3;
    }

    private static bool IsApipa(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static string Show(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "—" ? "—" : value.Trim();
}
