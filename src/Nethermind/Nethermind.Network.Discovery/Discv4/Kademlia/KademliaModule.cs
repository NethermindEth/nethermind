// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network;
using Nethermind.Network.Config;
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
public sealed class KademliaModule(Node currentNode, IReadOnlyList<Node> bootNodes) : DiscoveryKademliaModuleBase(currentNode, bootNodes, DiscoveryStorageKeys.V4)
{
    protected override void RegisterProtocolServices(ContainerBuilder builder) => builder
            // This two class contains the actual `INodeSource` logic. As in finding nodes within the network.
            .AddSingleton<IKademliaNodeSource, NodeSource>()

            // Some transport wiring.
            .AddSingleton<IKademliaAdapter, KademliaAdapter>()
            .Bind<IDiscoveryMsgListener, IKademliaAdapter>()
            .Bind<IKademliaMessageSender<PublicKey, Node>, IKademliaAdapter>()
            .AddSingleton<DiscoveryHandler>()

            // The limiter is built inline (not registered) so that `NodeSource` gets it only through the
            // config; the config leaves `NodeLimiter` null when filtering is disabled
            // (`Network.FilterDiscoveryNodesByRecentIp`), which keeps the routing table unguarded.
            // When enabled it caps entries per subnet key (2/bucket, 10/table), filters peer candidates,
            // and bypasses LAN addresses. Exemptions are optional: outside the full client they are absent
            // and nothing is exempt beyond self/bootnodes/flags.
            .AddSingleton<KademliaConfig<Node>>(ctx =>
                {
                    KademliaConfig<Node> config = CreateKademliaConfig(ctx.Resolve<IDiscoveryConfig>());
                    INetworkConfig networkConfig = ctx.Resolve<INetworkConfig>();
                    config.NodeLimiter = networkConfig.FilterDiscoveryNodesByRecentIp
                        ? CreateNodeLimiter(
                            networkConfig,
                            ctx.ResolveOptional<IDiscoveryNodeExemptions>())
                        : null;
                    return config;
                })
            ;
}
