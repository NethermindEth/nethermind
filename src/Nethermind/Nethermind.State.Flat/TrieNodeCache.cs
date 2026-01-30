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
/// A specialized <see cref="RefCounterTrieNodeRlp"/> cache. It uses a sharded array as the cache with the
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
    private readonly RefCounterTrieNodeRlp?[][] _cacheShards;
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

        _cacheShards = new RefCounterTrieNodeRlp[ShardCount][];
        for (int i = 0; i < ShardCount; i++)
        {
            _cacheShards[i] = new RefCounterTrieNodeRlp[_bucketSize];
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

    /// <inheritdoc />
    public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        if (TryGet(address, in path, hash, out RefCounterTrieNodeRlp? rlp))
        {
            try
            {
                node = new TrieNode(NodeType.Unknown, hash, rlp.ToArray());
                return true;
            }
            finally
            {
                rlp.Dispose();
            }
        }

        node = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out RefCounterTrieNodeRlp? rlp)
    {
        (int shardIdx, int hashCode) = GetShardAndHashCode(address, in path);
        int bucketIdx = hashCode & _bucketMask;

        RefCounterTrieNodeRlp? maybeRlp = _cacheShards[shardIdx][bucketIdx];
        if (maybeRlp is not null && maybeRlp.TryAcquireLease())
        {
            // We have a lease, now verify the hash
            // For non-inline nodes (length >= 32), verify hash using Keccak.Compute
            if (maybeRlp.Length >= 32)
            {
                Hash256 computedHash = Keccak.Compute(maybeRlp.Span);
                if (computedHash == hash)
                {
                    rlp = maybeRlp;
                    return true;
                }
            }
            else
            {
                // For inline nodes (length < 32), we cannot verify by hash
                // This shouldn't normally happen in the cache, but we still check
                // We'll treat it as a cache miss since we can't verify
            }

            // Hash mismatch or inline node, release the lease
            maybeRlp.Dispose();
        }

        rlp = null;
        return false;
    }

    public void Add(TransientResource transientResource)
    {
        if (_maxCacheMemoryThreshold == 0)
        {
            // When cache is disabled, the child cache entries are not transferred
            // The child cache will be reset when TransientResource is returned to pool
            return;
        }

        void AddToCacheWithHashCode(int shardIdx, int hashCode, RefCounterTrieNodeRlp childRlp)
        {
            int bucketIdx = hashCode & _bucketMask;

            // Create a new RefCounterTrieNodeRlp for the main cache (child cache owns the original)
            RefCounterTrieNodeRlp newRlp = RefCounterTrieNodeRlp.CreateFromRlp(childRlp.Span);

            Interlocked.Add(ref _shardMemoryUsages[shardIdx], newRlp.MemorySize);

            RefCounterTrieNodeRlp? oldRlp = Interlocked.Exchange(ref _cacheShards[shardIdx][bucketIdx], newRlp);
            if (oldRlp is not null)
            {
                long oldMemory = oldRlp.MemorySize;
                oldRlp.Dispose(); // Release the old entry

                Interlocked.Add(ref _shardMemoryUsages[shardIdx], -oldMemory);
            }
        }

        Parallel.For(0, ShardCount, (i) =>
        {
            (int hashCode, RefCounterTrieNodeRlp? rlp)[] shard = transientResource.Nodes.Shards[i];
            for (int j = 0; j < shard.Length; j++)
            {
                if (shard[j].rlp is { } childRlp) AddToCacheWithHashCode(i, shard[j].hashCode, childRlp);
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

            // Dispose all entries in the shard
            for (int i = 0; i < _bucketSize; i++)
            {
                RefCounterTrieNodeRlp? entry = Interlocked.Exchange(ref _cacheShards[shardToClear][i], null);
                entry?.Dispose();
            }

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
                RefCounterTrieNodeRlp? entry = Interlocked.Exchange(ref _cacheShards[i][j], null);
                entry?.Dispose();
            }
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
        private readonly (int hashCode, RefCounterTrieNodeRlp? rlp)[][] _shards;
        private int _count = 0;
        private int _mask;
        private int _shardSize;

        public int Count => _count;
        public int Capacity => _shards.Length * _shardSize;
        public (int hashCode, RefCounterTrieNodeRlp? rlp)[][] Shards => _shards;

        public ChildCache(int size)
        {
            int powerOfTwoSize = (int)BitOperations.RoundUpToPowerOf2((uint)(size + ShardCount - 1) / ShardCount);
            _shards = new (int, RefCounterTrieNodeRlp?)[ShardCount][];
            _mask = powerOfTwoSize - 1;
            _shardSize = powerOfTwoSize;
            CreateCacheArray(_shardSize);
        }

        private void CreateCacheArray(int size)
        {
            for (int i = 0; i < ShardCount; i++) _shards[i] = new (int, RefCounterTrieNodeRlp?)[size];
        }

        public void Reset()
        {
            // Dispose all entries before clearing
            for (int i = 0; i < ShardCount; i++)
            {
                (int hashCode, RefCounterTrieNodeRlp? rlp)[] shard = _shards[i];
                for (int j = 0; j < shard.Length; j++)
                {
                    shard[j].rlp?.Dispose();
                }
            }

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

        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out RefCounterTrieNodeRlp? rlp)
        {
            (int shardIdx, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;
            (int hashCode, RefCounterTrieNodeRlp? rlp) entry = _shards[shardIdx][idx]; // Copy struct once

            if (entry.hashCode != hashCode)
            {
                rlp = null;
                return false;
            }

            RefCounterTrieNodeRlp? maybeRlp = entry.rlp; // Store it to prevent concurrency issue
            if (maybeRlp is null || !maybeRlp.TryAcquireLease())
            {
                rlp = null;
                return false;
            }

            // Verify hash for non-inline nodes (length >= 32)
            if (maybeRlp.Length >= 32)
            {
                Hash256 computedHash = Keccak.Compute(maybeRlp.Span);
                if (computedHash != hash)
                {
                    maybeRlp.Dispose(); // Release the lease
                    rlp = null;
                    return false;
                }
            }
            else
            {
                // For inline nodes (length < 32), cannot verify by hash - treat as cache miss
                maybeRlp.Dispose();
                rlp = null;
                return false;
            }

            rlp = maybeRlp;
            return true;
        }

        public void Set(Hash256? address, in TreePath path, RefCounterTrieNodeRlp rlp)
        {
            (int shard, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            _count++; // Track count

            ref (int hashCode, RefCounterTrieNodeRlp? rlp) entry = ref _shards[shard][idx];

            // Dispose old entry when replacing
            entry.rlp?.Dispose();

            entry = (hashCode, rlp);
        }

        public RefCounterTrieNodeRlp GetOrAdd(Hash256? address, in TreePath path, RefCounterTrieNodeRlp rlp)
        {
            (int shard, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            ref (int hashCode, RefCounterTrieNodeRlp? rlp) entry = ref _shards[shard][idx];
            RefCounterTrieNodeRlp? maybeRlp = entry.rlp; // Store it to prevent concurrency issue
            if (maybeRlp is not null && maybeRlp.TryAcquireLease())
            {
                // We got a lease - verify it's the same RLP by hash
                if (maybeRlp.Length >= 32 && rlp.Length >= 32)
                {
                    Hash256 existingHash = Keccak.Compute(maybeRlp.Span);
                    Hash256 newHash = Keccak.Compute(rlp.Span);
                    if (existingHash == newHash)
                    {
                        // Same RLP, dispose the new one and return existing
                        rlp.Dispose();
                        return maybeRlp;
                    }
                }
                // Hash mismatch or inline node - release the lease and replace
                maybeRlp.Dispose();
            }

            if (entry.rlp is null)
            {
                _count++; // Track count for new entries
            }
            else
            {
                entry.rlp.Dispose(); // Dispose old entry being replaced
            }

            entry = (hashCode, rlp);
            return rlp;
        }
    }
}
