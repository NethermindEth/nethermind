// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Kademlia;
using Nethermind.Network;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class FilteringNodeHealthTrackerTests
{
    [Test]
    public void Rejects_second_node_from_same_subnet_or_exact_ip(
        [Values("203.0.113.10", "203.0.113.20")] string secondNodeIp)
    {
        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, _) = CreateTracker();

        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyA, "203.0.113.10"));
        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyB, secondNodeIp));

        inner.Received(1).OnIncomingMessageFrom(Arg.Any<Node>());
    }

    [Test]
    public void Accepts_second_node_from_different_subnet()
    {
        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, _) = CreateTracker();

        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyA, "203.0.113.10"));
        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyB, "203.0.114.20"));

        inner.Received(2).OnIncomingMessageFrom(Arg.Any<Node>());
    }

    [Test]
    public void Accepts_same_subnet_when_filter_is_disabled()
    {
        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, _) = CreateTracker(enabled: false);

        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyA, "203.0.113.10"));
        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyB, "203.0.113.20"));

        inner.Received(2).OnIncomingMessageFrom(Arg.Any<Node>());
    }

    [Test]
    public void When_subnet_bucketing_is_disabled_uses_exact_ip()
    {
        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, _) = CreateTracker(subnetBucketing: false);

        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyA, "203.0.113.10"));
        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyB, "203.0.113.20"));
        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyC, "203.0.113.10"));

        inner.Received(2).OnIncomingMessageFrom(Arg.Any<Node>());
    }

    [Test]
    public void Refreshes_known_node_without_consuming_filter_capacity()
    {
        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, IRoutingTable<Node, ValueHash256> routingTable) = CreateTracker();
        Node node = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        routingTable.TryGet(Arg.Any<ValueHash256>(), out Arg.Any<Node?>()).Returns(ci =>
        {
            ci[1] = node;
            return true;
        });

        tracker.OnIncomingMessageFrom(node);
        tracker.OnIncomingMessageFrom(node);

        inner.Received(2).OnIncomingMessageFrom(Arg.Any<Node>());
    }

    [Test]
    public void Persisted_node_bypasses_filter()
    {
        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, _) = CreateTracker(
            persisted: [new NetworkNode(TestItem.PublicKeyB, "203.0.113.20", 30303)]);

        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyA, "203.0.113.10"));
        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyB, "203.0.113.20"));

        inner.Received(2).OnIncomingMessageFrom(Arg.Any<Node>());
    }

    [Test]
    public void Trusted_static_and_bootnode_bypass_filter()
    {
        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, _) = CreateTracker();
        Node trusted = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        trusted.IsTrusted = true;
        Node staticNode = CreateNode(TestItem.PublicKeyB, "203.0.113.11");
        staticNode.IsStatic = true;
        Node bootnode = CreateNode(TestItem.PublicKeyC, "203.0.113.12");
        bootnode.IsBootnode = true;

        tracker.OnIncomingMessageFrom(trusted);
        tracker.OnIncomingMessageFrom(staticNode);
        tracker.OnIncomingMessageFrom(bootnode);

        inner.Received(3).OnIncomingMessageFrom(Arg.Any<Node>());
    }

    [Test]
    public void Self_and_configured_bootnode_bypass_filter()
    {
        Node self = CreateNode(TestItem.PublicKeyF, "198.51.100.2");
        Node bootnode = CreateNode(TestItem.PublicKeyE, "203.0.113.15");
        KademliaConfig<Node> config = new()
        {
            CurrentNodeId = self,
            BootNodes = [bootnode],
        };
        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, _) = CreateTracker(config: config);

        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyA, "203.0.113.10"));
        tracker.OnIncomingMessageFrom(self);
        tracker.OnIncomingMessageFrom(bootnode);

        inner.Received(3).OnIncomingMessageFrom(Arg.Any<Node>());
    }

    private static Node CreateNode(PublicKey id, string host) => new(id, host, 30303, 30303);

    private static (INodeHealthTracker<Node> Tracker, INodeHealthTracker<Node> Inner, IRoutingTable<Node, ValueHash256> RoutingTable)
        CreateTracker(
            bool enabled = true,
            bool subnetBucketing = true,
            NetworkNode[]? persisted = null,
            KademliaConfig<Node>? config = null,
            string currentIp = "198.51.100.1")
    {
        INodeHealthTracker<Node> inner = Substitute.For<INodeHealthTracker<Node>>();
        IRoutingTable<Node, ValueHash256> routingTable = Substitute.For<IRoutingTable<Node, ValueHash256>>();
        IContainer container = DiscoveryAdmissionTestContainer.Create(
            enabled,
            subnetBucketing,
            currentIp,
            persisted: persisted,
            kademliaConfig: config,
            routingTable: routingTable,
            innerTracker: inner);
        return (container.Resolve<INodeHealthTracker<Node>>(), inner, routingTable);
    }
}
