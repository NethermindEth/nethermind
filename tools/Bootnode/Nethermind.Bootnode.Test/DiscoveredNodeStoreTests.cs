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
}
