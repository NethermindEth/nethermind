// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

public abstract class DiscoveryKademliaModuleBase(Node currentNode, IReadOnlyList<Node> bootNodes) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        RegisterProtocolServices(builder);

        builder
            .AddModule(new KademliaModule<PublicKey, Node, Hash256>())
            .AddSingleton<IKademliaDistance<Hash256>>(Hash256KademliaDistance.Instance)
            .AddSingleton<IKeyOperator<PublicKey, Node, Hash256>, PublicKeyKeyOperator>()
            .AddSingleton<DiscoveryPersistenceManager>()
            .AddSingleton<KademliaConfig<Node>, IDiscoveryConfig>((discoveryConfig) => DiscoveryKademliaConfigFactory.Create(currentNode, bootNodes, discoveryConfig));
    }

    protected abstract void RegisterProtocolServices(ContainerBuilder builder);
}
