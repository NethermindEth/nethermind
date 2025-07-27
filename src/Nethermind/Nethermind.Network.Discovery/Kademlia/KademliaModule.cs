// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Network.Discovery.Discv4;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// A kademlia module.
/// Application is expected to expose a
/// - <see cref="IKademliaMessageSender{TKey, TNode}"/>
/// - <see cref="IKeyOperator{TKey, TNode}"/>
/// - <see cref="KademliaConfig{TNode}"/>
/// for the table bootstrap and maintenance to function.
/// Call <see cref="IKademlia{TKey,TNode}.Run"/> to start the table.
/// Additionally, application is expected to call <see cref="INodeHealthTracker{TNode}.OnIncomingMessageFrom" />
/// and <see cref="INodeHealthTracker{TNode}.OnRequestFailed" /> respectedly which allow it to detect bad peer
/// from the table and add new peer as they send message.
/// Any authentication or session is handled externally.
/// </summary>
/// <typeparam name="TKey">Key is the type that represent the target or hash.</typeparam>
/// <typeparam name="TNode">Type of the node.</typeparam>
public class KademliaModule<TKey, TNode> : Module where TNode : notnull
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IKademlia<TKey, TNode>, Kademlia<TKey, TNode>>()
            .AddSingleton<ILookupAlgo<TNode>, LookupKNearestNeighbour<TKey, TNode>>()
            .AddSingleton<INodeHashProvider<TNode>, FromKeyNodeHashProvider<TKey, TNode>>()
            .AddSingleton<IRoutingTable<TNode>, KBucketTree<TNode>>()
            .AddSingleton<IIteratorNodeLookup<TKey, TNode>, IteratorNodeLookup<TKey, TNode>>()
            .AddSingleton<INodeHealthTracker<TNode>, NodeHealthTracker<TKey, TNode>>();
    }
}
