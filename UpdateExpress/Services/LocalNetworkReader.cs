using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UpdateExpress.Services;

/// <summary>
/// Active LAN adapter addressing for the PC details dialog.
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
                    ipv4 = uni;
                    break;
                }
                if (ipv4 is null) continue;

                string gateway = "—";
                foreach (var g in props.GatewayAddresses)
                {
                    if (g.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(g.Address)) continue;
                    gateway = g.Address.ToString();
                    break;
                }

                string primary = "—";
                string secondary = "—";
                int dnsIndex = 0;
                foreach (var dns in props.DnsAddresses)
                {
                    if (dns.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (dnsIndex == 0) primary = dns.ToString();
                    else if (dnsIndex == 1) { secondary = dns.ToString(); break; }
                    dnsIndex++;
                }

                return new(
                    Show(ipv4.Address.ToString()),
                    Show(ipv4.IPv4Mask?.ToString()),
                    Show(gateway),
                    Show(primary),
                    Show(secondary));
            }
        }
        catch { }

        return new("—", "—", "—", "—", "—");
    }

    private static string Show(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "—" ? "—" : value.Trim();
}
