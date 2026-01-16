// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A specialized <see cref="TrieNode"/> cache. It uses a sharded array of <see cref="TrieNode"/> as the cache with the
/// hashcode of the path mapping to the array position directly. If a collision happen, it just replace the old entry.
/// When trying to get the node, the node hash must be checked to ensure the right node is the one fetched.
/// The use of sharding is so that when memory target is exceeded, whole shard which is grouped by tree path is cleared.
/// This improve block cache hit rate as trie nodes of similar subtree tend to be clustered together.
/// </summary>
public sealed class TrieNodeCache : ITrieNodeCache
{
    private const int EstimatedSizePerNode = 700;
    private const double UtilRatio = 0.25;
    private const int ShardCount = 256;

    private readonly ILogger _logger;
    private readonly TrieNode?[][] _cacheShards;
    private readonly long[] _shardMemoryUsages;
    private readonly long _maxCacheMemoryThreshold;
    private readonly int _bucketSize;
    private readonly int _bucketMask;

    private int _nextShardToClear = 0;

    public TrieNodeCache(IFlatDbConfig flatDbConfig, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<TrieNodeCache>();

        long maxCacheMemoryThreshold = flatDbConfig.TrieCacheMemoryBudget;
        long totalNodeCount = (maxCacheMemoryThreshold / EstimatedSizePerNode);

        int targetBucketSize = (int)((totalNodeCount / UtilRatio) / ShardCount);
        _bucketSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, targetBucketSize));
        _bucketMask = _bucketSize - 1;

        _cacheShards = new TrieNode[ShardCount][];
        for (int i = 0; i < ShardCount; i++)
        {
            _cacheShards[i] = new TrieNode[_bucketSize];
        }

        _shardMemoryUsages = new long[ShardCount];
        _maxCacheMemoryThreshold = maxCacheMemoryThreshold;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int, int) GetShardAndHashCode(Hash256? address, in TreePath path)
    {
        int shardIdx;
        int h1;

        shardIdx = path.Path.Bytes[0];
        h1 = address is null ? 0 : address.GetHashCode();

        int h2 = path.GetHashCode();

        // Simple XOR is often enough and faster than HashCode.Combine for this use case
        int hashCode = (h1 ^ h2) & int.MaxValue;

        return (shardIdx, hashCode);
    }

    public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        (int shardIdx, int hashCode) = GetShardAndHashCode(address, in path);
        int bucketIdx = hashCode & _bucketMask;

        TrieNode? maybeNode = _cacheShards[shardIdx][bucketIdx];
        if (maybeNode is not null && maybeNode.Keccak == hash)
        {
            node = maybeNode;
            return true;
        }

        node = null;
        return false;
    }

    public void Add(TransientResource transientResource)
    {
        if (_maxCacheMemoryThreshold == 0)
        {
            for (int i = 0; i < ShardCount; i++)
            {
                (int hashCode, TrieNode? node)[] shard = transientResource.Nodes.Shards[i];
                for (int j = 0; j < shard.Length; j++)
                {
                    if (shard[j].node is { } newNode) newNode.PrunePersistedRecursively(1);

                }
            }
            return;
        }

        void AddToCacheWithHashCode(int shardIdx, int hashCode, TrieNode newNode)
        {
            int bucketIdx = hashCode & _bucketMask;
            newNode.PrunePersistedRecursively(1);
            Interlocked.Add(ref _shardMemoryUsages[shardIdx], newNode.GetMemorySize(false));

            TrieNode? oldNode = Interlocked.Exchange(ref _cacheShards[shardIdx][bucketIdx], newNode);
            if (oldNode is not null)
            {
                long oldMemory = oldNode.GetMemorySize(false);
                oldNode.PrunePersistedRecursively(1);

                Interlocked.Add(ref _shardMemoryUsages[shardIdx], -oldMemory);
            }
        }

        Parallel.For(0, ShardCount, (i) =>
        {
            (int hashCode, TrieNode? node)[] shard = transientResource.Nodes.Shards[i];
            for (int j = 0; j < shard.Length; j++)
            {
                if (shard[j].node is { } newNode) AddToCacheWithHashCode(i, shard[j].hashCode, newNode);
            }
        });

        long currentTotalMemory = 0;
        for (int i = 0; i < ShardCount; i++) currentTotalMemory += _shardMemoryUsages[i];

        long prevMemory = currentTotalMemory;
        bool wasPruned = false;

        while (currentTotalMemory > _maxCacheMemoryThreshold)
        {
            wasPruned = true;
            int shardToClear = _nextShardToClear;

            // Prune any remaining reference
            for (int i = 0; i < _bucketSize; i++)
            {
                _cacheShards[shardToClear][i]?.PrunePersistedRecursively(1);
            }

            // Clear the shard
            Array.Clear(_cacheShards[shardToClear]);

            // Reset shard memory
            long freedMemory = Interlocked.Exchange(ref _shardMemoryUsages[shardToClear], 0);
            currentTotalMemory -= freedMemory;

            _nextShardToClear = (_nextShardToClear + 1) & 255; // Fast modulo 256
        }

        if (wasPruned && _logger.IsTrace) _logger.Trace($"Pruning trie cache from {prevMemory} to {currentTotalMemory}");

        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = currentTotalMemory;
    }

    /// <summary>
    /// Clears all cached trie nodes.
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < ShardCount; i++)
        {
            for (int j = 0; j < _bucketSize; j++)
            {
                _cacheShards[i][j]?.PrunePersistedRecursively(1);
            }
            Array.Clear(_cacheShards[i]);
            Interlocked.Exchange(ref _shardMemoryUsages[i], 0);
        }
        _nextShardToClear = 0;
        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = 0;
    }

    /// <summary>
    /// Small cached for use in <see cref="TransientResource"/>. Its also sharded with the same shard mechanics so that
    /// when adding to trie node cache can be done in parallel.
    /// </summary>
    public class ChildCache
    {
        private (int hashCode, TrieNode? node)[][] _shards;
        private int _count = 0;
        private int _mask;
        private int _shardSize;

        public int Count => _count;
        public int Capacity => _shards.Length * _shardSize;
        public (int hashCode, TrieNode? node)[][] Shards => _shards;

        public ChildCache(int size)
        {
            int powerOfTwoSize = (int)BitOperations.RoundUpToPowerOf2((uint)(size + ShardCount - 1) / ShardCount);
            _shards = new (int, TrieNode?)[ShardCount][];
            _mask = powerOfTwoSize - 1;
            _shardSize = powerOfTwoSize;
            CreateCacheArray(_shardSize);
        }

        private void CreateCacheArray(int size)
        {
            for (int i = 0; i < ShardCount; i++) _shards[i] = new (int, TrieNode?)[size];
        }

        public void Reset()
        {
            if (_count / UtilRatio > ShardCount * _shardSize)
            {
                int newTarget = (int)(_count / UtilRatio);
                int powerOfTwoSize = (int)BitOperations.RoundUpToPowerOf2((uint)(newTarget + ShardCount - 1) / ShardCount);
                _shardSize = powerOfTwoSize;
                CreateCacheArray(_shardSize);
                _mask = powerOfTwoSize - 1;
            }
            else
            {
                for (int i = 0; i < ShardCount; i++)
                {
                    Array.Clear(_shards[i], 0, _shards[i].Length);
                }
            }

            _count = 0;
        }

        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            (int shardIdx, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;
            (int hashCode, TrieNode? node) entry = _shards[shardIdx][idx]; // Copy struct once

            if (entry.hashCode != hashCode)
            {
                node = null;
                return false;
            }

            TrieNode? maybeNode = entry.node; // Store it to prevent concurrency issue
            if (maybeNode is null || maybeNode.Keccak != hash)
            {
                node = null;
                return false;
            }

            node = maybeNode;
            return true;
        }

        public void Set(Hash256? address, in TreePath path, TrieNode node)
        {
            (int shard, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            _count++; // Track count

            _shards[shard][idx] = (hashCode, node);
        }

        public TrieNode GetOrAdd(Hash256? address, in TreePath path, TrieNode trieNode)
        {
            (int shard, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            ref (int hashCode, TrieNode? node) entry = ref _shards[shard][idx];
            TrieNode? maybeNode = entry.node; // Store it to prevent concurrency issue
            if (maybeNode is not null)
            {
                if (maybeNode.Keccak == trieNode.Keccak) return maybeNode;
            }
            else
            {
                _count++; // Track count
            }

            entry = (hashCode, trieNode);
            return trieNode;
        }
    }
}
