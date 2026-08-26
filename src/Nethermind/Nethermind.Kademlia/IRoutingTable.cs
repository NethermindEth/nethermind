// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

public interface IRoutingTable<TNode, TKadKey>
    where TNode : notnull
    where TKadKey : notnull
{
    BucketAddResult TryAddOrRefresh(in TKadKey hash, TNode item, out TNode? toRefresh);
    bool Remove(in TKadKey hash);
    TNode[] GetKNearestNeighbour(TKadKey hash, bool excludeSelf = false);
    TNode[] GetKNearestNeighbourExcluding(TKadKey hash, TKadKey exclude, bool excludeSelf = false);
    TNode[] GetAllAtDistance(int i);
    IEnumerable<RoutingTableBucket<TNode, TKadKey>> IterateBuckets();
    TNode? GetByHash(TKadKey nodeId);
    void LogDebugInfo();
    event EventHandler<TNode>? OnNodeAdded;
    event EventHandler<TNode>? OnNodeRemoved;

    /// <summary>
    /// Returns how many nodes the table holds and how many slots its current buckets provide.
    /// </summary>
    /// <remarks>Implementations may walk every bucket, so this is not meant for hot paths.</remarks>
    RoutingTableOccupancy GetOccupancy();
}
