// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class PrivilegedIpProviderTests
{
    private static readonly IPAddress ConfigStaticIp = IPAddress.Parse("1.2.3.4");
    private static readonly IPAddress StaticManagerIp = IPAddress.Parse("5.6.7.8");
    private static readonly IPAddress TrustedIp = IPAddress.Parse("9.10.11.12");
    private static readonly IPAddress OtherIp = IPAddress.Parse("13.14.15.16");

    private static string Enode(PublicKey key, IPAddress ip) => new Enode(key, ip, 30303).ToString();

    private static PrivilegedIpProvider Create(IStaticNodesManager staticManager, ITrustedNodesManager trustedManager, string? configStaticPeers) =>
        new(staticManager, trustedManager, new NetworkConfig { StaticPeers = configStaticPeers }, LimboLogs.Instance);

    [Test]
    public void Privileges_config_static_static_manager_and_trusted_ips()
    {
        IStaticNodesManager staticManager = Substitute.For<IStaticNodesManager>();
        staticManager.ContainsIp(StaticManagerIp).Returns(true);
        ITrustedNodesManager trustedManager = Substitute.For<ITrustedNodesManager>();
        trustedManager.ContainsIp(TrustedIp).Returns(true);

        PrivilegedIpProvider provider = Create(staticManager, trustedManager, Enode(TestItem.PublicKeyA, ConfigStaticIp));

        Assert.That(provider.IsPrivileged(ConfigStaticIp), Is.True, "config Network.StaticPeers");
        Assert.That(provider.IsPrivileged(StaticManagerIp), Is.True, "static-nodes.json / admin_addPeer");
        Assert.That(provider.IsPrivileged(TrustedIp), Is.True, "trusted-nodes.json / admin_addTrustedPeer");
        Assert.That(provider.IsPrivileged(OtherIp), Is.False, "unrelated IP");
    }

    [Test]
    public void Delegates_membership_to_the_managers_live()
    {
        IStaticNodesManager staticManager = Substitute.For<IStaticNodesManager>();
        PrivilegedIpProvider provider = Create(staticManager, Substitute.For<ITrustedNodesManager>(), configStaticPeers: null);

        Assert.That(provider.IsPrivileged(StaticManagerIp), Is.False);

        staticManager.ContainsIp(StaticManagerIp).Returns(true); // e.g. admin_addPeer
        Assert.That(provider.IsPrivileged(StaticManagerIp), Is.True, "the manager is queried on each call, not cached");
    }

    [Test]
    public void Config_static_ip_is_normalized_ipv4_mapped_ipv6()
    {
        PrivilegedIpProvider provider = Create(Substitute.For<IStaticNodesManager>(), Substitute.For<ITrustedNodesManager>(),
            Enode(TestItem.PublicKeyA, ConfigStaticIp));

        Assert.That(provider.IsPrivileged(ConfigStaticIp.MapToIPv6()), Is.True, "IPv4-mapped-IPv6 query matches an IPv4 config static peer");
    }
}
