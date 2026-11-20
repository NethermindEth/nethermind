// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly List<IContainer> _containers = [];

    [TearDown]
    public async Task DisposeContainers()
    {
        // Containers from CreateTracker must outlive the assertions, so they are released here.
        for (int i = _containers.Count - 1; i >= 0; i--)
        {
            await _containers[i].DisposeAsync();
        }

        _containers.Clear();
    }

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
    public void Persisted_node_at_another_address_does_not_bypass_filter()
    {
        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, _) = CreateTracker(
            persisted: [new NetworkNode(TestItem.PublicKeyB, "203.0.113.20", 30303)]);

        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyA, "203.0.113.10"));
        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyB, "203.0.113.21"));

        inner.Received(1).OnIncomingMessageFrom(Arg.Any<Node>());
    }

    [Test]
    public void Persisted_node_at_another_address_is_checked_again_after_being_admitted()
    {
        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, _) = CreateTracker(
            persisted: [new NetworkNode(TestItem.PublicKeyB, "203.0.113.20", 30303)]);

        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyB, "203.0.113.20"));
        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyA, "203.0.113.10"));
        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyB, "203.0.113.21"));

        inner.Received(2).OnIncomingMessageFrom(Arg.Any<Node>());
    }

    [Test]
    public void Persisted_node_at_another_address_is_admitted_when_its_new_subnet_is_free()
    {
        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, _) = CreateTracker(
            persisted: [new NetworkNode(TestItem.PublicKeyB, "203.0.113.20", 30303)]);

        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyB, "203.0.113.20"));
        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyB, "203.0.114.20"));
        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyC, "203.0.114.21"));

        inner.Received(2).OnIncomingMessageFrom(Arg.Any<Node>());
    }

    [Test]
    public void Persisted_node_without_an_endpoint_is_not_exempt()
    {
        NetworkNode persisted = new(TestEnrBuilder.BuildSignedWithoutEndpoint(TestItem.PrivateKeyB).ToString());
        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, _) = CreateTracker(persisted: [persisted]);

        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyA, "203.0.113.10"));
        tracker.OnIncomingMessageFrom(CreateNode(TestItem.PublicKeyB, "203.0.113.11"));

        inner.Received(1).OnIncomingMessageFrom(Arg.Any<Node>());
    }

    [Test]
    public void Trusted_static_and_bootnode_bypass_filter()
    {
        NetworkNode trustedNode = new(TestItem.PublicKeyA, "203.0.113.10", 30303);
        ITrustedNodesManager trustedNodes = Substitute.For<ITrustedNodesManager>();
        trustedNodes.Nodes.Returns([trustedNode]);
        trustedNodes.ContainsNode(Arg.Any<PublicKey>()).Returns(call => call.Arg<PublicKey>().Equals(trustedNode.NodeId));
        NetworkNode staticNode = new(TestItem.PublicKeyB, "203.0.113.11", 30303);
        IStaticNodesManager staticNodes = Substitute.For<IStaticNodesManager>();
        staticNodes.Nodes.Returns([staticNode]);
        staticNodes.ContainsNode(Arg.Any<PublicKey>()).Returns(call => call.Arg<PublicKey>().Equals(staticNode.NodeId));

        (INodeHealthTracker<Node> tracker, INodeHealthTracker<Node> inner, _) =
            CreateTracker(staticNodes: staticNodes, trustedNodes: trustedNodes);
        Node trusted = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node managedStatic = CreateNode(TestItem.PublicKeyB, "203.0.113.11");
        Node bootnode = CreateNode(TestItem.PublicKeyC, "203.0.113.12");
        bootnode.IsBootnode = true;

        tracker.OnIncomingMessageFrom(trusted);
        tracker.OnIncomingMessageFrom(managedStatic);
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

    [Test]
    [CancelAfter(10000)]
    public Task Refresh_evicts_an_incumbent_that_is_not_admitted(
        [Values] bool staleTrustedFlag, CancellationToken token)
    {
        Node incumbent = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        incumbent.IsTrusted = staleTrustedFlag;
        return AssertRefreshOutcome(incumbent, expectEvicted: true, trustedNodes: null, token);
    }

    [Test]
    [CancelAfter(10000)]
    public Task Refresh_keeps_an_incumbent_exempted_by_a_live_manager(CancellationToken token)
    {
        Node incumbent = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        ITrustedNodesManager trustedNodes = Substitute.For<ITrustedNodesManager>();
        NetworkNode trustedNode = new(incumbent.Id, "203.0.113.10", 30303);
        trustedNodes.Nodes.Returns([trustedNode]);
        trustedNodes.ContainsNode(Arg.Any<PublicKey>()).Returns(call => call.Arg<PublicKey>().Equals(trustedNode.NodeId));
        return AssertRefreshOutcome(incumbent, expectEvicted: false, trustedNodes, token);
    }

    private static async Task AssertRefreshOutcome(
        Node incumbent,
        bool expectEvicted,
        ITrustedNodesManager? trustedNodes,
        CancellationToken token)
    {
        Node newcomer = CreateNode(TestItem.PublicKeyB, "203.0.114.20");
        Node otherIdentity = CreateNode(TestItem.PublicKeyC, "203.0.113.10");
        ValueHash256 incumbentHash = incumbent.Id.Hash;

        ConcurrentBag<(ValueHash256 Hash, Node Node)> addCalls = [];
        ConcurrentBag<ValueHash256> removeCalls = [];
        IRoutingTable<Node, ValueHash256> routingTable = Substitute.For<IRoutingTable<Node, ValueHash256>>();
        routingTable.TryAddOrRefresh(Arg.Any<ValueHash256>(), Arg.Any<Node>(), out Arg.Any<Node?>())
            .Returns(call =>
            {
                Node node = call.ArgAt<Node>(1);
                addCalls.Add((call.ArgAt<ValueHash256>(0), node));
                if (ReferenceEquals(node, newcomer))
                {
                    call[2] = incumbent;
                    return BucketAddResult.Full;
                }

                call[2] = null;
                return BucketAddResult.Refreshed;
            });
        routingTable.Remove(Arg.Any<ValueHash256>()).Returns(call =>
        {
            removeCalls.Add(call.ArgAt<ValueHash256>(0));
            return true;
        });

        using IContainer container = DiscoveryAdmissionTestContainer.Create(
            routingTable: routingTable,
            trustedNodes: trustedNodes);
        INodeHealthTracker<Node> tracker = container.Resolve<INodeHealthTracker<Node>>();
        container.Resolve<IKademliaMessageSender<PublicKey, Node>>()
            .Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(true);

        Assert.That(container.Resolve<DiscoveryNodeAdmission>().TryAdmit(otherIdentity), Is.True);

        tracker.OnIncomingMessageFrom(newcomer);

        if (expectEvicted)
        {
            await AsyncTestHelpers.AssertEventuallyAsync(
                () => removeCalls.Any(hash => hash == incumbentHash), token);
            Assert.That(addCalls, Has.None.Matches<(ValueHash256 Hash, Node Node)>(call => call.Hash == incumbentHash));
        }
        else
        {
            await AsyncTestHelpers.AssertEventuallyAsync(
                () => addCalls.Any(call => call.Hash == incumbentHash), token);
            Assert.That(removeCalls, Does.Not.Contain(incumbentHash));
        }
    }

    private static Node CreateNode(PublicKey id, string host) => new(id, host, 30303, 30303);

    private (INodeHealthTracker<Node> Tracker, INodeHealthTracker<Node> Inner, IRoutingTable<Node, ValueHash256> RoutingTable)
        CreateTracker(
            bool enabled = true,
            bool subnetBucketing = true,
            NetworkNode[]? persisted = null,
            KademliaConfig<Node>? config = null,
            string currentIp = "198.51.100.1",
            IStaticNodesManager? staticNodes = null,
            ITrustedNodesManager? trustedNodes = null)
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
            staticNodes: staticNodes,
            trustedNodes: trustedNodes,
            innerTracker: inner);
        _containers.Add(container);
        return (container.Resolve<INodeHealthTracker<Node>>(), inner, routingTable);
    }
}
