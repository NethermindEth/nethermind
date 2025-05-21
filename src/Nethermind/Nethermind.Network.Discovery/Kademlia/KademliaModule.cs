// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Network.Discovery.Discv4;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// A kademlia module.
/// Application is expeccted to expose a  <see cref="IKademliaMessageSender{TKey, TNode}"/>
/// for the table maintenance to function.
/// Additionally, application is expected to call <see cref="INodeHealthTracker{TNode}.OnIncomingMessageFrom" />
/// and <see cref="INodeHealthTracker{TNode}.OnRequestFailed" /> respectedly.
/// </summary>
/// <typeparam name="TKey"></typeparam>
/// <typeparam name="TNode"></typeparam>
public class KademliaModule<TKey, TNode> : Module where TNode : notnull
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IKademlia<TKey, TNode>, Kademlia<TKey, TNode>>()
            .AddSingleton<LookupKNearestNeighbour<TKey, TNode>>()
            .AddSingleton<OriginalLookupKNearestNeighbour<TKey, TNode>>()
            .AddSingleton<ILookupAlgo<TNode>>(provider =>
            {
                KademliaConfig<TNode> config = provider.Resolve<KademliaConfig<TNode>>();
                if (config.UseNewLookup)
                {
                    return provider.Resolve<LookupKNearestNeighbour<TKey, TNode>>();
                }

                return provider.Resolve<OriginalLookupKNearestNeighbour<TKey, TNode>>();
            })
            .AddSingleton<IIteratorNodeLookup, IteratorNodeLookup>()
            .AddSingleton<KBucketTree<TKey, TNode>>()
            .AddSingleton<IRoutingTable<TNode>, KBucketTree<TKey, TNode>>()
            .AddSingleton<INodeHealthTracker<TNode>, NodeHealthTracker<TKey, TNode>>();
    }
}
