// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Network.Discovery.Kademlia;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection ConfigureKademliaComponents<TNode, TContentKey, TContent>(this IServiceCollection collection) where TNode : notnull
    {
        return collection
            .AddSingleton<IKademlia<TNode, TContentKey, TContent>, Kademlia<TNode, TContentKey, TContent>>()
            .AddSingleton<NewLookupKNearestNeighbour<TNode>>()
            .AddSingleton<OriginalLookupKNearestNeighbour<TNode>>()
            .AddSingleton<ILookupAlgo<TNode>>(provider =>
            {
                KademliaConfig<TNode> config = provider.GetRequiredService<KademliaConfig<TNode>>();
                if (config.UseNewLookup)
                {
                    return provider.GetRequiredService<NewLookupKNearestNeighbour<TNode>>();
                }

                return provider.GetRequiredService<OriginalLookupKNearestNeighbour<TNode>>();
            })
            .AddSingleton<KBucketTree<TNode>>()
            .AddSingleton<BucketListRoutingTable<TNode>>()
            .AddSingleton<IRoutingTable<TNode>>(provider =>
            {
                KademliaConfig<TNode> config = provider.GetRequiredService<KademliaConfig<TNode>>();
                if (config.UseTreeBasedRoutingTable)
                {
                    return provider.GetRequiredService<KBucketTree<TNode>>();
                }

                return provider.GetRequiredService<BucketListRoutingTable<TNode>>();
            });
    }
}
