// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public interface IRoutingTable<TNode>
{
    BucketAddResult TryAddOrRefresh(in ValueHash256 hash, TNode item, out TNode? toRefresh);
    void Remove(in ValueHash256 hash);
    TNode[] GetKNearestNeighbour(ValueHash256 hash, ValueHash256? exclude);
    TNode[] GetAllAtDistance(int i);
    IEnumerable<ValueHash256> IterateBucketRandomHashes();
}
