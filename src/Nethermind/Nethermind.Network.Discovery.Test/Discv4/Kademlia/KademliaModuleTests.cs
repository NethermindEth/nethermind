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

    private static IContainer CreateContainer(bool filterEnabled)
    {
        NetworkConfig networkConfig = new()
        {
            ExternalIp = "198.51.100.1",
            LocalIp = "0.0.0.0",
            FilterDiscoveryNodesByRecentIp = filterEnabled,
            FilterDiscoveryNodesBySameSubnet = true,
        };
        Node currentNode = CreateNode(TestItem.PublicKeyF, "198.51.100.1");
        INetworkStorage storage = Substitute.For<INetworkStorage>();
        storage.GetPersistedNodes().Returns([]);
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
