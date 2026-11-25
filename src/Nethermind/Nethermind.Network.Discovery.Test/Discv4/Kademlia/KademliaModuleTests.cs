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
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Kademlia;
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
    public void Peer_candidate_reservations_are_shared_with_the_compatibility_constructor()
    {
        using IContainer container = CreateContainer(filterEnabled: true);
        KademliaConfig<Node> kademliaConfig = container.Resolve<KademliaConfig<Node>>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                ReferenceEquals(container.Resolve<PeerCandidateReservations>(), container.Resolve<PeerCandidateReservations>()),
                Is.True,
                "the gate and the adapter must release from the same store");
            Assert.That(
                ReferenceEquals(container.Resolve<PeerCandidateReservations>(), PeerCandidateReservations.For(kademliaConfig)),
                Is.True,
                "the compatibility constructor must observe the store the container wires");
        }
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
    public async Task Discv4_persists_the_bonded_endpoint_while_recent_ip_filtering_is_enabled(CancellationToken cancellationToken)
    {
        NetworkNode persistedNode = await PersistSingleNodeAsync(filterEnabled: true, CreateDualStackNode(), cancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedNode.IsEnode, Is.True);
            Assert.That(persistedNode.HostIp, Is.EqualTo(IPAddress.Parse("203.0.113.10")));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Discv4_restores_the_enr_family_when_recent_ip_filtering_is_disabled(CancellationToken cancellationToken)
    {
        Node node = CreateDualStackNode();

        NetworkNode persistedNode = await PersistSingleNodeAsync(filterEnabled: false, node, cancellationToken);

        // Guards the wiring, not a lost-peer outcome: with the filter off the row keeps the ENR, and restore picks the
        // family the listener serves. A filter-on row reaches the same endpoint through FallbackEnr, so the row format
        // is the only observable this test can pin.
        Node? restoredNode = DiscoveryApp.RestorePersistedNode(persistedNode, IPAddress.Parse("2001:db8::5"));

        Assert.That(restoredNode, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedNode.IsEnr, Is.True);
            Assert.That(restoredNode!.Host, Is.EqualTo("2001:db8::1"));
            Assert.That(restoredNode.Port, Is.EqualTo(30304));
            Assert.That(restoredNode.DiscoveryPort, Is.EqualTo(30304));
            Assert.That(restoredNode.Enr!.GetHex(), Is.EqualTo(node.Enr!.GetHex()));
        }
    }

    private static async Task<NetworkNode> PersistSingleNodeAsync(
        bool filterEnabled,
        Node node,
        CancellationToken cancellationToken)
    {
        INetworkStorage storage = Substitute.For<INetworkStorage>();
        List<NetworkNode> persistedNodes = [];
        bool captured = false;
        TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.When(s => s.UpdateNodes(Arg.Any<IEnumerable<NetworkNode>>()))
            .Do(call =>
            {
                // The interval is short enough that a second tick can land before cancellation.
                if (captured) return;
                captured = true;
                persistedNodes.AddRange(call.Arg<IEnumerable<NetworkNode>>());
            });
        storage.When(s => s.Commit()).Do(_ => committed.TrySetResult());

        using IContainer container = DiscoveryAdmissionTestContainer.Create(
            enabled: filterEnabled,
            storage: storage,
            discoveryConfig: new DiscoveryConfig
            {
                DiscoveryPersistenceInterval = 10,
                MaxNodeLifecycleManagersCount = 100,
            });

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

        return persistedNodes.Single(n => n.NodeId.Equals(TestItem.PublicKeyA));
    }

    private static Node CreateDualStackNode()
    {
        NodeRecord enr = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyA,
            IPAddress.Parse("203.0.114.20"),
            tcpPort: 30304,
            udpPort: 30304,
            configureExtras: record =>
            {
                record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
                record.SetEntry(new Tcp6Entry(30304));
                record.SetEntry(new Udp6Entry(30304));
            });

        Node node = new(TestItem.PublicKeyA, "203.0.113.10", 30304, 30304);
        Assert.That(node.SetVerifiedEnr(enr), Is.True);
        return node;
    }

    private static IContainer CreateContainer(bool filterEnabled)
        => DiscoveryAdmissionTestContainer.Create(
            enabled: filterEnabled,
            subnetBucketing: true,
            currentNode: CreateNode(TestItem.PublicKeyF, "198.51.100.1"),
            useDiscoveryConfigFactory: true);

    private static Node CreateNode(PublicKey id, string host) => new(id, host, 30303, 30303);
}
