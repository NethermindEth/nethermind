// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Kademlia;

/// <summary>
/// Represents a Kademlia routing table.
/// </summary>
/// <remarks>Unless otherwise stated, read-only members observe only active entries and exclude the replacement cache.</remarks>
/// <typeparam name="TNode">The protocol-specific node/contact type.</typeparam>
/// <typeparam name="TKadKey">The key-space value used by the routing table.</typeparam>
public interface IRoutingTable<TNode, TKadKey>
    where TNode : notnull
    where TKadKey : notnull
{
    BucketAddResult TryAddOrRefresh(in TKadKey hash, TNode item, out TNode? toRefresh);

    /// <summary>
    /// Finds a node in either the active routing table or its replacement cache.
    /// </summary>
    /// <param name="hash">The Kademlia key to find.</param>
    /// <param name="node">The matching node when found.</param>
    /// <returns><see langword="true"/> when a matching node is found; otherwise, <see langword="false"/>.</returns>
    bool TryGet(in TKadKey hash, [MaybeNullWhen(false)] out TNode node);
    bool Remove(in TKadKey hash);
    TNode[] GetKNearestNeighbour(TKadKey hash, bool excludeSelf = false);
    TNode[] GetKNearestNeighbourExcluding(TKadKey hash, TKadKey exclude, bool excludeSelf = false);
    TNode[] GetAllAtDistance(int i);
    IEnumerable<RoutingTableBucket<TNode, TKadKey>> IterateBuckets();
    void LogDebugInfo();
    event EventHandler<TNode>? OnNodeAdded;
    event EventHandler<TNode>? OnNodeRemoved;

    /// <summary>
    /// Returns how many nodes the table holds and how many slots its current buckets provide.
    /// </summary>
    /// <remarks>Implementations may walk every bucket, so this is not meant for hot paths.</remarks>
    RoutingTableOccupancy GetOccupancy();
}
