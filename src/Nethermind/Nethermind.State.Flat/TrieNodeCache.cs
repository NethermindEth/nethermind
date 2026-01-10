// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State.Flat;

public class TrieNodeCache
{
    private const int EstimatedSizePerNode = 700;
    private const double UtilRatio = 0.5;

    // [Optimization] Cache Line Padding to prevent False Sharing between threads updating different shards.
    [StructLayout(LayoutKind.Explicit, Size = 64)] // Align to 64-byte cache line
    private struct PaddedLong
    {
        [FieldOffset(0)] public long Value;
    }

    private TrieNode?[][] _cacheShards;
    private PaddedLong[] _shardMemoryUsages; // Replaces long[] to avoid false sharing

    private int _nextShardToClear = 0;
    private readonly long _maxCacheMemoryThreshold;
    private readonly int _bucketSize;
    private readonly int _bucketMask; // [Optimization] For bitwise modulo
    private const int _shardCount = 256;

    private readonly ILogger _logger;

    public TrieNodeCache(IFlatDbConfig flatDbConfig, ILogManager logManager)
    {
        long maxCacheMemoryThreshold = flatDbConfig.TrieCacheMemoryTarget;
        long totalNodeCount = (maxCacheMemoryThreshold / EstimatedSizePerNode);

        // [Optimization] Round bucket size up to Power of 2 for fast AND masking
        int targetBucketSize = (int)(totalNodeCount / _shardCount / UtilRatio);
        _bucketSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, targetBucketSize));
        _bucketMask = _bucketSize - 1;

        _cacheShards = new TrieNode[_shardCount][];
        for (int i = 0; i < _shardCount; i++)
        {
            _cacheShards[i] = new TrieNode[_bucketSize];
        }

        _shardMemoryUsages = new PaddedLong[_shardCount]; // Padded counters
        _maxCacheMemoryThreshold = maxCacheMemoryThreshold;
        _logger = logManager.GetClassLogger<TrieNodeCache>();
    }

    internal static Histogram _times = DevMetric.Factory.CreateHistogram("trienodecache_times", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "type" },
        Buckets = Histogram.PowersOfTenDividedBuckets(2, 12, 5)
    });

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int, int) GetShardAndHashCode(Hash256? address, in TreePath path)
    {
        int shardIdx;
        int h1;

        if (address is null)
        {
            shardIdx = path.Path.Bytes[0];
            h1 = 0;
        }
        else
        {
            shardIdx = address.Bytes[0];
            h1 = address.GetHashCode();
        }

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
            for (int i = 0; i < _shardCount; i++)
            {
                var shard = cachedResource.Nodes.Shards[i];
                for (int j = 0; j < shard.Length; j++)
                {
                    if (shard[j].node is not null) shard[j].node.PrunePersistedRecursively(1);

                }
            }
            foreach (var kv in snapshot.StateNodes) kv.Value.PrunePersistedRecursively(1);
            foreach (var kv in snapshot.StorageNodes) kv.Value.PrunePersistedRecursively(1);
            return;
        }

        void AddToCacheWithHashCode(int shardIdx, int hashCode, TrieNode newNode)
        {
            // [Optimization] Bitwise AND
            int bucketIdx = hashCode & _bucketMask;

            long memory = newNode.GetMemorySize(false);

            // [Optimization] Atomic Add on Padded Counter (Fixes Race Condition + False Sharing)
            Interlocked.Add(ref _shardMemoryUsages[shardIdx].Value, memory);

            // [Optimization] Removed global _estimatedMemoryUsage Interlocked update (bottleneck).
            // We verify total memory only when needed (lazy calculation).

            TrieNode? oldNode = Interlocked.Exchange(ref _cacheShards[shardIdx][bucketIdx], newNode);
            if (oldNode is not null)
            {
                long oldMemory = oldNode.GetMemorySize(false);
                oldNode.PrunePersistedRecursively(1);

                // Subtract old memory
                Interlocked.Add(ref _shardMemoryUsages[shardIdx].Value, -oldMemory);
            }
        }

        long sw = Stopwatch.GetTimestamp();

        _times.WithLabels("statenodes").Observe(Stopwatch.GetTimestamp() - sw);
        sw = Stopwatch.GetTimestamp();

        Parallel.For(0, _shardCount, (i) =>
        {
            var shard = cachedResource.Nodes.Shards[i];
            for (int j = 0; j < shard.Length; j++)
            {
                if (shard[j].node is not null)
                {
                    shard[j].node.PrunePersistedRecursively(1);
                    AddToCacheWithHashCode(i, shard[j].hashCode, shard[j].node);
                }
            }
        });

        _times.WithLabels("cachednodes").Observe(Stopwatch.GetTimestamp() - sw);
        sw = Stopwatch.GetTimestamp();

        // Calculate total memory only once here, instead of atomically updating it 1000s of times
        long CalculateTotalMemory()
        {
            long total = 0;
            for(int i=0; i < _shardCount; i++) total += _shardMemoryUsages[i].Value;
            return total;
        }

        long currentTotalMemory = CalculateTotalMemory();
        long prevMemory = currentTotalMemory;
        bool wasPruned = false;

        while (snapshot.To.blockNumber % 16 == 0 && currentTotalMemory > _maxCacheMemoryThreshold)
        {
            wasPruned = true;
            int shardToClear = _nextShardToClear;

            // Clear the shard
            Array.Clear(_cacheShards[shardToClear]);

            // Reset shard memory
            long freedMemory = Interlocked.Exchange(ref _shardMemoryUsages[shardToClear].Value, 0);
            currentTotalMemory -= freedMemory;

            _nextShardToClear = (_nextShardToClear + 1) & 255; // Fast modulo 256
        }

        sw = Stopwatch.GetTimestamp();

        if (wasPruned)
        {
            _times.WithLabels("prune").Observe(Stopwatch.GetTimestamp() - sw);
            _logger.Info($"Pruning trie cache from {prevMemory} to {currentTotalMemory}");
        }

        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = currentTotalMemory;
    }

    public class ChildCache
    {
        private (int hashCode, TrieNode node)[][] _shards;
        private int _count = 0; // [Optimization] Track count explicitly O(1)
        private int _mask;
        private int _shardSize;

        public int Count => _count;
        public int Capacity => _shards.Length * _shardSize;
        public (int hashCode, TrieNode node)[][] Shards => _shards;

        public ChildCache(int size)
        {
            int powerOfTwoSize = (int)BitOperations.RoundUpToPowerOf2((uint)(size + _shardCount - 1) / _shardCount);
            _shards = new (int, TrieNode)[_shardCount][];
            _mask = powerOfTwoSize - 1;
            _shardSize = powerOfTwoSize;
            CreateCacheArray(_shardSize);
        }

        private void CreateCacheArray(int size)
        {
            for (int i = 0; i < _shardCount; i++)
            {
                _shards[i] = new (int, TrieNode)[size];
            }
        }

        public void Reset()
        {
            // [Optimization] No need to Enumerate().Count() here (which was O(N)). Use _count (O(1)).
            if (_count / UtilRatio > (_shards.Length * _shardSize))
            {
                int newTarget = (int)(_count / UtilRatio);
                int powerOfTwoSize = (int)BitOperations.RoundUpToPowerOf2((uint)(newTarget + _shardCount - 1) / _shardCount);
                Console.Error.WriteLine($"Resize from {_shardSize} to {powerOfTwoSize}");
                _shardSize = powerOfTwoSize;
                CreateCacheArray(_shardSize);
                _mask = powerOfTwoSize - 1;
            }
            else
            {
                for (int i = 0; i < _shardCount; i++)
                {
                    Array.Clear(_shards[i], 0, _shards[i].Length);
                }
            }

            _count = 0;
        }

        private (int, int) GetBucketIdx(Hash256? address, TreePath path)
        {
            (int shard, int hashCode) = GetShardAndHashCode(address, path);
            return (shard, hashCode & _mask); // [Optimization] Bitwise AND
        }

        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            (int shardIdx, int idx) = GetBucketIdx(address, path);
            var entry = _shards[shardIdx][idx]; // Copy struct once

            if (entry.node == null)
            {
                node = null;
                return false;
            }
            if (entry.node.Keccak == hash)
            {
                node = entry.node;
                return true;
            }

            node = null;
            return false;
        }

        public void Set(Hash256? address, in TreePath path, TrieNode node)
        {
            (int shard, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            if (_shards[shard][idx].node == null) _count++; // Track count

            _shards[shard][idx] = (hashCode, node);
        }

        public TrieNode GetOrAdd(Hash256? address, in TreePath path, TrieNode trieNode)
        {
            (int shard, int hashCode) = TrieNodeCache.GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            ref var entry = ref _shards[shard][idx];
            if (entry.node != null && entry.node.Keccak == trieNode.Keccak) return entry.node;
            if (entry.node == null) _count++; // Track count

            entry = (hashCode, trieNode);
            return trieNode;
        }

        public void ClearCachedNode(int shard, int hashCode)
        {
            int idx = hashCode & _mask;
            if (_shards[shard][idx].node is not null)
            {
                _shards[shard][idx].node.PrunePersistedRecursively(1);
                _shards[shard][idx] = default;
            }
        }
    }
}
