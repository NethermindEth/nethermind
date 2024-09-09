// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public class BucketListRoutingTable<TNode>: IRoutingTable<TNode> where TNode : notnull
{
    private readonly KBucket<TNode>[] _buckets;
    private readonly ValueHash256 _currentNodeIdAsHash;
    private readonly int _kSize;

    public BucketListRoutingTable(ValueHash256 currentNodeIdAsHash, int kSize)
    {
        // Note: It does not have to be this much. In practice, only like 16 of these bucket get populated.
        _buckets = new KBucket<TNode>[Hash256XORUtils.MaxDistance + 1];
        for (int i = 0; i < Hash256XORUtils.MaxDistance + 1; i++)
        {
            _buckets[i] = new KBucket<TNode>(kSize);
        }

        _currentNodeIdAsHash = currentNodeIdAsHash;
        _kSize = kSize;
    }

    private KBucket<TNode> GetBucket(in ValueHash256 hash)
    {
        int idx = Hash256XORUtils.CalculateDistance(hash, _currentNodeIdAsHash);
        return _buckets[idx];
    }

    public BucketAddResult TryAddOrRefresh(in ValueHash256 hash, TNode item, out TNode? toRefresh)
    {
        return GetBucket(hash).TryAddOrRefresh(hash, item, out toRefresh);
    }

    public void Remove(in ValueHash256 hash)
    {
        GetBucket(hash).Remove(hash);
    }

    public TNode[] GetAllAtDistance(int i)
    {
        return _buckets[i].GetAll();
    }

    public IEnumerable<ValueHash256> IterateBucketRandomHashes()
    {
        for (var i = 0; i < _buckets.Length; i++)
        {
            if (_buckets[i].Count > 0)
            {
                ValueHash256 nodeToLookup = Hash256XORUtils.GetRandomHashAtDistance(_currentNodeIdAsHash, i);
                yield return nodeToLookup;
            }
        }
    }

    public IEnumerable<TNode> IterateNeighbour(ValueHash256 hash)
    {
        int startingDistance = Hash256XORUtils.CalculateDistance(_currentNodeIdAsHash, hash);
        foreach (var bucketToGet in EnumerateBucket(startingDistance))
        {
            foreach (TNode bucketContent in GetAllAtDistance(bucketToGet))
            {
                yield return bucketContent;
            }
        }

    }

    public TNode[] GetKNearestNeighbour(ValueHash256 hash)
    {
        return IterateNeighbour(hash).Take(_kSize).ToArray();
    }

    private IEnumerable<int> EnumerateBucket(int startingDistance)
    {
        // Note, without a tree based routing table, we don't exactly know
        // which way (left or right) is the right way to go. So this is all approximate.
        // Well, even with a full tree, it would still be approximate, just that it would
        // be a bit more accurate.
        yield return startingDistance;
        int left = startingDistance - 1;
        int right = startingDistance + 1;
        while (left > 0 || right <= Hash256XORUtils.MaxDistance)
        {
            if (left > 0)
            {
                yield return left;
            }

            if (right <= Hash256XORUtils.MaxDistance)
            {
                yield return right;
            }

            left -= 1;
            right += 1;
        }
    }
}
