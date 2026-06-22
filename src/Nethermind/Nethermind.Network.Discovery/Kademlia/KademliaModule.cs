// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Kademlia;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// A kademlia module.
/// Application is expected to expose a
/// - <see cref="IKademliaMessageSender{TKey, TNode}"/>
/// - <see cref="IKeyOperator{TKey, TNode, TKadKey}"/>
/// - <see cref="IKademliaDistance{TKadKey}"/>
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
/// <typeparam name="TKadKey">Type of the key-space value used by the routing table.</typeparam>
public class KademliaModule<TKey, TNode, TKadKey> : Module
    where TNode : notnull
    where TKadKey : notnull
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IKademlia<TKey, TNode>, Kademlia<TKey, TNode, TKadKey>>()
            .AddSingleton<IKademliaDiscovery<TKey, TNode>, RandomWalkKademliaDiscovery<TKey, TNode, TKadKey>>()
            .AddSingleton<ILookupAlgo<TNode, TKadKey>, LookupKNearestNeighbour<TKey, TNode, TKadKey>>()
            .AddSingleton<INodeHashProvider<TNode, TKadKey>, FromKeyNodeHashProvider<TKey, TNode, TKadKey>>()
            .AddSingleton<IRoutingTable<TNode, TKadKey>, KBucketTree<TNode, TKadKey>>()
            .AddSingleton<INodeHealthTracker<TNode>, NodeHealthTracker<TKey, TNode, TKadKey>>();
    }
}
