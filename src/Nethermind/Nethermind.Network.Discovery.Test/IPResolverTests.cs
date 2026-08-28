// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Config;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

[Parallelizable(ParallelScope.All)]
public class IPResolverTests
{
    [Test]
    public void Nethermind_ip_preserves_positional_api()
    {
        IPAddress localIp = IPAddress.Loopback;
        IPAddress externalIp = IPAddress.Parse("192.0.2.1");
        IIPResolver.NethermindIp ip = new(LocalIp: localIp, ExternalIp: externalIp);

        (IPAddress deconstructedLocalIp, IPAddress deconstructedExternalIp) = ip;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deconstructedLocalIp, Is.SameAs(localIp));
            Assert.That(deconstructedExternalIp, Is.SameAs(externalIp));
        }
    }

    [Test]
    public async Task Ipv6_only_override_does_not_become_primary()
    {
        IPAddress externalIpV6 = IPAddress.Parse("2001:db8::1");
        IPResolver ipResolver = new(
            new NetworkConfig { ExternalIpV6 = externalIpV6.ToString() },
            LimboLogs.Instance);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();
        IPAddress? expectedExternalIpV4 = ip.ExternalIp.AddressFamily == AddressFamily.InterNetwork &&
            !ip.ExternalIp.Equals(IPAddress.Any) &&
            !ip.ExternalIp.Equals(IPAddress.None)
            ? ip.ExternalIp
            : null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.Not.EqualTo(externalIpV6));
            Assert.That(ip.ExternalIpV4, Is.EqualTo(expectedExternalIpV4));
            Assert.That(ip.ExternalIpV6, Is.EqualTo(externalIpV6));
        }
    }

    [TestCase("99.99.99.99", "99.99.99.99", "99.99.99.99", null)]
    [TestCase("10.50.50.50", "10.50.50.50", "10.50.50.50", null)]
    [TestCase("::ffff:192.0.2.1", "192.0.2.1", "192.0.2.1", null)]
    [TestCase("2001:db8::1", "2001:db8::1", null, "2001:db8::1")]
    public async Task Can_resolve_external_ip_with_override(
        string ipOverride,
        string expectedExternalIp,
        string? expectedExternalIpV4,
        string? expectedExternalIpV6)
    {
        INetworkConfig networkConfig = new NetworkConfig { ExternalIp = ipOverride };
        IPResolver ipResolver = new(networkConfig, LimboLogs.Instance);
        IIPResolver.NethermindIp ip = await ipResolver.Resolve();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.Parse(expectedExternalIp)));
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

    [TestCase("192.0.2.1")]
    [TestCase("::")]
    [TestCase("::ffff:198.51.100.2")]
    public async Task Invalid_ipv6_override_is_ignored(string externalIpV6)
    {
        INetworkConfig networkConfig = new NetworkConfig
        {
            ExternalIpV4 = "192.0.2.1",
            ExternalIpV6 = externalIpV6
        };
        IPResolver ipResolver = new(networkConfig, LimboLogs.Instance);

        IIPResolver.NethermindIp ip = await ipResolver.Resolve();

        Assert.That(ip.ExternalIpV6, Is.Null);
    }

    [TestCase("192.0.2.1", null, null, "192.0.2.1", null)]
    [TestCase("2001:db8::1", null, null, null, "2001:db8::1")]
    [TestCase("::ffff:198.51.100.2", null, null, "198.51.100.2", null)]
    [TestCase("192.0.2.1", null, "2001:db8::1", "192.0.2.1", "2001:db8::1")]
    [TestCase("2001:db8::1", "192.0.2.1", null, "192.0.2.1", "2001:db8::1")]
    [TestCase("192.0.2.1", null, "192.0.2.2", "192.0.2.1", null)] // wrong-family override ignored
    [TestCase("192.0.2.1", null, "::", "192.0.2.1", null)] // unspecified override ignored
    public void NethermindIp_derives_family_addresses(
        string externalIp,
        string? externalIpV4,
        string? externalIpV6,
        string? expectedIpV4,
        string? expectedIpV6)
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
    public void NethermindIp_recomputes_derived_family_addresses_after_with_expression()
    {
        IIPResolver.NethermindIp original = new(IPAddress.Loopback, IPAddress.Parse("2001:db8::1"));

        IIPResolver.NethermindIp changed = original with { ExternalIp = IPAddress.Parse("192.0.2.1") };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed.ExternalIpV4, Is.EqualTo(IPAddress.Parse("192.0.2.1")));
            Assert.That(changed.ExternalIpV6, Is.Null);
        }
    }

    [TestCase("2001:db8::1", "192.0.2.1", null, "2001:db8::2", "192.0.2.1", "2001:db8::2")]
    [TestCase("192.0.2.1", null, "2001:db8::1", "198.51.100.1", "198.51.100.1", "2001:db8::1")]
    public void NethermindIp_preserves_explicit_family_override_after_with_expression(
        string externalIp,
        string? externalIpV4,
        string? externalIpV6,
        string changedExternalIp,
        string expectedIpV4,
        string expectedIpV6)
    {
        IIPResolver.NethermindIp original = new(
            IPAddress.Loopback,
            IPAddress.Parse(externalIp),
            externalIpV4 is null ? null : IPAddress.Parse(externalIpV4),
            externalIpV6 is null ? null : IPAddress.Parse(externalIpV6));

        IIPResolver.NethermindIp changed = original with { ExternalIp = IPAddress.Parse(changedExternalIp) };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed.ExternalIpV4, Is.EqualTo(IPAddress.Parse(expectedIpV4)));
            Assert.That(changed.ExternalIpV6, Is.EqualTo(IPAddress.Parse(expectedIpV6)));
        }
    }

    [Test]
    public void NethermindIp_equality_compares_resolved_addresses()
    {
        IPAddress externalIpV6 = IPAddress.Parse("2001:db8::1");
        IIPResolver.NethermindIp derived = new(IPAddress.IPv6Any, externalIpV6);
        IIPResolver.NethermindIp overridden = new(
            IPAddress.IPv6Any,
            externalIpV6,
            externalIpV4: null,
            externalIpV6);

        Assert.That(overridden, Is.EqualTo(derived));
        Assert.That(overridden.GetHashCode(), Is.EqualTo(derived.GetHashCode()));
    }

    [TestCase("192.0.2.1", "192.0.2.2", null, nameof(NetworkConfig.ExternalIpV4))]
    [TestCase("2001:db8::1", null, "2001:db8::2", nameof(NetworkConfig.ExternalIpV6))]
    public async Task Warns_when_primary_and_family_override_disagree(
        string externalIp,
        string? externalIpV4,
        string? externalIpV6,
        string familyConfigName)
    {
        InterfaceLogger underlyingLogger = Substitute.For<InterfaceLogger>();
        underlyingLogger.IsWarn.Returns(true);
        ILogger logger = new(underlyingLogger);
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<IPResolver>().Returns(logger);
        IPResolver ipResolver = new(
            new NetworkConfig
            {
                ExternalIp = externalIp,
                ExternalIpV4 = externalIpV4,
                ExternalIpV6 = externalIpV6
            },
            logManager);

        await ipResolver.Resolve();

        underlyingLogger.Received(1).Warn(Arg.Is<string>(message =>
            message.Contains($"disagrees with {familyConfigName}")));
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
