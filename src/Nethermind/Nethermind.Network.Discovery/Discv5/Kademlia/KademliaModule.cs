// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Discv5.Packets;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Kademlia;

/// <summary>
/// Specifies the protocol-specific Kademlia services used by discv5.
/// </summary>
public class KademliaModule(PublicKey masterNode, IReadOnlyList<Node> bootNodes) : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<IKademliaNodeSource, NodeSource>()
        .AddSingleton<IKademliaAdapter, KademliaAdapter>()
        .Bind<IKademliaMessageSender<PublicKey, Node>, IKademliaAdapter>()
        .AddSingleton<NettyDiscoveryV5Handler>()
        .AddSingleton<PacketCodec>()
        .AddModule(new KademliaModule<PublicKey, Node, Hash256>())
        .AddSingleton<IKademliaDistance<Hash256>>(Hash256KademliaDistance.Instance)
        .AddSingleton<IKeyOperator<PublicKey, Node, Hash256>, PublicKeyKeyOperator>()
        .AddSingleton<KademliaConfig<Node>, IDiscoveryConfig>((discoveryConfig) => DiscoveryKademliaConfigFactory.Create(masterNode, bootNodes, discoveryConfig));
}
