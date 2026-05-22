// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

public interface IRoutingTable<TNode> where TNode : notnull
{
    BucketAddResult TryAddOrRefresh(in KademliaHash hash, TNode item, out TNode? toRefresh);
    bool Remove(in KademliaHash hash);
    TNode[] GetKNearestNeighbour(KademliaHash hash, KademliaHash? exclude = null, bool excludeSelf = false);
    TNode[] GetAllAtDistance(int i);
    IEnumerable<(KademliaHash Prefix, int Distance, KBucket<TNode> Bucket)> IterateBuckets();
    TNode? GetByHash(KademliaHash nodeId);
    void LogDebugInfo();
    event EventHandler<TNode>? OnNodeAdded;
    event EventHandler<TNode>? OnNodeRemoved;
    int Size { get; }
}
