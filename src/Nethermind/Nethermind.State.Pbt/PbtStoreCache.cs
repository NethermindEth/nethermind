// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Caches PBT leaf blobs and trie-node groups across processing scopes.</summary>
/// <remarks>
/// The six value-kind/partition buckets have independent byte budgets. A cached value is valid only
/// for the hash supplied by its caller, which prevents a value from one fork being served to another.
/// Every retained value and every returned value owns a separate <see cref="RefCountingMemory"/> lease.
/// </remarks>
/// <param name="config">The independent byte budget of each cache bucket.</param>
public sealed class PbtStoreCache(IPbtConfig config) : IDisposable
{
    private readonly Bucket<Stem>[] _leafBlobs =
    [
        new(config.AccountLeafBlobCacheSizeBudget),
        new(config.CodeLeafBlobCacheSizeBudget),
        new(config.StorageLeafBlobCacheSizeBudget),
    ];
    private readonly Bucket<TrieNodeKey>[] _trieNodes =
    [
        new(config.AccountTrieNodeCacheSizeBudget, trackTrieNodeMetrics: true),
        new(config.CodeTrieNodeCacheSizeBudget, trackTrieNodeMetrics: true),
        new(config.StorageTrieNodeCacheSizeBudget, trackTrieNodeMetrics: true),
    ];

    /// <summary>Returns a caller-owned lease when <paramref name="stem"/> is cached for <paramref name="hash"/>.</summary>
    public RefCountingMemory? GetLeafBlob(in Stem stem, in ValueHash256 hash) =>
        _leafBlobs[(int)PbtPartitions.Of(stem)].Get(stem, stem.Bytes[0], hash);

    /// <summary>Retains a cache-owned lease on <paramref name="blob"/> for <paramref name="stem"/> and <paramref name="hash"/>.</summary>
    public void SetLeafBlob(in Stem stem, in ValueHash256 hash, RefCountingMemory blob) =>
        _leafBlobs[(int)PbtPartitions.Of(stem)].Set(stem, stem.Bytes[0], hash, blob);

    /// <summary>Returns a caller-owned lease when <paramref name="key"/> is cached for <paramref name="hash"/>.</summary>
    public RefCountingMemory? GetTrieNode(in TrieNodeKey key, in ValueHash256 hash) =>
        _trieNodes[(int)PbtPartitions.Of(key)].Get(key, key.Path.Bytes[0], hash);

    /// <summary>Retains a cache-owned lease on <paramref name="node"/> for <paramref name="key"/> and <paramref name="hash"/>.</summary>
    public void SetTrieNode(in TrieNodeKey key, in ValueHash256 hash, RefCountingMemory node) =>
        _trieNodes[(int)PbtPartitions.Of(key)].Set(key, key.Path.Bytes[0], hash, node);

    /// <summary>Releases every cache-owned lease.</summary>
    public void Dispose()
    {
        for (int i = 0; i < PbtPartitions.Count; i++)
        {
            _leafBlobs[i].Dispose();
            _trieNodes[i].Dispose();
        }
    }

    /// <remarks>
    /// Matches the flat-state trie cache: the key's first path byte selects one of 256 shards, its
    /// hash code maps directly to an array bucket, collisions replace, and over-budget eviction clears
    /// whole shards in round-robin order. Shard locks additionally protect ref-counted lease transfer.
    /// </remarks>
    private sealed class Bucket<TKey> : IDisposable where TKey : notnull
    {
        private const int EstimatedSizePerEntry = 700;
        private const double UtilRatio = 0.25;
        private const int ShardCount = 256;

        private readonly Entry?[][] _cacheShards;
        private readonly Lock[] _shardLocks = new Lock[ShardCount];
        private readonly long[] _shardMemoryUsages = new long[ShardCount];
        private readonly Lock _pruneLock = new();
        private readonly long _maxCacheMemoryThreshold;
        private readonly int _bucketMask;
        private readonly bool _trackTrieNodeMetrics;

        private long _currentMemoryUsage;
        private int _nextShardToClear;
        private int _isDisposed;

        public Bucket(ulong budget, bool trackTrieNodeMetrics = false)
        {
            _trackTrieNodeMetrics = trackTrieNodeMetrics;
            _maxCacheMemoryThreshold = budget > long.MaxValue ? long.MaxValue : (long)budget;
            long totalEntryCount = _maxCacheMemoryThreshold / EstimatedSizePerEntry;
            long targetBucketSize = (long)((totalEntryCount / UtilRatio) / ShardCount);
            _bucketMask = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, targetBucketSize)) - 1;

            _cacheShards = new Entry[ShardCount][];
            for (int i = 0; i < ShardCount; i++)
            {
                _cacheShards[i] = new Entry[_bucketMask + 1];
                _shardLocks[i] = new Lock();
            }
        }

        public RefCountingMemory? Get(TKey key, int shardIdx, in ValueHash256 hash)
        {
            int bucketIdx = (key.GetHashCode() & int.MaxValue) & _bucketMask;
            lock (_shardLocks[shardIdx])
            {
                Entry? entry = _cacheShards[shardIdx][bucketIdx];
                if (Volatile.Read(ref _isDisposed) != 0 || entry is null || !entry.Key.Equals(key) || entry.Hash != hash)
                {
                    if (_trackTrieNodeMetrics) Metrics.IncrementPbtTrieNodeCacheMisses();
                    return null;
                }

                entry.Memory.AcquireLease();
                if (_trackTrieNodeMetrics) Metrics.IncrementPbtTrieNodeCacheHits();
                return entry.Memory;
            }
        }

        public void Set(TKey key, int shardIdx, in ValueHash256 hash, RefCountingMemory memory)
        {
            int size = memory.GetSpan().Length;
            if (_maxCacheMemoryThreshold == 0 || size > _maxCacheMemoryThreshold || Volatile.Read(ref _isDisposed) != 0) return;

            memory.AcquireLease();
            int bucketIdx = (key.GetHashCode() & int.MaxValue) & _bucketMask;
            lock (_shardLocks[shardIdx])
            {
                if (Volatile.Read(ref _isDisposed) != 0)
                {
                    ((IDisposable)memory).Dispose();
                    return;
                }

                Entry? old = _cacheShards[shardIdx][bucketIdx];
                _cacheShards[shardIdx][bucketIdx] = new Entry(key, hash, memory, size);

                int previousSize = old?.Size ?? 0;
                int delta = size - previousSize;
                _shardMemoryUsages[shardIdx] += delta;
                Interlocked.Add(ref _currentMemoryUsage, delta);
                if (_trackTrieNodeMetrics) Metrics.AddPbtTrieNodeCacheMemory(delta);
                ((IDisposable?)old?.Memory)?.Dispose();
            }

            Prune();
        }

        private void Prune()
        {
            if (Volatile.Read(ref _currentMemoryUsage) <= _maxCacheMemoryThreshold) return;

            lock (_pruneLock)
            {
                while (Volatile.Read(ref _currentMemoryUsage) > _maxCacheMemoryThreshold)
                {
                    ClearShard(_nextShardToClear);
                    _nextShardToClear = (_nextShardToClear + 1) & 255;
                }
            }
        }

        public void Dispose()
        {
            lock (_pruneLock)
            {
                if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
                for (int i = 0; i < ShardCount; i++) ClearShard(i);
                _nextShardToClear = 0;
            }
        }

        private void ClearShard(int shardIdx)
        {
            lock (_shardLocks[shardIdx])
            {
                Entry?[] shard = _cacheShards[shardIdx];
                for (int i = 0; i < shard.Length; i++) ((IDisposable?)shard[i]?.Memory)?.Dispose();
                Array.Clear(shard);

                long freedMemory = _shardMemoryUsages[shardIdx];
                _shardMemoryUsages[shardIdx] = 0;
                Interlocked.Add(ref _currentMemoryUsage, -freedMemory);
                if (_trackTrieNodeMetrics) Metrics.AddPbtTrieNodeCacheMemory(-freedMemory);
            }
        }

        private sealed record Entry(TKey Key, ValueHash256 Hash, RefCountingMemory Memory, int Size);
    }
}
