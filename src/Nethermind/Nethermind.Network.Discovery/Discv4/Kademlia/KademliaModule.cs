// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4.Kademlia;

/// <summary>
/// Specify the discv4 kademlia components. Mainly provide transport for <see cref="KademliaModule{TKey,TNode,TKadKey}"/>.
/// Because kademlia can and probably will be reused outside of discv4, this module is meant to be added within a child
/// lifecycle in <see cref="DiscoveryApp"/> to prevent unexpected conflict.
/// </summary>
/// <param name="masterNode"></param>
/// <param name="bootNodes"></param>
public class KademliaModule(PublicKey masterNode, IReadOnlyList<Node> bootNodes) : Module
{
    protected override void Load(ContainerBuilder builder) => builder
            .AddSingleton<DiscoveryPersistenceManager>()

            // This two class contains the actual `INodeSource` logic. As in finding nodes within the network.
            .AddSingleton<IKademliaNodeSource, NodeSource>()

            // Some transport wiring.
            .AddSingleton<IKademliaAdapter, KademliaAdapter>()
            .Bind<IDiscoveryMsgListener, IKademliaAdapter>()
            .AddSingleton<NettyDiscoveryHandler>()

            // Register the main kademlia module and integration
            .AddModule(new KademliaModule<PublicKey, Node, Hash256>())
            .Bind<IKademliaMessageSender<PublicKey, Node>, IKademliaAdapter>()
            .AddSingleton<IKademliaDistance<Hash256>>(Hash256KademliaDistance.Instance)
            .AddSingleton<IKeyOperator<PublicKey, Node, Hash256>, PublicKeyKeyOperator>()
            .AddSingleton<KademliaConfig<Node>, IDiscoveryConfig>((discoveryConfig) => DiscoveryKademliaConfigFactory.Create(masterNode, bootNodes, discoveryConfig))
            ;
}
