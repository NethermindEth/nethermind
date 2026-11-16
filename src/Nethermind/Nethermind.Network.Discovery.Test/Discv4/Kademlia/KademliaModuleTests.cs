// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
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
    public void Disabled_admission_does_not_read_persisted_nodes()
    {
        INetworkStorage storage = Substitute.For<INetworkStorage>();
        using IContainer container = CreateContainer(filterEnabled: false, storage);

        DiscoveryNodeAdmission admission = container.Resolve<DiscoveryNodeAdmission>();

        Assert.That(admission.Enabled, Is.False);
        storage.DidNotReceive().GetPersistedNodes();
    }

    private static IContainer CreateContainer(bool filterEnabled, INetworkStorage? storage = null)
    {
        NetworkConfig networkConfig = new()
        {
            ExternalIp = "198.51.100.1",
            LocalIp = "0.0.0.0",
            FilterDiscoveryNodesByRecentIp = filterEnabled,
            FilterDiscoveryNodesBySameSubnet = true,
        };
        Node currentNode = CreateNode(TestItem.PublicKeyF, "198.51.100.1");
        storage ??= Substitute.For<INetworkStorage>();
        if (filterEnabled)
        {
            storage.GetPersistedNodes().Returns([]);
        }

        IKademliaMessageSender<PublicKey, Node> messageSender = Substitute.For<IKademliaMessageSender<PublicKey, Node>>();

        ContainerBuilder builder = new();
        builder.AddModule(new KademliaModule(currentNode, []));
        builder.RegisterInstance(networkConfig).As<INetworkConfig>();
        builder.RegisterInstance(new DiscoveryConfig()).As<IDiscoveryConfig>();
        builder.RegisterInstance(LimboLogs.Instance).As<ILogManager>();
        builder.RegisterInstance(storage).Keyed<INetworkStorage>(DbNames.DiscoveryNodes);
        builder.RegisterInstance(messageSender).As<IKademliaMessageSender<PublicKey, Node>>();
        return builder.Build();
    }

    private static Node CreateNode(PublicKey id, string host) => new(id, host, 30303, 30303);
}
