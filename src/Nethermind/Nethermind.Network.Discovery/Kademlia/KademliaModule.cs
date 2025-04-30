// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;

namespace Nethermind.Network.Discovery.Kademlia;

public class KademliaModule<TKey, TNode> : Module where TNode : notnull
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IKademlia<TKey, TNode>, Kademlia<TKey, TNode>>()
            .AddSingleton<IKademliaMessageReceiver<TKey, TNode>, KademliaKademliaMessageReceiver<TKey, TNode>>()
            .AddSingleton<NewLookupKNearestNeighbour<TKey, TNode>>()
            .AddSingleton<OriginalLookupKNearestNeighbour<TKey, TNode>>()
            .AddSingleton<ILookupAlgo<TNode>>(provider =>
            {
                KademliaConfig<TNode> config = provider.Resolve<KademliaConfig<TNode>>();
                if (config.UseNewLookup)
                {
                    return provider.Resolve<NewLookupKNearestNeighbour<TKey, TNode>>();
                }

                return provider.Resolve<OriginalLookupKNearestNeighbour<TKey, TNode>>();
            })
            .AddSingleton<ILookupAlgo2<TNode>, NewaTrackingLookupKNearestNeighbour<TNode>>()
            .AddSingleton<IRoutingTable<TNode>, KBucketTree<TKey, TNode>>()
            .AddSingleton<INodeHealthTracker<TNode>, NodeHealthTracker<TKey, TNode>>();
    }
}
