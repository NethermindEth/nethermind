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
public class StaticNodesPrivilegedIpProviderTests
{
    private static readonly IPAddress ConfigIp = IPAddress.Parse("1.2.3.4");
    private static readonly IPAddress ManagerIp = IPAddress.Parse("5.6.7.8");
    private static readonly IPAddress OtherIp = IPAddress.Parse("9.10.11.12");

    private static string Enode(PublicKey key, IPAddress ip) => new Enode(key, ip, 30303).ToString();

    private static StaticNodesPrivilegedIpProvider CreateProvider(IStaticNodesManager manager, string? configStaticPeers)
        => new(manager, new NetworkConfig { StaticPeers = configStaticPeers }, LimboLogs.Instance);

    [Test]
    public void Privileges_config_and_manager_static_ips_only()
    {
        IStaticNodesManager manager = Substitute.For<IStaticNodesManager>();
        manager.Nodes.Returns([new NetworkNode(Enode(TestItem.PublicKeyB, ManagerIp))]);

        StaticNodesPrivilegedIpProvider provider = CreateProvider(manager, Enode(TestItem.PublicKeyA, ConfigIp));

        Assert.That(provider.IsPrivileged(ConfigIp), Is.True, "config static IP");
        Assert.That(provider.IsPrivileged(ManagerIp), Is.True, "manager static IP");
        Assert.That(provider.IsPrivileged(OtherIp), Is.False, "unrelated IP");
    }

    [Test]
    public void Reflects_manager_changes_live()
    {
        IStaticNodesManager manager = Substitute.For<IStaticNodesManager>();
        manager.Nodes.Returns([new NetworkNode(Enode(TestItem.PublicKeyB, ManagerIp))]);

        StaticNodesPrivilegedIpProvider provider = CreateProvider(manager, configStaticPeers: null);
        Assert.That(provider.IsPrivileged(ManagerIp), Is.True);

        manager.Nodes.Returns([]); // admin_removePeer
        Assert.That(provider.IsPrivileged(ManagerIp), Is.False, "live read reflects removal without re-construction");
    }

    [Test]
    public void Normalizes_ipv4_mapped_ipv6()
    {
        IStaticNodesManager manager = Substitute.For<IStaticNodesManager>();
        manager.Nodes.Returns([]);

        StaticNodesPrivilegedIpProvider provider = CreateProvider(manager, Enode(TestItem.PublicKeyA, ConfigIp));

        Assert.That(provider.IsPrivileged(ConfigIp.MapToIPv6()), Is.True, "IPv4-mapped-IPv6 query matches an IPv4 static");
    }
}
