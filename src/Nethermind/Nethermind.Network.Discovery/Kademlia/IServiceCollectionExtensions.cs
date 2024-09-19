// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Kademlia;

public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Configure the <param name="collection">service collection</param> with kademlia services. The following
    /// dependencies are expected:
    ///
    /// - <see cref="IKademliaMessageSender{TNode}">IKademliaMessageSender</see>
    /// - <see cref="KademliaConfig{TNode}">KademliaConfig</see>
    /// - <see cref="INodeHashProvider{TNode}">INodeHashProvider</see>
    /// - <see cref="ILogManager">ILogManager</see>
    ///
    /// Additionally, the transport layer is expected to call the method in <see cref="IKademliaMessageReceiver{TNode}">IKademliaMessageReceiver</see>
    /// when external message is received.
    ///
    /// </summary>
    /// <param name="collection"></param>
    /// <typeparam name="TNode">The type of node</typeparam>
    /// <returns></returns>
    public static IServiceCollection ConfigureKademliaComponents<TNode>(this IServiceCollection collection) where TNode : notnull
    {
        return collection
            .AddSingleton<IKademlia<TNode>, Kademlia<TNode>>()
            .AddSingleton<IKademliaMessageReceiver<TNode>, KademliaKademliaMessageReceiver<TNode>>()
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
