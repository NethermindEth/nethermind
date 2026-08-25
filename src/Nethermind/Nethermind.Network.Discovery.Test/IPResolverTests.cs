// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Config;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

[Parallelizable(ParallelScope.All)]
public class IPResolverTests
{
    [Test]
    public async Task Can_resolve_ip_without_override()
    {
        IPResolver ipResolver = new(new NetworkConfig(), LimboLogs.Instance);
        IIPResolver.NethermindIp ip = await ipResolver.Resolve();
        Assert.That(ip.LocalIp, Is.Not.Null);
        Assert.That(ip.ExternalIp, Is.Not.Null);
    }

    [TestCase("99.99.99.99", "99.99.99.99", null)]
    [TestCase("10.50.50.50", "10.50.50.50", null)]
    [TestCase("2001:db8::1", null, "2001:db8::1")]
    public async Task Can_resolve_external_ip_with_override(string ipOverride, string? expectedExternalIpV4, string? expectedExternalIpV6)
    {
        INetworkConfig networkConfig = new NetworkConfig { ExternalIp = ipOverride };
        IPResolver ipResolver = new(networkConfig, LimboLogs.Instance);
        IIPResolver.NethermindIp ip = await ipResolver.Resolve();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.Parse(ipOverride)));
            Assert.That(ip.ExternalIpV4, Is.EqualTo(expectedExternalIpV4 is null ? null : IPAddress.Parse(expectedExternalIpV4)));
            Assert.That(ip.ExternalIpV6, Is.EqualTo(expectedExternalIpV6 is null ? null : IPAddress.Parse(expectedExternalIpV6)));
        }
    }

    [Test]
    public async Task Can_resolve_dual_stack_external_ip_overrides()
    {
        INetworkConfig networkConfig = new NetworkConfig
        {
            ExternalIpV4 = "192.0.2.1",
            ExternalIpV6 = "2001:db8::1"
        };
        IPResolver ipResolver = new(networkConfig, LimboLogs.Instance);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.Parse("192.0.2.1")));
            Assert.That(ip.ExternalIpV4, Is.EqualTo(IPAddress.Parse("192.0.2.1")));
            Assert.That(ip.ExternalIpV6, Is.EqualTo(IPAddress.Parse("2001:db8::1")));
        }
    }

    [Test]
    public async Task Can_resolve_ipv6_only_override_without_becoming_primary()
    {
        INetworkConfig networkConfig = new NetworkConfig { ExternalIpV6 = "2001:db8::1" };
        IPResolver ipResolver = new(networkConfig, LimboLogs.Instance);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIpV6, Is.EqualTo(IPAddress.Parse("2001:db8::1")));
            Assert.That(ip.ExternalIp, Is.Not.EqualTo(IPAddress.Parse("2001:db8::1")));
        }
    }

    [Test]
    public async Task Mapped_unspecified_ipv4_override_does_not_suppress_resolution()
    {
        INetworkConfig networkConfig = new NetworkConfig { ExternalIpV4 = "::ffff:0.0.0.0" };
        IPResolver ipResolver = new(networkConfig, LimboLogs.Instance);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        Assert.That(ip.ExternalIp, Is.Not.EqualTo(IPAddress.Any));
    }

    [Test]
    public void NethermindIp_with_ExternalIp_recomputes_family_addresses()
    {
        IIPResolver.NethermindIp ip = new(IPAddress.Loopback, IPAddress.Parse("192.0.2.1"));

        IIPResolver.NethermindIp copied = ip with { ExternalIp = IPAddress.Parse("198.51.100.2") };

        Assert.That(copied.ExternalIpV4, Is.EqualTo(IPAddress.Parse("198.51.100.2")));
    }

    [Test]
    public void NethermindIp_deconstructs_to_local_and_external_addresses()
    {
        IIPResolver.NethermindIp ip = new(IPAddress.Loopback, IPAddress.Parse("192.0.2.1"));

        (IPAddress localIp, IPAddress externalIp) = ip;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(localIp, Is.EqualTo(IPAddress.Loopback));
            Assert.That(externalIp, Is.EqualTo(IPAddress.Parse("192.0.2.1")));
        }
    }

    [Test]
    public void NethermindIp_supports_named_constructor_arguments()
    {
        IIPResolver.NethermindIp ip = new(
            LocalIp: IPAddress.Loopback,
            ExternalIp: IPAddress.Parse("192.0.2.1"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.LocalIp, Is.EqualTo(IPAddress.Loopback));
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.Parse("192.0.2.1")));
        }
    }

    [TestCase("192.0.2.1", null, null, "192.0.2.1", null)]
    [TestCase("2001:db8::1", null, null, null, "2001:db8::1")]
    [TestCase("::ffff:198.51.100.2", null, null, "198.51.100.2", null)]
    [TestCase("192.0.2.1", "2001:db8::1", null, "192.0.2.1", null)] // wrong-family override ignored
    [TestCase("192.0.2.1", "::ffff:198.51.100.2", null, "198.51.100.2", null)] // mapped override normalized
    [TestCase("192.0.2.1", "0.0.0.0", null, "192.0.2.1", null)] // unspecified override ignored
    [TestCase("192.0.2.1", "255.255.255.255", null, "192.0.2.1", null)] // None override ignored
    [TestCase("192.0.2.1", "::", null, "192.0.2.1", null)] // IPv6Any override ignored
    [TestCase("192.0.2.1", "::ffff:0.0.0.0", null, "192.0.2.1", null)] // mapped unspecified override ignored
    [TestCase("192.0.2.1", null, "192.0.2.1", "192.0.2.1", null)] // wrong-family IPv6 override ignored
    public void NethermindIp_derives_family_addresses(
        string externalIp, string? externalIpV4, string? externalIpV6, string? expectedIpV4, string? expectedIpV6)
    {
        IIPResolver.NethermindIp ip = new(
            IPAddress.Loopback,
            IPAddress.Parse(externalIp),
            externalIpV4 is null ? null : IPAddress.Parse(externalIpV4),
            externalIpV6 is null ? null : IPAddress.Parse(externalIpV6));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIpV4, Is.EqualTo(expectedIpV4 is null ? null : IPAddress.Parse(expectedIpV4)));
            Assert.That(ip.ExternalIpV6, Is.EqualTo(expectedIpV6 is null ? null : IPAddress.Parse(expectedIpV6)));
        }
    }

    [Test]
    public async Task Can_resolve_external_ip_and_family_override_independently()
    {
        INetworkConfig networkConfig = new NetworkConfig
        {
            ExternalIp = "192.0.2.1",
            ExternalIpV4 = "198.51.100.2"
        };
        IPResolver ipResolver = new(networkConfig, LimboLogs.Instance);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.Parse("192.0.2.1")));
            Assert.That(ip.ExternalIpV4, Is.EqualTo(IPAddress.Parse("198.51.100.2")));
        }
    }

    [Test]
    public async Task Can_resolve_local_ip_with_override()
    {
        const string ipOverride = "99.99.99.99";
        INetworkConfig networkConfig = new NetworkConfig { LocalIp = ipOverride };
        IPResolver ipResolver = new(networkConfig, LimboLogs.Instance);
        IIPResolver.NethermindIp ip = await ipResolver.Resolve();
        Assert.That(ip.LocalIp, Is.EqualTo(IPAddress.Parse(ipOverride)));
    }
}
