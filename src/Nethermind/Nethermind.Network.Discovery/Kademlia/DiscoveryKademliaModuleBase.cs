// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Autofac.Features.AttributeFilters;
using Microsoft.Extensions.Logging;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Logging.Microsoft;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

public abstract class DiscoveryKademliaModuleBase(Node currentNode, IReadOnlyList<Node> bootNodes, string discoveryStorageKey) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        RegisterProtocolServices(builder);

        builder
            .AddModule(new KademliaModule<PublicKey, Node, ValueHash256>())
            .AddSingleton<ILoggerFactory, ILogManager>(static logManager => new NethermindLoggerFactory(logManager))
            .AddSingleton<IKademliaDistance<ValueHash256>>(ValueHash256KademliaDistance.Instance)
            .AddSingleton<IKeyOperator<PublicKey, Node, ValueHash256>, PublicKeyKeyOperator>();

        builder.RegisterType<DiscoveryPersistenceManager>()
            .AsSelf()
            .WithAttributeFiltering()
            .WithParameter(ResolvedParameter.ForKeyed<INetworkStorage>(discoveryStorageKey))
            .SingleInstance();
    }

    protected abstract void RegisterProtocolServices(ContainerBuilder builder);

    /// <summary>Creates the routing-table configuration for this protocol; a subclass may attach protocol-specific policy.</summary>
    protected KademliaConfig<Node> CreateKademliaConfig(IDiscoveryConfig discoveryConfig)
        => DiscoveryKademliaConfigFactory.Create(currentNode, bootNodes, discoveryConfig);

    /// <summary>Creates the subnet limiter bound to this protocol's current node and bootnodes.</summary>
    protected IRoutingTableNodeLimiter<Node> CreateNodeLimiter(
        INetworkConfig networkConfig,
        IStaticNodesManager staticNodesManager,
        ITrustedNodesManager trustedNodesManager,
        IForkInfo forkInfo,
        ILogManager logManager)
        => new DiscoveryNodeLimiter(currentNode, bootNodes, networkConfig, staticNodesManager, trustedNodesManager, forkInfo, logManager);
}
