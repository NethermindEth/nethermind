// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class DiscoveredNodeStoreTests
{
    [Test]
    public void Configured_node_becomes_active_under_discovered_protocol()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        NetworkNode networkNode = new(privateKey.PublicKey, "127.0.0.1", 30303);
        Node node = new(networkNode);
        DiscoveredNodeStore store = new();

        DiscoverySnapshot configuredSnapshot = store.AddConfiguredBootnodes([networkNode]);
        DiscoverySnapshot activeSnapshot = store.AddOrUpdate(node, "discv4", isActive: true);
        NodeDto activeNode = store.GetActiveNodes().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuredSnapshot.AllConfiguredCount, Is.EqualTo(1));
            Assert.That(configuredSnapshot.ActiveConfiguredCount, Is.Zero);
            Assert.That(activeSnapshot.ActiveCount, Is.EqualTo(1));
            Assert.That(activeSnapshot.AllCount, Is.EqualTo(1));
            Assert.That(activeSnapshot.ActiveDiscv4Count, Is.EqualTo(1));
            Assert.That(activeSnapshot.AllConfiguredCount, Is.Zero);
            Assert.That(activeNode.IsBootnode, Is.True);
            Assert.That(activeNode.Enode, Is.EqualTo(node.ToString(Node.Format.ENode)));
        }
    }

    [Test]
    public void Retention_limit_prunes_oldest_current_node_and_updates_snapshot_counts()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey firstKey = generator.Generate();
        using PrivateKey secondKey = generator.Generate();
        using PrivateKey thirdKey = generator.Generate();
        Node firstNode = CreateNode(firstKey, 30303);
        Node secondNode = CreateNode(secondKey, 30304);
        Node thirdNode = CreateNode(thirdKey, 30305);
        DiscoveredNodeStore store = new(maxRetainedNodes: 2);

        store.AddOrUpdate(firstNode, "discv4", isActive: true);
        store.AddOrUpdate(secondNode, "discv5", isActive: false);
        DiscoverySnapshot snapshot = store.AddOrUpdate(thirdNode, "discv4", isActive: true);
        NodeDto[] retainedNodes = store.GetAllNodes();
        string[] retainedNodeIds = retainedNodes.Select(static node => node.NodeId).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retainedNodes, Has.Length.EqualTo(2));
            Assert.That(retainedNodeIds, Does.Not.Contain(firstNode.Id.ToString(false)));
            Assert.That(retainedNodeIds, Does.Contain(secondNode.Id.ToString(false)));
            Assert.That(retainedNodeIds, Does.Contain(thirdNode.Id.ToString(false)));
            Assert.That(snapshot.AllCount, Is.EqualTo(2));
            Assert.That(snapshot.ActiveCount, Is.EqualTo(1));
            Assert.That(snapshot.AllDiscv4Count, Is.EqualTo(1));
            Assert.That(snapshot.ActiveDiscv4Count, Is.EqualTo(1));
            Assert.That(snapshot.AllDiscv5Count, Is.EqualTo(1));
            Assert.That(snapshot.ActiveDiscv5Count, Is.Zero);
        }
    }

    private static Node CreateNode(PrivateKey privateKey, int port) =>
        new(privateKey.PublicKey, "127.0.0.1", port);
}
