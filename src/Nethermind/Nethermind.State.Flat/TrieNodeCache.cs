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
/// Wraps a hash and RLP bytes as a single atomic reference so concurrent reads/writes do not produce torn reads.
/// </summary>
internal sealed class RlpCacheEntry(Hash256 hash, byte[] rlp)
{
    public readonly Hash256 Hash = hash;
    public readonly byte[] Rlp = rlp;
}

/// <summary>
/// A specialized RLP byte-array cache. It uses a sharded array of <see cref="RlpCacheEntry"/> as the cache with the
/// hashcode of the path mapping to the array position directly. If a collision happens, it just replaces the old entry.
/// When trying to get the node, the node hash must be checked to ensure the right node is the one fetched.
/// The use of sharding is so that when memory target is exceeded, whole shards which are grouped by tree path are cleared.
/// This improves block cache hit rate as trie nodes of similar subtree tend to be clustered together.
/// </summary>
public sealed class TrieNodeCache : ITrieNodeCache
{
    private const int EstimatedSizePerNode = 500;
    private const double UtilRatio = 0.25;
    private const int ShardCount = 256;

    private readonly ILogger _logger;
    private readonly RlpCacheEntry?[][] _cacheShards;
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

        _cacheShards = new RlpCacheEntry[ShardCount][];
        for (int i = 0; i < ShardCount; i++)
        {
            _cacheShards[i] = new RlpCacheEntry[_bucketSize];
        }

        _shardMemoryUsages = new long[ShardCount];
        _maxCacheMemoryThreshold = maxCacheMemoryThreshold;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int, int) GetShardAndHashCode(Hash256? address, in TreePath path)
    {
        int h1;

        int shardIdx = path.Path.Bytes[0];
        if (address is not null)
        {
            // Add address byte so that the root nodes of storage does not all sit in a single shard
            shardIdx += address.Bytes[0];
            shardIdx %= 256;
            h1 = address.GetHashCode();
        }
        else
        {
            h1 = 0;
        }

        int h2 = path.GetHashCode();

        // Simple XOR is often enough and faster than HashCode.Combine for this use case
        int hashCode = (h1 ^ h2) & int.MaxValue;

        return (shardIdx, hashCode);
    }

    public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out byte[]? rlp)
    {
        (int shardIdx, int hashCode) = GetShardAndHashCode(address, in path);
        int bucketIdx = hashCode & _bucketMask;

        RlpCacheEntry? entry = _cacheShards[shardIdx][bucketIdx];
        if (entry is not null && entry.Hash == hash)
        {
            rlp = entry.Rlp;
            return true;
        }

        rlp = null;
        return false;
    }

    public void Add(TransientResource transientResource)
    {
        if (_maxCacheMemoryThreshold == 0)
        {
            return;
        }

        void AddToCacheWithHashCode(int shardIdx, int hashCode, RlpCacheEntry newEntry)
        {
            int bucketIdx = hashCode & _bucketMask;
            long entrySize = newEntry.Rlp.Length + 64L; // RLP bytes + hash + object overhead
            Interlocked.Add(ref _shardMemoryUsages[shardIdx], entrySize);

            RlpCacheEntry? oldEntry = Interlocked.Exchange(ref _cacheShards[shardIdx][bucketIdx], newEntry);
            if (oldEntry is not null)
            {
                long oldSize = oldEntry.Rlp.Length + 64L;
                Interlocked.Add(ref _shardMemoryUsages[shardIdx], -oldSize);
            }
        }

        Parallel.For(0, ShardCount, (i) =>
        {
            (int hashCode, RlpCacheEntry? entry)[] shard = transientResource.Nodes.Shards[i];
            for (int j = 0; j < shard.Length; j++)
            {
                if (shard[j].entry is { } newEntry) AddToCacheWithHashCode(i, shard[j].hashCode, newEntry);
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
    /// Clears all cached RLP entries.
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < ShardCount; i++)
        {
            Array.Clear(_cacheShards[i]);
            Interlocked.Exchange(ref _shardMemoryUsages[i], 0);
        }
        _nextShardToClear = 0;
        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = 0;
    }

    /// <summary>
    /// Small cache for use in <see cref="TransientResource"/>. Also sharded with the same shard mechanics so that
    /// adding to trie node cache can be done in parallel.
    /// </summary>
    public class ChildCache
    {
        private readonly (int hashCode, RlpCacheEntry? entry)[][] _shards;
        private int _count = 0;
        private int _mask;
        private int _shardSize;

        public int Count => _count;
        public int Capacity => _shards.Length * _shardSize;
        internal (int hashCode, RlpCacheEntry? entry)[][] Shards => _shards;

        public ChildCache(int size)
        {
            int powerOfTwoSize = (int)BitOperations.RoundUpToPowerOf2((uint)(size + ShardCount - 1) / ShardCount);
            _shards = new (int, RlpCacheEntry?)[ShardCount][];
            _mask = powerOfTwoSize - 1;
            _shardSize = powerOfTwoSize;
            CreateCacheArray(_shardSize);
        }

        private void CreateCacheArray(int size)
        {
            for (int i = 0; i < ShardCount; i++) _shards[i] = new (int, RlpCacheEntry?)[size];
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

        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out byte[]? rlp)
        {
            (int shardIdx, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;
            (int hashCode, RlpCacheEntry? entry) cached = _shards[shardIdx][idx]; // Copy struct once

            if (cached.hashCode != hashCode)
            {
                rlp = null;
                return false;
            }

            RlpCacheEntry? maybeEntry = cached.entry; // Store to prevent concurrency issue
            if (maybeEntry is null || maybeEntry.Hash != hash)
            {
                rlp = null;
                return false;
            }

            rlp = maybeEntry.Rlp;
            return true;
        }

        public void Set(Hash256? address, in TreePath path, Hash256 hash, byte[] rlp)
        {
            (int shard, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            _count++; // Track count

            _shards[shard][idx] = (hashCode, new RlpCacheEntry(hash, rlp));
        }
    }
}
