// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
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

    private static IStaticNodesManager StaticManager(params IPAddress[] ips)
    {
        IStaticNodesManager manager = Substitute.For<IStaticNodesManager>();
        manager.Nodes.Returns(ips.Select(ip => new NetworkNode(Enode(TestItem.PublicKeyB, ip))).ToArray());
        return manager;
    }

    private static ITrustedNodesManager TrustedManager(params IPAddress[] ips)
    {
        ITrustedNodesManager manager = Substitute.For<ITrustedNodesManager>();
        manager.Nodes.Returns(ips.Select(ip => new NetworkNode(Enode(TestItem.PublicKeyC, ip))).ToArray());
        return manager;
    }

    [Test]
    public void Privileges_config_static_static_manager_and_trusted_ips()
    {
        PrivilegedIpProvider provider = Create(StaticManager(StaticManagerIp), TrustedManager(TrustedIp), Enode(TestItem.PublicKeyA, ConfigStaticIp));

        Assert.That(provider.IsPrivileged(ConfigStaticIp), Is.True, "config Network.StaticPeers");
        Assert.That(provider.IsPrivileged(StaticManagerIp), Is.True, "static-nodes.json / admin_addPeer");
        Assert.That(provider.IsPrivileged(TrustedIp), Is.True, "trusted-nodes.json / admin_addTrustedPeer");
        Assert.That(provider.IsPrivileged(OtherIp), Is.False, "unrelated IP");
    }

    [Test]
    public void Reflects_manager_changes_live()
    {
        IStaticNodesManager staticManager = StaticManager(StaticManagerIp);
        PrivilegedIpProvider provider = Create(staticManager, TrustedManager(), configStaticPeers: null);
        Assert.That(provider.IsPrivileged(StaticManagerIp), Is.True);

        staticManager.Nodes.Returns([]); // admin_removePeer
        Assert.That(provider.IsPrivileged(StaticManagerIp), Is.False, "live read reflects removal");
    }

    [Test]
    public void Normalizes_ipv4_mapped_ipv6()
    {
        PrivilegedIpProvider provider = Create(StaticManager(), TrustedManager(TrustedIp), null);

        Assert.That(provider.IsPrivileged(TrustedIp.MapToIPv6()), Is.True, "IPv4-mapped-IPv6 query matches an IPv4 node");
    }
}
