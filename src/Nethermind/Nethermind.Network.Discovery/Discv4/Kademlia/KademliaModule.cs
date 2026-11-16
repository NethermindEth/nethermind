// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
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
public sealed class KademliaModule(Node currentNode, IReadOnlyList<Node> bootNodes) : DiscoveryKademliaModuleBase(currentNode, bootNodes, DbNames.DiscoveryNodes)
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.AddSingleton<DiscoveryNodeAdmission>((ctx) =>
        {
            INetworkConfig networkConfig = ctx.Resolve<INetworkConfig>();
            IDiscoveryConfig discoveryConfig = ctx.Resolve<IDiscoveryConfig>();
            IPAddress? currentIp = IPAddress.TryParse(networkConfig.ExternalIp ?? networkConfig.LocalIp, out IPAddress? ip) ? ip : null;
            int filterSize = Math.Max(networkConfig.MaxActivePeers, discoveryConfig.MaxNodeLifecycleManagersCount);
            NodeFilter filter = NodeFilter.Create(
                filterSize,
                networkConfig.FilterDiscoveryNodesByRecentIp,
                networkConfig.FilterDiscoveryNodesBySameSubnet,
                currentIp);

            return new DiscoveryNodeAdmission(
                filter,
                networkConfig.FilterDiscoveryNodesByRecentIp,
                ctx.Resolve<IRoutingTable<Node, ValueHash256>>(),
                ctx.Resolve<KademliaConfig<Node>>(),
                ctx.ResolveKeyed<INetworkStorage>(DbNames.DiscoveryNodes),
                filterSize,
                ctx.Resolve<ILogManager>());
        });
        builder.AddDecorator<INodeHealthTracker<Node>>((ctx, inner) =>
            new FilteringNodeHealthTracker(
                inner,
                ctx.Resolve<DiscoveryNodeAdmission>(),
                ctx.Resolve<ILogManager>()));
    }

    protected override void RegisterProtocolServices(ContainerBuilder builder) => builder
            // This two class contains the actual `INodeSource` logic. As in finding nodes within the network.
            .AddSingleton<IKademliaNodeSource, NodeSource>()

            // Some transport wiring.
            .AddSingleton<IKademliaAdapter, KademliaAdapter>()
            .Bind<IDiscoveryMsgListener, IKademliaAdapter>()
            .Bind<IKademliaMessageSender<PublicKey, Node>, IKademliaAdapter>()
            .AddSingleton<DiscoveryHandler>()
            ;
}
