// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.Kademlia;

/// <summary>
/// Creates the default Kademlia routing table, lookup algorithm, health tracker, and facade.
/// </summary>
public static class KademliaFactory
{
    /// <summary>
    /// Creates the default Kademlia component graph for consumers that do not use a dependency-injection container.
    /// </summary>
    /// <param name="keyOperator">Maps nodes and lookup keys to the Kademlia key space.</param>
    /// <param name="distance">Compares and manipulates values in the Kademlia key space.</param>
    /// <param name="sender">Sends protocol-specific ping and find-neighbour requests.</param>
    /// <param name="config">Kademlia table and maintenance settings.</param>
    /// <param name="logManager">Optional log manager. When omitted, logging is disabled.</param>
    /// <param name="timeProvider">Optional time provider used for bucket refresh scheduling.</param>
    public static KademliaComponents<TKey, TNode, TKadKey> Create<TKey, TNode, TKadKey>(
        IKeyOperator<TKey, TNode, TKadKey> keyOperator,
        IKademliaDistance<TKadKey> distance,
        IKademliaMessageSender<TKey, TNode> sender,
        KademliaConfig<TNode> config,
        ILogManager? logManager = null,
        TimeProvider? timeProvider = null)
        where TNode : notnull
        where TKadKey : notnull
    {
        ArgumentNullException.ThrowIfNull(keyOperator);
        ArgumentNullException.ThrowIfNull(distance);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(config);

        FromKeyNodeHashProvider<TKey, TNode, TKadKey> nodeHashProvider = new(keyOperator);
        KBucketTree<TNode, TKadKey> routingTable = new(config, nodeHashProvider, distance, logManager);
        NodeHealthTracker<TKey, TNode, TKadKey> nodeHealthTracker = new(config, routingTable, nodeHashProvider, sender, logManager);
        LookupKNearestNeighbour<TKey, TNode, TKadKey> lookup = new(routingTable, nodeHashProvider, distance, nodeHealthTracker, config, logManager);
        Kademlia<TKey, TNode, TKadKey> kademlia = new(
            keyOperator,
            sender,
            routingTable,
            lookup,
            nodeHealthTracker,
            config,
            logManager,
            timeProvider);

        return new KademliaComponents<TKey, TNode, TKadKey>(
            kademlia,
            routingTable,
            lookup,
            nodeHashProvider,
            nodeHealthTracker);
    }
}

/// <summary>
/// Owns a Kademlia instance and the default components created for it.
/// </summary>
public sealed class KademliaComponents<TKey, TNode, TKadKey>(
    Kademlia<TKey, TNode, TKadKey> kademlia,
    IRoutingTable<TNode, TKadKey> routingTable,
    ILookupAlgo<TNode, TKadKey> lookup,
    INodeHashProvider<TNode, TKadKey> nodeHashProvider,
    NodeHealthTracker<TKey, TNode, TKadKey> nodeHealthTracker) : IDisposable
    where TNode : notnull
    where TKadKey : notnull
{
    /// <summary>
    /// The high-level Kademlia facade.
    /// </summary>
    public Kademlia<TKey, TNode, TKadKey> Kademlia { get; } = kademlia;

    /// <summary>
    /// The routing table used by <see cref="Kademlia"/>.
    /// </summary>
    public IRoutingTable<TNode, TKadKey> RoutingTable { get; } = routingTable;

    /// <summary>
    /// The iterative closest-node lookup algorithm used by <see cref="Kademlia"/>.
    /// </summary>
    public ILookupAlgo<TNode, TKadKey> Lookup { get; } = lookup;

    /// <summary>
    /// Maps nodes to their Kademlia hash.
    /// </summary>
    public INodeHashProvider<TNode, TKadKey> NodeHashProvider { get; } = nodeHashProvider;

    /// <summary>
    /// Tracks liveness and evicts failed nodes.
    /// </summary>
    public NodeHealthTracker<TKey, TNode, TKadKey> NodeHealthTracker { get; } = nodeHealthTracker;

    /// <inheritdoc/>
    public void Dispose() => NodeHealthTracker.Dispose();
}
