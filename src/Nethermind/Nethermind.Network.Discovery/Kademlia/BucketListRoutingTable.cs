// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Kademlia;

public class BucketListRoutingTable<TNode>: IRoutingTable<TNode> where TNode : notnull
{
    private readonly ILogger _logger;
    private readonly KBucket<TNode>[] _buckets;
    private readonly ValueHash256 _currentNodeIdAsHash;
    private readonly int _kSize;

    // TODO: Double check and probably make lockless
    private readonly McsLock _lock = new McsLock();

    public BucketListRoutingTable(KademliaConfig<TNode> config, INodeHashProvider<TNode> nodeHashProvider, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<BucketListRoutingTable<TNode>>();

        // Note: It does not have to be this much. In practice, only like 16 of these bucket get populated.
        _buckets = new KBucket<TNode>[Hash256XorUtils.MaxDistance + 1];
        for (int i = 0; i < Hash256XorUtils.MaxDistance + 1; i++)
        {
            _buckets[i] = new KBucket<TNode>(config.KSize);
        }

        _currentNodeIdAsHash = nodeHashProvider.GetHash(config.CurrentNodeId);
        _kSize = config.KSize;
    }

    private KBucket<TNode> GetBucket(in ValueHash256 hash)
    {
        int idx = Hash256XorUtils.CalculateDistance(hash, _currentNodeIdAsHash);
        return _buckets[idx];
    }

    public BucketAddResult TryAddOrRefresh(in ValueHash256 hash, TNode item, out TNode? toRefresh)
    {
        using McsLock.Disposable _ = _lock.Acquire();
        return GetBucket(hash).TryAddOrRefresh(hash, item, out toRefresh);
    }

    public bool Remove(in ValueHash256 hash)
    {
        using McsLock.Disposable _ = _lock.Acquire();
        return GetBucket(hash).RemoveAndReplace(hash);
    }

    public TNode[] GetAllAtDistance(int i)
    {
        using McsLock.Disposable _ = _lock.Acquire();
        return _buckets[i].GetAll();
    }

    public IEnumerable<ValueHash256> IterateBucketRandomHashes()
    {
        for (var i = 0; i < _buckets.Length; i++)
        {
            if (_buckets[i].Count > 0)
            {
                ValueHash256 nodeToLookup = Hash256XorUtils.GetRandomHashAtDistance(_currentNodeIdAsHash, i);
                yield return nodeToLookup;
            }
        }
    }

    public TNode? GetByHash(ValueHash256 hash)
    {
        return GetBucket(hash).GetByHash(hash);
    }

    private IEnumerable<(ValueHash256, TNode)> IterateNeighbour(ValueHash256 hash)
    {
        int startingDistance = Hash256XorUtils.CalculateDistance(_currentNodeIdAsHash, hash);
        foreach (var bucketToGet in EnumerateBucket(startingDistance))
        {
            foreach (var entry in bucketToGet.GetAllWithHash())
            {
                yield return entry;
            }
        }
    }

    public TNode[] GetKNearestNeighbour(ValueHash256 hash, ValueHash256? exclude, bool excludeSelf)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        int startingDistance = Hash256XorUtils.CalculateDistance(_currentNodeIdAsHash, hash);
        KBucket<TNode> firstBucket = _buckets[startingDistance];
        bool shouldNotContainExcludedNode = exclude == null || !firstBucket.ContainsNode(exclude.Value);
        bool shouldNotContainSelf = excludeSelf == false || !firstBucket.ContainsNode(_currentNodeIdAsHash);

        if (shouldNotContainExcludedNode && shouldNotContainSelf)
        {
            TNode[] nodes = firstBucket.GetAll();
            if (nodes.Length == _kSize)
            {
                // Fast path. In theory, most of the time, this would be the taken path, where no array
                // concatenation or creation is needed.
                return nodes;
            }
        }

        var iterator = IterateNeighbour(hash);

        if (exclude != null)
            iterator = iterator
                .Where(kv => kv.Item1 != exclude.Value);

        if (excludeSelf)
            iterator = iterator
                .Where(kv => kv.Item1 != _currentNodeIdAsHash);

        return iterator.Take(_kSize)
            .Select(kv => kv.Item2)
            .ToArray();
    }

    private IEnumerable<KBucket<TNode>> EnumerateBucket(int startingDistance)
    {
        // Note, without a tree based routing table, we don't exactly know
        // which way (left or right) is the right way to go. So this is all approximate.
        // Well, even with a full tree, it would still be approximate, just that it would
        // be a bit more accurate.
        yield return _buckets[startingDistance];
        int left = startingDistance - 1;
        int right = startingDistance + 1;
        while (left >= 0 || right <= Hash256XorUtils.MaxDistance)
        {
            if (left >= 0)
            {
                yield return _buckets[left];
            }

            if (right <= Hash256XorUtils.MaxDistance)
            {
                yield return _buckets[right];
            }

            left -= 1;
            right += 1;
        }
    }

    public void LogDebugInfo()
    {
        _logger.Debug($"Bucket sizes {string.Join(", ", _buckets.Select(b => b.Count))}");
    }
}
