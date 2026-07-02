// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
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
}
