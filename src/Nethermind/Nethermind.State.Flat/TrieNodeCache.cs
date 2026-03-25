// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A specialized <see cref="RefCountingTrieNode"/> cache. Uses sharded arrays with one
/// <see cref="RefCountingTrieNodePool"/> per shard for memory tracking. Collision = last-write-wins.
/// When memory budget is exceeded, whole shards are cleared round-robin.
/// </summary>
public sealed class TrieNodeCache : ITrieNodeCache
{
    // RefCountingTrieNode object: ~128 (PaddedValue) + 32 (Hash) + 35 (Metadata) + 546 (Rlp) + 8 (pool ref) + 16 (header) ≈ 765
    private const int EstimatedSizePerNode = 800;
    private const double UtilRatio = 0.25;
    private const int ShardCount = 256;

    private readonly ILogger _logger;
    private readonly RefCountingTrieNode?[][] _cacheShards;
    private readonly RefCountingTrieNodePool[] _shardPools;
    private readonly long[] _shardMemoryUsages;
    private readonly long _maxCacheMemoryThreshold;
    private readonly int _bucketSize;
    private readonly int _bucketMask;

    private int _nextShardToClear = 0;

    public TrieNodeCache(IFlatDbConfig flatDbConfig, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<TrieNodeCache>();

        long maxCacheMemoryThreshold = flatDbConfig.TrieCacheMemoryBudget;
        long totalNodeCount = maxCacheMemoryThreshold / EstimatedSizePerNode;

        int targetBucketSize = (int)((totalNodeCount / UtilRatio) / ShardCount);
        _bucketSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, targetBucketSize));
        _bucketMask = _bucketSize - 1;

        _cacheShards = new RefCountingTrieNode?[ShardCount][];
        _shardPools = new RefCountingTrieNodePool[ShardCount];
        for (int i = 0; i < ShardCount; i++)
        {
            _cacheShards[i] = new RefCountingTrieNode?[_bucketSize];
            _shardPools[i] = new RefCountingTrieNodePool(_bucketSize);
        }

        _shardMemoryUsages = new long[ShardCount];
        _maxCacheMemoryThreshold = maxCacheMemoryThreshold;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int, int) GetShardAndHashCode(Hash256? address, in TreePath path)
    {
        int shardIdx = path.Path.Bytes[0];
        int h1;
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
        int hashCode = (h1 ^ h2) & int.MaxValue;

        return (shardIdx, hashCode);
    }

    public RefCountingTrieNode? TryGet(Hash256? address, in TreePath path, Hash256 hash)
    {
        (int shardIdx, int hashCode) = GetShardAndHashCode(address, in path);
        int bucketIdx = hashCode & _bucketMask;

        RefCountingTrieNode? maybeNode = Volatile.Read(ref _cacheShards[shardIdx][bucketIdx]);
        if (maybeNode is not null && maybeNode.Hash == hash && maybeNode.TryAcquireLease())
        {
            return maybeNode;
        }

        return null;
    }

    public void Add(TransientResource transientResource)
    {
        if (_maxCacheMemoryThreshold == 0)
        {
            // Dispose all nodes in the child cache without transferring
            for (int i = 0; i < ShardCount; i++)
            {
                RefCountingTrieNode?[] shard = transientResource.Nodes.Shards[i];
                for (int j = 0; j < shard.Length; j++)
                {
                    shard[j]?.Dispose();
                    shard[j] = null;
                }
            }
            return;
        }

        Parallel.For(0, ShardCount, (i) =>
        {
            RefCountingTrieNode?[] childShard = transientResource.Nodes.Shards[i];
            for (int j = 0; j < childShard.Length; j++)
            {
                RefCountingTrieNode? childNode = childShard[j];
                if (childNode is null) continue;

                // Transfer from child cache to main cache: create a new node in the shard pool
                // and release the child node's lease.
                int hashCode = transientResource.Nodes.HashCodes[i][j];
                int bucketIdx = hashCode & _bucketMask;

                RefCountingTrieNode newNode = _shardPools[i].Rent(childNode.Hash, childNode.Rlp.AsSpan());
                Interlocked.Add(ref _shardMemoryUsages[i], EstimatedSizePerNode);

                RefCountingTrieNode? oldNode = Interlocked.Exchange(ref _cacheShards[i][bucketIdx], newNode);
                if (oldNode is not null)
                {
                    Interlocked.Add(ref _shardMemoryUsages[i], -EstimatedSizePerNode);
                    oldNode.Dispose();
                }

                childNode.Dispose();
                childShard[j] = null;
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

            for (int i = 0; i < _bucketSize; i++)
            {
                RefCountingTrieNode? node = Interlocked.Exchange(ref _cacheShards[shardToClear][i], null);
                node?.Dispose();
            }

            long freedMemory = Interlocked.Exchange(ref _shardMemoryUsages[shardToClear], 0);
            currentTotalMemory -= freedMemory;

            _nextShardToClear = (_nextShardToClear + 1) & 255;
        }

        if (wasPruned && _logger.IsTrace) _logger.Trace($"Pruning trie cache from {prevMemory} to {currentTotalMemory}");

        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = currentTotalMemory;
    }

    /// <summary>Clears all cached trie nodes.</summary>
    public void Clear()
    {
        for (int i = 0; i < ShardCount; i++)
        {
            for (int j = 0; j < _bucketSize; j++)
            {
                RefCountingTrieNode? node = Interlocked.Exchange(ref _cacheShards[i][j], null);
                node?.Dispose();
            }
            Interlocked.Exchange(ref _shardMemoryUsages[i], 0);
        }
        _nextShardToClear = 0;
        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = 0;
    }

    /// <summary>
    /// Small cache for use in <see cref="TransientResource"/>. Sharded with the same mechanics so that
    /// adding to trie node cache can be done in parallel.
    /// </summary>
    public class ChildCache
    {
        private RefCountingTrieNode?[][] _shards;
        private int[][] _hashCodes;
        private int _count = 0;
        private int _mask;
        private int _shardSize;

        public int Count => _count;
        public int Capacity => ShardCount * _shardSize;
        public RefCountingTrieNode?[][] Shards => _shards;
        public int[][] HashCodes => _hashCodes;

        public ChildCache(int size)
        {
            int powerOfTwoSize = (int)BitOperations.RoundUpToPowerOf2((uint)(size + ShardCount - 1) / ShardCount);
            _mask = powerOfTwoSize - 1;
            _shardSize = powerOfTwoSize;
            _shards = new RefCountingTrieNode?[ShardCount][];
            _hashCodes = new int[ShardCount][];
            CreateCacheArray(_shardSize);
        }

        private void CreateCacheArray(int size)
        {
            for (int i = 0; i < ShardCount; i++)
            {
                _shards[i] = new RefCountingTrieNode?[size];
                _hashCodes[i] = new int[size];
            }
        }

        public void Reset()
        {
            if (_count / UtilRatio > ShardCount * _shardSize)
            {
                int newTarget = (int)(_count / UtilRatio);
                int powerOfTwoSize = (int)BitOperations.RoundUpToPowerOf2((uint)(newTarget + ShardCount - 1) / ShardCount);
                _shardSize = powerOfTwoSize;
                _mask = powerOfTwoSize - 1;
                CreateCacheArray(_shardSize);
            }
            else
            {
                for (int i = 0; i < ShardCount; i++)
                {
                    // Dispose any remaining nodes before clearing
                    RefCountingTrieNode?[] shard = _shards[i];
                    for (int j = 0; j < shard.Length; j++)
                    {
                        shard[j]?.Dispose();
                        shard[j] = null;
                    }
                    Array.Clear(_hashCodes[i]);
                }
            }

            _count = 0;
        }

        public RefCountingTrieNode? TryGet(Hash256? address, in TreePath path, Hash256 hash)
        {
            (int shardIdx, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            RefCountingTrieNode? maybeNode = _shards[shardIdx][idx];
            if (maybeNode is not null && _hashCodes[shardIdx][idx] == hashCode && maybeNode.Hash == hash)
            {
                return maybeNode;
            }

            return null;
        }

        public void Set(Hash256? address, in TreePath path, Hash256 hash, ReadOnlySpan<byte> rlp, RefCountingTrieNodePool pool)
        {
            (int shard, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            RefCountingTrieNode? oldNode = _shards[shard][idx];
            oldNode?.Dispose();

            RefCountingTrieNode newNode = pool.Rent(hash, rlp);
            _shards[shard][idx] = newNode;
            _hashCodes[shard][idx] = hashCode;

            _count++;
        }
    }
}
