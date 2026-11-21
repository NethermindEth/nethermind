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
using Nethermind.Network.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

public abstract class DiscoveryKademliaModuleBase(Node currentNode, IReadOnlyList<Node> bootNodes, string discoveryStorageKey) : Module
{
    /// <summary>
    /// Whether persisted nodes must restore to the bonded routing endpoint instead of an ENR-advertised one.
    /// </summary>
    /// <remarks>
    /// discv4 enables this because its persisted-node admission exemption is scoped to the address that was
    /// admitted; an ENR can advertise an endpoint that never answered a ping. The signed ENR is persisted alongside
    /// the bonded endpoint, so a restore can still fall back to a family the local listener serves. The preference
    /// applies only while <see cref="INetworkConfig.FilterDiscoveryNodesByRecentIp"/> is enabled: with the filter
    /// off no persisted node needs the exemption, so the ENR is kept and a restore can pick the endpoint the
    /// listener serves. The flag is therefore read from the same configuration instance the persisted-node
    /// exemption reads when it decides whether to filter.
    /// </remarks>
    protected virtual bool PersistBondedEndpoint => false;

    protected override void Load(ContainerBuilder builder)
    {
        RegisterProtocolServices(builder);

        builder
            .AddModule(new KademliaModule<PublicKey, Node, ValueHash256>())
            .AddSingleton<ILoggerFactory, ILogManager>(static logManager => new NethermindLoggerFactory(logManager))
            .AddSingleton<IKademliaDistance<ValueHash256>>(ValueHash256KademliaDistance.Instance)
            .AddSingleton<IKeyOperator<PublicKey, Node, ValueHash256>, PublicKeyKeyOperator>()
            .AddSingleton<KademliaConfig<Node>, IDiscoveryConfig>((discoveryConfig) => DiscoveryKademliaConfigFactory.Create(currentNode, bootNodes, discoveryConfig));

        builder.RegisterType<DiscoveryPersistenceManager>()
            .AsSelf()
            .WithAttributeFiltering()
            .WithParameter(ResolvedParameter.ForKeyed<INetworkStorage>(discoveryStorageKey))
            .WithParameter(new ResolvedParameter(
                static (parameter, _) => parameter.ParameterType == typeof(bool),
                (_, context) => PersistBondedEndpoint && context.Resolve<INetworkConfig>().FilterDiscoveryNodesByRecentIp))
            .SingleInstance();
    }

    protected abstract void RegisterProtocolServices(ContainerBuilder builder);
}
