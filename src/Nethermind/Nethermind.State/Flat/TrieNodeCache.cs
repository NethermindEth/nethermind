// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// The trienode cache significantly reduce the amount of RLP needed to be read by almost 60%. is a populated on the
/// latest snapshot instead of on the last one. This has a slightly better hit rate and uses less memory overall.
/// </summary>
public class TrieNodeCache
{
    private const int EstimatedSizePerNode = 700; // More or less the average size.
    private const double UtilRatio = 0.5;

    // You *could* use a single large bucket and just let them replace each other. However, clearing by treepath
    // is more efficent block cache wise.
    private TrieNode[][] _cacheShards;

    private long[] _shardMemoryUsages;
    private long _estimatedMemoryUsage = 0;

    private int _nextShardToClear = 0;
    private readonly long _maxCacheMemoryThreshold;
    private readonly int _bucketSize;
    private readonly int _shardCount = 256;

    private readonly ILogger _logger;

    public TrieNodeCache(long maxCacheMemoryThreshold, ILogManager logManager)
    {
        long totalNodeCount = (maxCacheMemoryThreshold / EstimatedSizePerNode);
        _bucketSize = (int)(totalNodeCount / _shardCount / UtilRatio);
        _cacheShards = new TrieNode[_shardCount][];
        for (int i = 0; i < _shardCount; i++)
        {
            _cacheShards[i] = new TrieNode[_bucketSize];
        }
        _shardMemoryUsages = new long[_shardCount];
        _maxCacheMemoryThreshold = maxCacheMemoryThreshold;
        _logger = logManager.GetClassLogger<TrieNodeCache>();
    }

    private (int, int) GetShardAndBucketIdx(Hash256? address, in TreePath path)
    {
        int shardIdx;
        // Separate by tree partition so that when pruned, whole partition is removed. This is because it is
        // more efficient to load nodes from the same partition.
        if (address is null) shardIdx = path.Path.Bytes[0];
        else shardIdx = address.Bytes[0];

        var addressHash = address != default ? address.GetHashCode() : 1;
        int hashCode = HashCode.Combine(addressHash, path.GetHashCode());
        int bucketIdx =  (hashCode & int.MaxValue) % _bucketSize;
        return (shardIdx, bucketIdx);
    }

    public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, out TrieNode node)
    {
        (int shardIdx, int bucketIdx) = GetShardAndBucketIdx(address, path);
        TrieNode? maybeNode = _cacheShards[shardIdx][bucketIdx];
        if (maybeNode != null && maybeNode.Keccak == hash)
        {
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            node = maybeNode;
            return true;
        }

        node = null;
        return false;
    }

    public void Add(Snapshot snapshot, CachedResource cachedResource)
    {
        if (_maxCacheMemoryThreshold == 0)
        {
            // Note: still need to be done
            // this was explicitly skipped during block processing to make commit run faster,
            // but if this is not done, it will ccause OOM.
            foreach (var kv in cachedResource.TrieWarmerLoadedNodes)
            {
                kv.Value.PrunePersistedRecursively(1);
            }

            foreach (var kv in cachedResource.LoadedStorageNodes)
            {
                kv.Value.PrunePersistedRecursively(1);
            }

            foreach (var kv in snapshot.StateNodes)
            {
                kv.Value.PrunePersistedRecursively(1);
            }

            foreach (var kv in snapshot.StorageNodes)
            {
                kv.Value.PrunePersistedRecursively(1);
            }

            return;
        }

        void AddToCache(Hash256? address, in TreePath path, TrieNode newNode)
        {
            (int shardIdx, int bucketIdx) = GetShardAndBucketIdx(address, path);

            long memory = newNode.GetMemorySize(false);
            _shardMemoryUsages[shardIdx] += memory;
            Interlocked.Add(ref _estimatedMemoryUsage, memory);
            _estimatedMemoryUsage += memory;

            TrieNode? oldNode = Interlocked.Exchange(ref _cacheShards[shardIdx][bucketIdx], newNode);
            if (oldNode is not null)
            {
                memory = oldNode.GetMemorySize(false);
                oldNode.PrunePersistedRecursively(1);
                _shardMemoryUsages[shardIdx] -= memory;
                Interlocked.Add(ref _estimatedMemoryUsage, -memory);
            }
        }

        foreach (var kv in cachedResource.TrieWarmerLoadedNodes)
        {
            kv.Value.PrunePersistedRecursively(1);
            if (!snapshot.TryGetStateNode(kv.Key, out _))
            {
                AddToCache(null, kv.Key, kv.Value);
            }
        }

        foreach (var kv in cachedResource.LoadedStorageNodes)
        {
            if (kv.Value is null) continue;
            kv.Value.PrunePersistedRecursively(1);
            if (!snapshot.TryGetStorageNode(kv.Key.Item1, kv.Key.Item2, out _))
            {
                AddToCache(kv.Key.Item1, kv.Key.Item2, kv.Value);
            }
        }

        foreach (var kv in snapshot.StateNodes)
        {
            kv.Value.PrunePersistedRecursively(1);
            AddToCache(null, kv.Key, kv.Value);
        }

        foreach (var kv in snapshot.StorageNodes)
        {
            kv.Value.PrunePersistedRecursively(1);
            AddToCache(kv.Key.Item1, kv.Key.Item2, kv.Value);
        }

        long prevMemory = _estimatedMemoryUsage;
        bool wasPruned = false;
        // TODO: Make 16 parameter configurable.
        while (snapshot.To.blockNumber % 16 == 0 && _estimatedMemoryUsage > _maxCacheMemoryThreshold)
        {
            wasPruned = true;
            int shardToClear = _nextShardToClear;

            for (int i = 0; i < _bucketSize; i++)
            {
                TrieNode? node = _cacheShards[shardToClear][i];
                if (node is not null)
                {
                    node.PrunePersistedRecursively(1);
                    _cacheShards[shardToClear][i] = null;
                }
            }

            _shardMemoryUsages[shardToClear] = 0;
            long recalculatedTotalMemory = 0;
            foreach (var shardMemoryUsage in _shardMemoryUsages)
            {
                recalculatedTotalMemory += shardMemoryUsage;
            }

            _estimatedMemoryUsage = recalculatedTotalMemory;

            _nextShardToClear += 1;
            _nextShardToClear %= _shardCount;
        }

        if (wasPruned)
        {
            _logger.Info($"Pruning trie cache from {prevMemory} to {_estimatedMemoryUsage}");
        }

        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = _estimatedMemoryUsage;
    }
}
