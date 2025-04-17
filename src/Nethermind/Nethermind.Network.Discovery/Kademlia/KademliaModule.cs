// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;

namespace Nethermind.Network.Discovery.Kademlia;

public class KademliaModule<TNode> : Module where TNode : notnull
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IKademlia<TNode>, Kademlia<TNode>>()
            .AddSingleton<IKademliaMessageReceiver<TNode>, KademliaKademliaMessageReceiver<TNode>>()
            .AddSingleton<NewLookupKNearestNeighbour<TNode>>()
            .AddSingleton<OriginalLookupKNearestNeighbour<TNode>>()
            .AddSingleton<ILookupAlgo<TNode>>(provider =>
            {
                KademliaConfig<TNode> config = provider.Resolve<KademliaConfig<TNode>>();
                if (config.UseNewLookup)
                {
                    return provider.Resolve<NewLookupKNearestNeighbour<TNode>>();
                }

                return provider.Resolve<OriginalLookupKNearestNeighbour<TNode>>();
            })
            .AddSingleton<ILookupAlgo2<TNode>, NewaTrackingLookupKNearestNeighbour<TNode>>()
            .AddSingleton<KBucketTree<TNode>>()
            .AddSingleton<BucketListRoutingTable<TNode>>()
            .AddSingleton<NodeHealthTracker<TNode>>()
            .AddSingleton<IRoutingTable<TNode>>(provider =>
            {
                KademliaConfig<TNode> config = provider.Resolve<KademliaConfig<TNode>>();
                if (config.UseTreeBasedRoutingTable)
                {
                    return provider.Resolve<KBucketTree<TNode>>();
                }

                return provider.Resolve<BucketListRoutingTable<TNode>>();
            });
    }
}
