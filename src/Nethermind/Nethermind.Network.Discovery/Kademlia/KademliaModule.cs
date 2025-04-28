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
            .AddSingleton<ILookupAlgo<TKey, TNode>>(provider =>
            {
                KademliaConfig<TNode> config = provider.Resolve<KademliaConfig<TNode>>();
                if (config.UseNewLookup)
                {
                    return provider.Resolve<NewLookupKNearestNeighbour<TKey, TNode>>();
                }

                return provider.Resolve<OriginalLookupKNearestNeighbour<TKey, TNode>>();
            })
            .AddSingleton<ILookupAlgo2<TKey, TNode>, NewaTrackingLookupKNearestNeighbour<TKey, TNode>>()
            .AddSingleton<KBucketTree<TKey, TNode>>()
            .AddSingleton<BucketListRoutingTable<TKey, TNode>>()
            .AddSingleton<NodeHealthTracker<TKey, TNode>>()
            .AddSingleton<IRoutingTable<TNode>>(provider =>
            {
                KademliaConfig<TNode> config = provider.Resolve<KademliaConfig<TNode>>();
                if (config.UseTreeBasedRoutingTable)
                {
                    return provider.Resolve<KBucketTree<TKey, TNode>>();
                }

                return provider.Resolve<BucketListRoutingTable<TKey, TNode>>();
            });
    }
}
