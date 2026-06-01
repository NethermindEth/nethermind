// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5;

/// <summary>
/// Specifies the protocol-specific Kademlia services used by discv5.
/// </summary>
public class DiscV5KademliaModule(PublicKey masterNode, IReadOnlyList<Node> bootNodes) : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<IKademliaNodeSource, Discv5NodeSource>()
        .AddSingleton<IDiscv5KademliaAdapter, Discv5KademliaAdapter>()
        .Bind<IKademliaMessageSender<PublicKey, Node>, IDiscv5KademliaAdapter>()
        .AddSingleton<NettyDiscoveryV5Handler>()
        .AddSingleton<Discv5PacketCodec>()
        .AddModule(new KademliaModule<PublicKey, Node, Hash256>())
        .AddSingleton<IKademliaDistance<Hash256>>(Hash256KademliaDistance.Instance)
        .AddSingleton<IKeyOperator<PublicKey, Node, Hash256>, PublicKeyKeyOperator>()
        .AddSingleton<KademliaConfig<Node>, IDiscoveryConfig>((discoveryConfig) => DiscoveryKademliaConfigFactory.Create(masterNode, bootNodes, discoveryConfig));
}
