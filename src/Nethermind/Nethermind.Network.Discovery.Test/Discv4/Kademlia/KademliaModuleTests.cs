// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Kademlia;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Test.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4.Kademlia;

public class KademliaModuleTests
{
    [Test]
    public void Filtering_tracker_is_a_singleton()
    {
        using IContainer container = CreateContainer(filterEnabled: true);

        INodeHealthTracker<Node> first = container.Resolve<INodeHealthTracker<Node>>();
        INodeHealthTracker<Node> second = container.Resolve<INodeHealthTracker<Node>>();

        Assert.That(ReferenceEquals(first, second), Is.True, "the filter state must survive across resolutions");
    }

    [Test]
    public void Discovery_node_admission_is_a_singleton()
    {
        using IContainer container = CreateContainer(filterEnabled: true);

        DiscoveryNodeAdmission first = container.Resolve<DiscoveryNodeAdmission>();
        DiscoveryNodeAdmission second = container.Resolve<DiscoveryNodeAdmission>();

        Assert.That(ReferenceEquals(first, second), Is.True);
    }

    [Test]
    public void AddOrRefresh_applies_filter_when_enabled()
    {
        using IContainer container = CreateContainer(filterEnabled: true);
        IKademlia<PublicKey, Node> kademlia = container.Resolve<IKademlia<PublicKey, Node>>();
        IRoutingTable<Node, ValueHash256> routingTable = container.Resolve<IRoutingTable<Node, ValueHash256>>();
        Node first = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node second = CreateNode(TestItem.PublicKeyB, "203.0.113.20");

        kademlia.AddOrRefresh(first);
        kademlia.AddOrRefresh(second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(routingTable.TryGet(first.Id.Hash, out _), Is.True);
            Assert.That(routingTable.TryGet(second.Id.Hash, out _), Is.False);
        }
    }

    [Test]
    public void AddOrRefresh_accepts_everything_when_filter_is_disabled()
    {
        using IContainer container = CreateContainer(filterEnabled: false);
        IKademlia<PublicKey, Node> kademlia = container.Resolve<IKademlia<PublicKey, Node>>();
        IRoutingTable<Node, ValueHash256> routingTable = container.Resolve<IRoutingTable<Node, ValueHash256>>();
        Node first = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node second = CreateNode(TestItem.PublicKeyB, "203.0.113.20");

        kademlia.AddOrRefresh(first);
        kademlia.AddOrRefresh(second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(routingTable.TryGet(first.Id.Hash, out _), Is.True);
            Assert.That(routingTable.TryGet(second.Id.Hash, out _), Is.True);
        }
    }

    [Test]
    public void Promotion_reuses_the_discovery_admission_decision()
    {
        using IContainer container = CreateContainer(filterEnabled: true);
        DiscoveryNodeAdmission admission = container.Resolve<DiscoveryNodeAdmission>();
        KademliaConfig<Node> kademliaConfig = container.Resolve<KademliaConfig<Node>>();
        Node first = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node second = CreateNode(TestItem.PublicKeyB, "203.0.113.20");

        Assert.That(admission.TryAdmit(first), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(kademliaConfig.CanPromoteReplacement, Is.Not.Null);
            Assert.That(kademliaConfig.CanPromoteReplacement!(second), Is.False);
        }
    }

    [Test]
    public void Refresh_reuses_the_discovery_admission_decision()
    {
        using IContainer container = CreateContainer(filterEnabled: true);
        DiscoveryNodeAdmission admission = container.Resolve<DiscoveryNodeAdmission>();
        KademliaConfig<Node> kademliaConfig = container.Resolve<KademliaConfig<Node>>();
        Node first = CreateNode(TestItem.PublicKeyA, "203.0.113.10");
        Node second = CreateNode(TestItem.PublicKeyB, "203.0.113.20");
        // A flag left on the stored instance must not exempt a node that has lost its subnet.
        second.IsTrusted = true;

        Assert.That(admission.TryAdmit(first), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(kademliaConfig.CanRefreshNode, Is.Not.Null);
            Assert.That(kademliaConfig.CanRefreshNode!(first), Is.True);
            Assert.That(kademliaConfig.CanRefreshNode!(second), Is.False);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Discv4_persists_the_bonded_endpoint(CancellationToken cancellationToken)
    {
        INetworkStorage storage = Substitute.For<INetworkStorage>();
        List<NetworkNode> persistedNodes = [];
        bool captured = false;
        TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.When(s => s.UpdateNodes(Arg.Any<IEnumerable<NetworkNode>>()))
            .Do(call =>
            {
                if (captured) return;
                captured = true;
                persistedNodes.AddRange(call.Arg<IEnumerable<NetworkNode>>());
            });
        storage.When(s => s.Commit()).Do(_ => committed.TrySetResult());

        using IContainer container = DiscoveryAdmissionTestContainer.Create(
            storage: storage,
            discoveryConfig: new DiscoveryConfig
            {
                DiscoveryPersistenceInterval = 10,
                MaxNodeLifecycleManagersCount = 100,
            });

        NodeRecord enr = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyA,
            IPAddress.Parse("203.0.114.20"),
            tcpPort: 30304,
            udpPort: 30304);
        Node node = new(TestItem.PublicKeyA, "203.0.113.10", 30304, 30304) { Enr = enr };
        container.Resolve<IKademlia<PublicKey, Node>>().AddOrRefresh(node);

        using CancellationTokenSource cts = new();
        Task commitTask = container.Resolve<DiscoveryPersistenceManager>().RunDiscoveryPersistenceCommit(cts.Token);
        await committed.Task.WaitAsync(cancellationToken);
        await cts.CancelAsync();
        try
        {
            await commitTask;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        NetworkNode persistedNode = persistedNodes.Single(n => n.NodeId.Equals(TestItem.PublicKeyA));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedNode.IsEnode, Is.True);
            Assert.That(persistedNode.HostIp, Is.EqualTo(IPAddress.Parse("203.0.113.10")));
        }
    }

    private static IContainer CreateContainer(bool filterEnabled)
        => DiscoveryAdmissionTestContainer.Create(
            enabled: filterEnabled,
            subnetBucketing: true,
            currentNode: CreateNode(TestItem.PublicKeyF, "198.51.100.1"),
            useDiscoveryConfigFactory: true);

    private static Node CreateNode(PublicKey id, string host) => new(id, host, 30303, 30303);
}
