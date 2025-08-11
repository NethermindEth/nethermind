// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia;

public interface IRoutingTable<THash, TNode> where TNode : notnull where THash : struct
{
    BucketAddResult TryAddOrRefresh(in THash hash, TNode item, out TNode? toRefresh);
    bool Remove(in THash hash);
    TNode[] GetKNearestNeighbour(THash hash, THash? exclude = null, bool excludeSelf = false);
    TNode[] GetAllAtDistance(int i);
    IEnumerable<(THash Prefix, int Distance, KBucket<THash, TNode> Bucket)> IterateBuckets();
    TNode? GetByHash(THash nodeId);
    void LogDebugInfo();
    event EventHandler<TNode>? OnNodeAdded;
    int Size { get; }
}
