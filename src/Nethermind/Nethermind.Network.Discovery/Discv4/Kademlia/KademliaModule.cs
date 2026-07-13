// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4.Kademlia;

/// <summary>
/// Specify the discv4 kademlia components. Mainly provide transport for <see cref="KademliaModule{TKey,TNode,TKadKey}"/>.
/// Because kademlia can and probably will be reused outside of discv4, this module is meant to be added within a child
/// lifecycle in <see cref="DiscoveryApp"/> to prevent unexpected conflict.
/// </summary>
/// <param name="currentNode"></param>
/// <param name="bootNodes"></param>
public sealed class KademliaModule(Node currentNode, IReadOnlyList<Node> bootNodes) : DiscoveryKademliaModuleBase(currentNode, bootNodes, DbNames.DiscoveryNodes)
{
    protected override void RegisterProtocolServices(ContainerBuilder builder) => builder
            // This two class contains the actual `INodeSource` logic. As in finding nodes within the network.
            .AddSingleton<IKademliaNodeSource, NodeSource>()

            // Some transport wiring.
            .AddSingleton<IKademliaAdapter, KademliaAdapter>()
            .Bind<IDiscoveryMsgListener, IKademliaAdapter>()
            .Bind<IKademliaMessageSender<PublicKey, Node>, IKademliaAdapter>()
            .AddSingleton<NettyDiscoveryHandler>()
            ;
}
