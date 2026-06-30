// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Net;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class TrustedNodesPrivilegedIpProviderTests
{
    private static readonly IPAddress TrustedIp = IPAddress.Parse("5.6.7.8");
    private static readonly IPAddress OtherIp = IPAddress.Parse("9.10.11.12");

    private static string Enode(PublicKey key, IPAddress ip) => new Enode(key, ip, 30303).ToString();

    private static ITrustedNodesManager WithNodes(params IPAddress[] ips)
    {
        ITrustedNodesManager manager = Substitute.For<ITrustedNodesManager>();
        manager.Nodes.Returns(ips.Select(ip => new NetworkNode(Enode(TestItem.PublicKeyB, ip))).ToArray());
        return manager;
    }

    [Test]
    public void Privileges_trusted_manager_ips_only()
    {
        TrustedNodesPrivilegedIpProvider provider = new(WithNodes(TrustedIp));

        Assert.That(provider.IsPrivileged(TrustedIp), Is.True);
        Assert.That(provider.IsPrivileged(OtherIp), Is.False);
    }

    [Test]
    public void Reflects_manager_changes_live()
    {
        ITrustedNodesManager manager = WithNodes(TrustedIp);
        TrustedNodesPrivilegedIpProvider provider = new(manager);
        Assert.That(provider.IsPrivileged(TrustedIp), Is.True);

        manager.Nodes.Returns([]); // admin_removeTrustedPeer
        Assert.That(provider.IsPrivileged(TrustedIp), Is.False, "live read reflects removal");
    }

    [Test]
    public void Normalizes_ipv4_mapped_ipv6()
    {
        TrustedNodesPrivilegedIpProvider provider = new(WithNodes(TrustedIp));

        Assert.That(provider.IsPrivileged(TrustedIp.MapToIPv6()), Is.True, "IPv4-mapped-IPv6 query matches an IPv4 trusted node");
    }
}
