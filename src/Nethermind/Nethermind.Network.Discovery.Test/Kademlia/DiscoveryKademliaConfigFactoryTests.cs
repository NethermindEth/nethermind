// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class DiscoveryKademliaConfigFactoryTests
{
    [Test]
    public void Create_ShouldUseProvidedCurrentNode()
    {
        Node currentNode = new(TestItem.PublicKeyA, "192.0.2.10", 30304, true);

        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(
            currentNode,
            [],
            new DiscoveryConfig());

        Assert.That(config.CurrentNodeId, Is.SameAs(currentNode));
        Assert.That(config.CurrentNodeId.Address, Is.EqualTo(currentNode.Address));
    }

    [Test]
    public void Create_ShouldMergeEnrStateWhenRoutingEntryIsRefreshed()
    {
        Node currentNode = new(TestItem.PublicKeyA, "192.0.2.10", 30304, true);
        Node existing = new(TestItem.PublicKeyB, "192.0.2.11", 30304);
        Node incoming = new(TestItem.PublicKeyB, "192.0.2.12", 30304);
        existing.ObserveEnrSequence(7);
        incoming.ObserveEnrSequence(8);
        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(
            currentNode,
            [],
            new DiscoveryConfig());

        incoming = config.MergeOnRefresh!(incoming, existing);
        incoming.TryRequestEnrSequence(9);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(incoming.HighestObservedEnrSequence, Is.EqualTo(8));
            Assert.That(existing.HighestObservedEnrSequence, Is.EqualTo(8));
            Assert.That(existing.RequestingEnrSequence, Is.EqualTo(9));
        }
    }

    [Test]
    public void RoutingTableRefresh_ShouldRetainNewerVerifiedEnrState()
    {
        Node currentNode = new(TestItem.PublicKeyA, "192.0.2.10", 30304, true);
        NodeRecord newerRecord = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            IPAddress.Parse("192.0.2.11"),
            tcpPort: 30304,
            udpPort: 30303,
            enrSequence: 12);
        Node existing = new(TestItem.PublicKeyB, "192.0.2.11", 30304);
        existing.SetVerifiedEnr(newerRecord);
        NodeRecord staleRecord = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            IPAddress.Parse("192.0.2.12"),
            tcpPort: 30304,
            udpPort: 30303,
            enrSequence: 11);
        Node incoming = new(TestItem.PublicKeyB, "192.0.2.12", 30304);
        incoming.SetVerifiedEnr(staleRecord);
        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(
            currentNode,
            [],
            new DiscoveryConfig());
        KBucketTree<Node, Hash256> tree = new(
            config,
            new FromKeyNodeHashProvider<PublicKey, Node, Hash256>(new PublicKeyKeyOperator()),
            Hash256KademliaDistance.Instance);
        tree.TryAddOrRefresh(existing.Id.Hash, existing, out _);

        tree.TryAddOrRefresh(incoming.Id.Hash, incoming, out _);

        Node stored = tree.GetByHash(incoming.Id.Hash)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Is.SameAs(incoming));
            Assert.That(stored.Enr, Is.SameAs(newerRecord));
            Assert.That(stored.IsVerifiedEnr(newerRecord), Is.True);
            Assert.That(stored.HighestObservedEnrSequence, Is.EqualTo(newerRecord.EnrSequence));
        }
    }
}
