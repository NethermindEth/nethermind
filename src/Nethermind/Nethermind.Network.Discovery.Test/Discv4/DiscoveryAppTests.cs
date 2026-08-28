// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4;

public class DiscoveryAppTests
{
    [Test]
    public void Should_use_discovery_port_from_configured_enode_bootnode()
    {
        Enode enode = new(TestItem.PrivateKeyA.PublicKey, IPAddress.Parse("8.8.8.8"), 30303, discoveryPort: 9001);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes([new NetworkNode(enode)], LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootNodes, Has.Count.EqualTo(1));
            Assert.That(bootNodes[0].Port, Is.EqualTo(30303));
            Assert.That(bootNodes[0].DiscoveryPort, Is.EqualTo(9001));
            Assert.That(bootNodes[0].Host, Is.EqualTo("8.8.8.8"));
        }
    }

    [TestCase("0.0.0.0", "8.8.8.8", 1)]
    [TestCase("0.0.0.0", "2001:4860:4860::8888", 0)]
    [TestCase("2001:4860:4860::8844", "8.8.8.8", 0)]
    [TestCase("::", "8.8.8.8", 1)]
    [TestCase("0.0.0.0", "::ffff:192.0.2.1", 0)]
    public void Should_only_use_bootnode_families_reachable_from_listener(
        string localIp,
        string bootnodeIp,
        int expectedCount)
    {
        Enode enode = new(TestItem.PrivateKeyA.PublicKey, IPAddress.Parse(bootnodeIp), 30303);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes(
            [new NetworkNode(enode)],
            LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>(),
            IPAddress.Parse(localIp));

        Assert.That(bootNodes, Has.Count.EqualTo(expectedCount));
    }

    [Test]
    public void Should_reconstruct_signed_enr_before_rejecting_generic_endpoint()
    {
        NodeRecord record = CreateAsymmetricDualStackRecord();
        Assert.That(Node.TryFromEnr(record, out Node? genericNode), Is.True);
        Assert.That(genericNode!.HasDiscoveryEndpoint, Is.False);

        bool result = DiscoveryApp.TryCreateReachableNode(
            genericNode,
            IPAddress.Parse("2001:db8::5"),
            out Node? reachableNode);

        Assert.That(result, Is.True);
        Assert.That(reachableNode, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reachableNode!.Host, Is.EqualTo("2001:db8::1"));
            Assert.That(reachableNode.Port, Is.EqualTo(30303));
            Assert.That(reachableNode.DiscoveryPort, Is.EqualTo(30304));
        }
    }

    [Test]
    public void Should_restore_persisted_enr_using_listener_address_family()
    {
        NodeRecord record = CreateAsymmetricDualStackRecord();
        NetworkNode persistedNode = new(record.ToString());

        Node? restoredNode = DiscoveryApp.RestorePersistedNode(
            persistedNode,
            IPAddress.Parse("2001:db8::5"));

        Assert.That(restoredNode, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restoredNode!.Host, Is.EqualTo("2001:db8::1"));
            Assert.That(restoredNode.Port, Is.EqualTo(30303));
            Assert.That(restoredNode.DiscoveryPort, Is.EqualTo(30304));
            Assert.That(restoredNode.Enr.GetHex(), Is.EqualTo(record.GetHex()));
        }
    }

    private static NodeRecord CreateAsymmetricDualStackRecord() =>
        TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyA,
            IPAddress.Parse("192.0.2.1"),
            tcpPort: 30303,
            udpPort: null,
            configureExtras: record =>
            {
                record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
                record.SetEntry(new Udp6Entry(30304));
            });
}
