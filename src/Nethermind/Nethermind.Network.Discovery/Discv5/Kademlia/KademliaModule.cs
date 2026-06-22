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
public sealed class KademliaModule(Node currentNode, IReadOnlyList<Node> bootNodes) : DiscoveryKademliaModuleBase(currentNode, bootNodes)
{
    protected override void RegisterProtocolServices(ContainerBuilder builder) => builder
        .AddSingleton<IDiscv5RecordFilter>(ExecutionLayerDiscv5RecordFilter.Instance)
        .AddSingleton<IKademliaNodeSource, NodeSource>()
        .AddSingleton<IKademliaAdapter, KademliaAdapter>()
        .Bind<IKademliaMessageSender<PublicKey, Node>, IKademliaAdapter>()
        .AddSingleton<NettyDiscoveryV5Handler>()
        .AddSingleton<PacketCodec>();
}
