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
    int Size { get; }
}
