// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Pbt.Tiles;

namespace Nethermind.State.Pbt;

/// <summary>Caches PBT leaf blobs and trie-node groups across processing scopes.</summary>
/// <remarks>
/// The six value-kind/partition buckets have independent byte budgets. A cached value is valid only
/// for the hash supplied by its caller, which prevents a value from one fork being served to another.
/// Every retained value and every returned value owns a separate <see cref="RefCountingMemory"/> lease.
/// </remarks>
public sealed class PbtStoreCache : IDisposable
{
    private const int AccountLeafBlobEstimatedSize = 4 * MemorySizes.KiB;
    private const int CodeLeafBlobEstimatedSize = 4 * MemorySizes.KiB;
    private const int StorageLeafBlobEstimatedSize = 40;

    private readonly Bucket<Stem>[] _leafBlobs;
    private readonly Bucket<TrieNodeKey>[] _trieNodes;

    /// <summary>Creates the six independently budgeted cache buckets.</summary>
    /// <param name="config">The independent byte budget of each cache bucket and the trie-node layout.</param>
    public PbtStoreCache(IPbtConfig config)
    {
        int trieNodeEstimatedSize = EstimateTrieNodeSize(config.TrieNodeLayout);
        _leafBlobs =
        [
            new(config.AccountLeafBlobCacheSizeBudget, EstimateLeafBlobSize(PbtPartition.Account), EvictionPolicy.Clock, Metrics.AccountLeafSnapshotMemory),
            new(config.CodeLeafBlobCacheSizeBudget, EstimateLeafBlobSize(PbtPartition.Code), EvictionPolicy.Clock, Metrics.CodeLeafSnapshotMemory),
            new(config.StorageLeafBlobCacheSizeBudget, EstimateLeafBlobSize(PbtPartition.Storage), EvictionPolicy.Clock, Metrics.StorageLeafSnapshotMemory),
        ];
        _trieNodes =
        [
            new(config.AccountTrieNodeCacheSizeBudget, trieNodeEstimatedSize, EvictionPolicy.ClearShard, Metrics.AccountTrieSnapshotMemory),
            new(config.CodeTrieNodeCacheSizeBudget, trieNodeEstimatedSize, EvictionPolicy.ClearShard, Metrics.CodeTrieSnapshotMemory),
            new(config.StorageTrieNodeCacheSizeBudget, trieNodeEstimatedSize, EvictionPolicy.ClearShard, Metrics.StorageTrieSnapshotMemory),
        ];
    }

    internal static int EstimateLeafBlobSize(PbtPartition partition) => partition switch
    {
        PbtPartition.Account => AccountLeafBlobEstimatedSize,
        PbtPartition.Code => CodeLeafBlobEstimatedSize,
        PbtPartition.Storage => StorageLeafBlobEstimatedSize,
        _ => throw new ArgumentOutOfRangeException(nameof(partition)),
    };

    internal static int EstimateTrieNodeSize(PbtTrieLayout layout)
    {
        PbtGroupFormat format = layout.GroupFormat();
        return layout.Tiling() switch
        {
            PbtTiling.FourLevel => EstimateGroupSize<PbtFourLevelTileLayout>(format),
            PbtTiling.FiveLevel => EstimateGroupSize<PbtFiveLevelTileLayout>(format),
            PbtTiling.SixLevel => EstimateGroupSize<PbtSixLevelTileLayout>(format),
            PbtTiling.EightLevel => EstimateGroupSize<PbtEightLevelTileLayout>(format),
            _ => throw new ArgumentOutOfRangeException(nameof(layout)),
        };
    }

    private static int EstimateGroupSize<TLayout>(PbtGroupFormat format) where TLayout : IPbtTileLayout
    {
        int storedPositions = TLayout.BoundarySlots;
        for (int width = 2; width < TLayout.BoundarySlots; width *= 2)
        {
            if (PbtLayout.TrieNodeGroupStoresInternalAtWidth(format, width))
                storedPositions += TLayout.BoundarySlots / width;
        }

        return TLayout.MaxMaskTrailerLength + PbtSubtreeStats.EncodedLength + sizeof(byte)
            + storedPositions * ValueHash256.MemorySize;
    }

    /// <summary>Returns a caller-owned lease when <paramref name="stem"/> is cached for <paramref name="hash"/>.</summary>
    public RefCountingMemory? GetLeafBlob(in Stem stem, in ValueHash256 hash)
    {
        PbtPartition partition = PbtPartitions.Of(stem);
        return _leafBlobs[(int)partition].Get(stem, ShardOf(partition, stem), hash);
    }

    /// <summary>Retains a cache-owned lease on <paramref name="blob"/> for <paramref name="stem"/> and <paramref name="hash"/>.</summary>
    public void SetLeafBlob(in Stem stem, in ValueHash256 hash, RefCountingMemory blob)
    {
        PbtPartition partition = PbtPartitions.Of(stem);
        _leafBlobs[(int)partition].Set(stem, ShardOf(partition, stem), hash, blob);
    }

    /// <summary>Returns a caller-owned lease when <paramref name="key"/> is cached for <paramref name="hash"/>.</summary>
    public RefCountingMemory? GetTrieNode(in TrieNodeKey key, in ValueHash256 hash)
    {
        PbtPartition partition = PbtPartitions.Of(key);
        return _trieNodes[(int)partition].Get(key, ShardOf(partition, key.Path), hash);
    }

    /// <summary>Retains a cache-owned lease on <paramref name="node"/> for <paramref name="key"/> and <paramref name="hash"/>.</summary>
    public void SetTrieNode(in TrieNodeKey key, in ValueHash256 hash, RefCountingMemory node)
    {
        PbtPartition partition = PbtPartitions.Of(key);
        _trieNodes[(int)partition].Set(key, ShardOf(partition, key.Path), hash, node);
    }

    /// <summary>The eight path bits below <paramref name="partition"/>'s fixed prefix.</summary>
    /// <remarks>
    /// A bucket holds one partition, whose prefix is therefore the same in every key it sees: sharding
    /// on the first path byte would spend the prefix bits on no distinction at all, leaving 15 of every
    /// 16 shards of an account or code bucket unreachable. The bits below it are the first that vary,
    /// and for a storage stem they are the head of the address prefix, so one contract's slots still
    /// land together.
    /// </remarks>
    private static int ShardOf(PbtPartition partition, in Stem path) =>
        path.GetByteAt(PbtPartitions.RootDepth(partition));

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
    /// A byte of the key's path selects one of 256 independently byte-bounded shards (see
    /// <see cref="ShardOf"/>), and its hash code maps directly to an array slot. The estimated entry
    /// size controls only array sizing and collision rate; exact payload lengths enforce capacity.
    /// Leaf shards evict individual cold entries with CLOCK, while trie shards clear together so a
    /// nearby subtree is refreshed as a unit. Shard locks additionally protect ref-counted leases.
    /// </remarks>
    private sealed class Bucket<TKey> : IDisposable where TKey : notnull
    {
        private const double UtilRatio = 0.25;
        private const int ShardCount = 256;
        private const int MinimumShardSlots = 16;
        private const int MaximumShardSlots = 1 << 30;

        private readonly Entry?[][] _cacheShards;
        private readonly Lock[] _shardLocks = new Lock[ShardCount];
        private readonly long[] _shardMemoryCapacities = new long[ShardCount];
        private readonly long[] _shardMemoryUsages = new long[ShardCount];
        private readonly int[] _clockHands = new int[ShardCount];
        private readonly EvictionPolicy _evictionPolicy;
        private readonly PbtSnapshotMemoryLabel _metricsLabel;

        private int _isDisposed;

        public Bucket(ulong budget, int estimatedSizePerEntry, EvictionPolicy evictionPolicy, PbtSnapshotMemoryLabel metricsLabel)
        {
            _evictionPolicy = evictionPolicy;
            _metricsLabel = metricsLabel;

            long totalBudget = budget > long.MaxValue ? long.MaxValue : (long)budget;
            long capacityPerShard = totalBudget / ShardCount;
            int remainder = (int)(totalBudget % ShardCount);

            _cacheShards = new Entry[ShardCount][];
            for (int i = 0; i < ShardCount; i++)
            {
                long shardCapacity = capacityPerShard + (i < remainder ? 1 : 0);
                _shardMemoryCapacities[i] = shardCapacity;
                _cacheShards[i] = new Entry[ShardSize(shardCapacity, estimatedSizePerEntry)];
                _shardLocks[i] = new Lock();
            }
        }

        private static int ShardSize(long capacity, int estimatedSizePerEntry)
        {
            long totalEntryCount = capacity / estimatedSizePerEntry;
            long targetSize = (long)(totalEntryCount / UtilRatio);
            uint boundedSize = (uint)Math.Clamp(targetSize, MinimumShardSlots, MaximumShardSlots);
            return (int)BitOperations.RoundUpToPowerOf2(boundedSize);
        }

        public RefCountingMemory? Get(TKey key, int shardIdx, in ValueHash256 hash)
        {
            Entry?[] shard = _cacheShards[shardIdx];
            int bucketIdx = (key.GetHashCode() & int.MaxValue) & (shard.Length - 1);
            lock (_shardLocks[shardIdx])
            {
                Entry? entry = shard[bucketIdx];
                if (Volatile.Read(ref _isDisposed) != 0 || entry is null || !entry.Key.Equals(key) || entry.Hash != hash)
                {
                    Metrics.PbtStoreCacheMisses.Increment(_metricsLabel);
                    return null;
                }

                if (_evictionPolicy is EvictionPolicy.Clock) entry.Referenced = true;
                entry.Memory.AcquireLease();
                Metrics.PbtStoreCacheHits.Increment(_metricsLabel);
                return entry.Memory;
            }
        }

        public void Set(TKey key, int shardIdx, in ValueHash256 hash, RefCountingMemory memory)
        {
            int size = memory.GetSpan().Length;
            long capacity = _shardMemoryCapacities[shardIdx];
            if (capacity == 0 || size > capacity || Volatile.Read(ref _isDisposed) != 0) return;

            memory.AcquireLease();
            lock (_shardLocks[shardIdx])
            {
                if (Volatile.Read(ref _isDisposed) != 0)
                {
                    ((IDisposable)memory).Dispose();
                    return;
                }

                Entry?[] shard = _cacheShards[shardIdx];
                int bucketIdx = (key.GetHashCode() & int.MaxValue) & (shard.Length - 1);
                Remove(shardIdx, bucketIdx);

                if (_shardMemoryUsages[shardIdx] > capacity - size)
                {
                    if (_evictionPolicy is EvictionPolicy.Clock) EvictWithClock(shardIdx, capacity - size);
                    else ClearShard(shardIdx);
                }

                shard[bucketIdx] = new Entry(key, hash, memory, size);
                _shardMemoryUsages[shardIdx] += size;
                Metrics.PbtStoreCacheMemory.AddBy(_metricsLabel, size);
            }
        }

        private void EvictWithClock(int shardIdx, long targetMemoryUsage)
        {
            Entry?[] shard = _cacheShards[shardIdx];
            int hand = _clockHands[shardIdx];
            while (_shardMemoryUsages[shardIdx] > targetMemoryUsage)
            {
                Entry? entry = shard[hand];
                if (entry is not null)
                {
                    if (entry.Referenced) entry.Referenced = false;
                    else Remove(shardIdx, hand);
                }

                hand = (hand + 1) & (shard.Length - 1);
            }

            _clockHands[shardIdx] = hand;
        }

        private void Remove(int shardIdx, int bucketIdx)
        {
            Entry? entry = _cacheShards[shardIdx][bucketIdx];
            if (entry is null) return;

            _cacheShards[shardIdx][bucketIdx] = null;
            _shardMemoryUsages[shardIdx] -= entry.Size;
            Metrics.PbtStoreCacheMemory.AddBy(_metricsLabel, -entry.Size);
            ((IDisposable)entry.Memory).Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
            for (int i = 0; i < ShardCount; i++)
            {
                lock (_shardLocks[i]) ClearShard(i);
            }
        }

        private void ClearShard(int shardIdx)
        {
            Entry?[] shard = _cacheShards[shardIdx];
            for (int i = 0; i < shard.Length; i++) ((IDisposable?)shard[i]?.Memory)?.Dispose();
            Array.Clear(shard);

            long freedMemory = _shardMemoryUsages[shardIdx];
            _shardMemoryUsages[shardIdx] = 0;
            _clockHands[shardIdx] = 0;
            Metrics.PbtStoreCacheMemory.AddBy(_metricsLabel, -freedMemory);
        }

        private sealed class Entry(TKey key, ValueHash256 hash, RefCountingMemory memory, int size)
        {
            public TKey Key { get; } = key;
            public ValueHash256 Hash { get; } = hash;
            public RefCountingMemory Memory { get; } = memory;
            public int Size { get; } = size;
            public bool Referenced { get; set; } = true;
        }
    }

    private enum EvictionPolicy
    {
        Clock,
        ClearShard,
    }
}
