using System.Net;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public class LocalNetworkReaderTests
{
    private const string None = "—";

    [Fact]
    public void Pick_UnroutedVirtualAdapterFirst_ChoosesAdapterWithGateway()
    {
        var result = LocalNetworkReader.Pick(new[]
        {
            Adapter(new[] { "172.24.80.1/255.255.240.0" }),
            Adapter(new[] { "192.168.1.50/255.255.255.0" }, new[] { "192.168.1.1" }, new[] { "192.168.1.1", "1.1.1.1" })
        });

        Assert.Equal(new LocalNetworkReader.Info("192.168.1.50", "255.255.255.0", "192.168.1.1", "192.168.1.1", "1.1.1.1"), result);
    }

    [Fact]
    public void Pick_NoAdapterHasGateway_FallsBackToFirstWithIpv4()
    {
        var result = LocalNetworkReader.Pick(new[]
        {
            Adapter(new[] { "fe80::1" }),
            Adapter(new[] { "10.0.0.5/255.0.0.0" }),
            Adapter(new[] { "172.24.80.1/255.255.240.0" })
        });

        Assert.Equal(new LocalNetworkReader.Info("10.0.0.5", "255.0.0.0", None, None, None), result);
    }

    [Fact]
    public void Pick_NoAdapters_ReturnsDashes()
    {
        var result = LocalNetworkReader.Pick(Array.Empty<LocalNetworkReader.Adapter>());

        Assert.Equal(new LocalNetworkReader.Info(None, None, None, None, None), result);
    }

    [Fact]
    public void Pick_RoutedApipaBeforeRoutedLan_ChoosesLan()
    {
        var result = LocalNetworkReader.Pick(new[]
        {
            Adapter(new[] { "169.254.10.20/255.255.0.0" }, new[] { "fe80::1" }),
            Adapter(new[] { "192.168.1.50/255.255.255.0" }, new[] { "192.168.1.1" })
        });

        Assert.Equal("192.168.1.50", result.LocalIp);
        Assert.Equal("192.168.1.1", result.Gateway);
    }

    [Fact]
    public void Pick_OnlyRoutedAdapterIsApipa_ChoosesItOverUnrouted()
    {
        var result = LocalNetworkReader.Pick(new[]
        {
            Adapter(new[] { "172.24.80.1/255.255.240.0" }),
            Adapter(new[] { "169.254.10.20/255.255.0.0" }, new[] { "fe80::1" })
        });

        Assert.Equal("169.254.10.20", result.LocalIp);
        Assert.Equal("fe80::1", result.Gateway);
    }

    [Fact]
    public void Pick_ApipaAndLanAddressOnOneAdapter_ShowsLanAddress()
    {
        var result = LocalNetworkReader.Pick(new[]
        {
            Adapter(new[] { "169.254.10.20/255.255.0.0", "192.168.1.50/255.255.255.0" }, new[] { "192.168.1.1" })
        });

        Assert.Equal("192.168.1.50", result.LocalIp);
        Assert.Equal("255.255.255.0", result.SubnetMask);
    }

    [Fact]
    public void Pick_Ipv6GatewayListedBeforeIpv4_ShowsIpv4Gateway()
    {
        var result = LocalNetworkReader.Pick(new[]
        {
            Adapter(new[] { "10.0.2.15/255.255.255.0" }, new[] { "fe80::2", "10.0.2.2" })
        });

        Assert.Equal("10.0.2.2", result.Gateway);
    }

    [Fact]
    public void Pick_OnlyIpv6Gateway_ShowsIt()
    {
        var result = LocalNetworkReader.Pick(new[]
        {
            Adapter(new[] { "192.168.1.50/255.255.255.0" }, new[] { "fe80::1" })
        });

        Assert.Equal("fe80::1", result.Gateway);
    }

    [Fact]
    public void Pick_ZeroGateway_DoesNotCountAsRouted()
    {
        var result = LocalNetworkReader.Pick(new[]
        {
            Adapter(new[] { "10.8.0.2/255.255.255.0" }, new[] { "0.0.0.0", "::" }),
            Adapter(new[] { "192.168.1.50/255.255.255.0" }, new[] { "192.168.1.1" })
        });

        Assert.Equal("192.168.1.50", result.LocalIp);
    }

    [Fact]
    public void Pick_Ipv6DnsFirst_KeepsConfiguredOrder()
    {
        var result = LocalNetworkReader.Pick(new[]
        {
            Adapter(
                new[] { "192.168.1.50/255.255.255.0" },
                new[] { "192.168.1.1" },
                new[] { "2001:4860:4860::8888", "192.168.1.1", "8.8.8.8" })
        });

        Assert.Equal("2001:4860:4860::8888", result.PrimaryDns);
        Assert.Equal("192.168.1.1", result.SecondaryDns);
    }

    [Fact]
    public void Pick_OnlyIpv6Dns_ShowsBoth()
    {
        var result = LocalNetworkReader.Pick(new[]
        {
            Adapter(
                new[] { "192.168.1.50/255.255.255.0" },
                new[] { "192.168.1.1" },
                new[] { "2001:4860:4860::8888", "2001:4860:4860::8844" })
        });

        Assert.Equal("2001:4860:4860::8888", result.PrimaryDns);
        Assert.Equal("2001:4860:4860::8844", result.SecondaryDns);
    }

    [Fact]
    public void Pick_WindowsPlaceholderDns_IsNotShownAsDns()
    {
        var result = LocalNetworkReader.Pick(new[]
        {
            Adapter(
                new[] { "192.168.1.50/255.255.255.0" },
                new[] { "192.168.1.1" },
                new[] { "fec0:0:0:ffff::1", "fec0:0:0:ffff::2", "fec0:0:0:ffff::3" })
        });

        Assert.Equal(None, result.PrimaryDns);
        Assert.Equal(None, result.SecondaryDns);
    }

    [Fact]
    public void Pick_PlaceholderDnsBeforeRealServer_ShowsRealServerAsPrimary()
    {
        var result = LocalNetworkReader.Pick(new[]
        {
            Adapter(
                new[] { "192.168.1.50/255.255.255.0" },
                new[] { "192.168.1.1" },
                new[] { "fec0:0:0:ffff::1", "192.168.1.1", "fec0::5" })
        });

        Assert.Equal("192.168.1.1", result.PrimaryDns);
        Assert.Equal("fec0::5", result.SecondaryDns);
    }

    /// <param name="unicast">"address" or "address/mask".</param>
    private static LocalNetworkReader.Adapter Adapter(string[] unicast, string[]? gateways = null, string[]? dns = null) =>
        new(
            unicast.Select(u =>
            {
                var parts = u.Split('/');
                return (IPAddress.Parse(parts[0]), parts.Length > 1 ? IPAddress.Parse(parts[1]) : (IPAddress?)null);
            }).ToList(),
            (gateways ?? Array.Empty<string>()).Select(IPAddress.Parse).ToList(),
            (dns ?? Array.Empty<string>()).Select(IPAddress.Parse).ToList());
}
