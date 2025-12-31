// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;

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
    private readonly int _shardCount = 256;

    private readonly ILogger _logger;

    public TrieNodeCache(long maxCacheMemoryThreshold, ILogManager logManager)
    {
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

    // [Optimization] Inline and remove HashCode.Combine overhead
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
            foreach (var kv in cachedResource.Nodes) kv.node.PrunePersistedRecursively(1);
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

        void AddToCache(Hash256? address, in TreePath path, TrieNode newNode)
        {
            (int shardIdx, int hashCode) = GetShardAndHashCode(address, path);
            AddToCacheWithHashCode(shardIdx, hashCode, newNode);
        }

        HashSet<(int, int)> addedEntries = new HashSet<(int, int)>();

        foreach (var kv in snapshot.StateNodes)
        {
            kv.Value.PrunePersistedRecursively(1);
            AddToCache(null, kv.Key, kv.Value);
            addedEntries.Add(GetShardAndHashCode(null, kv.Key));
        }

        foreach (var kv in snapshot.StorageNodes)
        {
            kv.Value.PrunePersistedRecursively(1);
            AddToCache(kv.Key.Item1, kv.Key.Item2, kv.Value);
            addedEntries.Add(GetShardAndHashCode(kv.Key.Item1, kv.Key.Item2));
        }

        // [Optimization] Use custom enumerator to avoid allocation
        foreach (var item in cachedResource.Nodes)
        {
            item.node.PrunePersistedRecursively(1);
            if (addedEntries.Contains((item.shardIdx, item.hashCode))) continue;
            AddToCacheWithHashCode(item.shardIdx, item.hashCode, item.node);
        }

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

        if (wasPruned)
        {
            _logger.Info($"Pruning trie cache from {prevMemory} to {currentTotalMemory}");
        }

        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = currentTotalMemory;
    }

    public class ChildCache
    {
        private (int bucketIdx, int hashCode, TrieNode node)[] _cacheArray;
        private int _count = 0; // [Optimization] Track count explicitly O(1)
        private int _mask;

        public int Count => _count;

        public ChildCache(int size)
        {
            int powerOfTwoSize = (int)BitOperations.RoundUpToPowerOf2((uint)size);
            _cacheArray = new (int, int, TrieNode)[powerOfTwoSize];
            _mask = powerOfTwoSize - 1;
        }

        public void Reset()
        {
            // [Optimization] No need to Enumerate().Count() here (which was O(N)). Use _count (O(1)).
            if (_count / UtilRatio > _cacheArray.Length)
            {
                int newSize = (int)BitOperations.RoundUpToPowerOf2((uint)(_count / UtilRatio));
                Console.Error.WriteLine($"Resize from {_cacheArray.Length} to {newSize}");
                _cacheArray = new (int, int, TrieNode)[newSize];
                _mask = newSize - 1;
            }
            else
            {
                Array.Clear(_cacheArray, 0, _cacheArray.Length);
            }

            _count = 0;
        }

        // [Optimization] Custom Enumerator to avoid IEnumerable allocation
        public Enumerator GetEnumerator() => new Enumerator(_cacheArray);

        public ref struct Enumerator
        {
            private readonly (int, int, TrieNode)[] _array;
            private int _index;

            public Enumerator((int, int, TrieNode)[] array)
            {
                _array = array;
                _index = -1;
            }

            public bool MoveNext()
            {
                while (++_index < _array.Length)
                {
                    if (_array[_index].Item3 != null) return true;
                }
                return false;
            }

            public (int shardIdx, int hashCode, TrieNode node) Current => _array[_index];
        }

        private int GetBucketIdx(Hash256? address, TreePath path)
        {
            (int shard, int hashCode) = TrieNodeCache.GetShardAndHashCode(address, path);
            return hashCode & _mask; // [Optimization] Bitwise AND
        }

        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            int idx = GetBucketIdx(address, path);
            var entry = _cacheArray[idx]; // Copy struct once

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
            (int shard, int hashCode) = TrieNodeCache.GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            if (_cacheArray[idx].node == null) _count++; // Track count

            _cacheArray[idx] = (shard, hashCode, node);
        }

        public TrieNode GetOrAdd(Hash256? address, in TreePath path, TrieNode trieNode)
        {
            (int shard, int hashCode) = TrieNodeCache.GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            ref var entry = ref _cacheArray[idx];
            if (entry.node != null && entry.node.Keccak == trieNode.Keccak) return entry.node;

            if (entry.node == null) _count++; // Track count

            entry = (shard, hashCode, trieNode);
            return trieNode;
        }
    }
}
