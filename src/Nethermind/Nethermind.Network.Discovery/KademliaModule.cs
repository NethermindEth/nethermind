// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery;

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
/// <typeparam name="THash">Key is the type that represent the target or hash.</typeparam>
/// <typeparam name="TNode">Type of the node.</typeparam>
public class KademliaModule<TPublicKey, THash, TNode> : Module where TNode : notnull where THash : struct, IKademiliaHash<THash>
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IKademlia<TPublicKey, TNode>, Kademlia<TPublicKey, THash, TNode>>()
            .AddSingleton<ILookupAlgo<THash, TNode>, LookupKNearestNeighbour<THash, TNode>>()
            .AddSingleton<INodeHashProvider<THash, TNode>, FromKeyNodeHashProvider<TPublicKey, THash, TNode>>()
            .AddSingleton<IRoutingTable<THash, TNode>, KBucketTree<THash, TNode>>()
            .AddSingleton<IIteratorNodeLookup<TPublicKey, TNode>, IteratorNodeLookup<TPublicKey, THash, TNode>>()
            .AddSingleton<INodeHealthTracker<TNode>, NodeHealthTracker<TPublicKey, THash, TNode>>();
    }
}
