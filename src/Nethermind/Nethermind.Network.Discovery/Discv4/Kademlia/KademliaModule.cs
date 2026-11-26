// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
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

            // The limiter instance is registered even when filtering is disabled so that `NodeSource` can inject it;
            // the config leaves `NodeLimiter` null in that case, which keeps the routing table unguarded.
            .AddSingleton<IRoutingTableNodeLimiter<Node>, INetworkConfig, IStaticNodesManager, ITrustedNodesManager, IForkInfo, ILogManager>(
                (networkConfig, staticNodesManager, trustedNodesManager, forkInfo, logManager) =>
                    CreateNodeLimiter(networkConfig, staticNodesManager, trustedNodesManager, forkInfo, logManager))
            .AddSingleton<KademliaConfig<Node>, IDiscoveryConfig, INetworkConfig, IRoutingTableNodeLimiter<Node>>(
                (discoveryConfig, networkConfig, nodeLimiter) =>
                {
                    KademliaConfig<Node> config = CreateKademliaConfig(discoveryConfig);
                    config.NodeLimiter = networkConfig.FilterDiscoveryNodesByRecentIp ? nodeLimiter : null;
                    return config;
                })
            ;
}
