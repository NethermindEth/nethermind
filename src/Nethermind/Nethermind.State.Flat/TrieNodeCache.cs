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
    private const int EstimatedSizePerNode = RefCountingTrieNode.EstimatedSize;
    private const double UtilRatio = 0.25;
    private const int ShardCount = 256;

    private readonly ILogger _logger;
    private readonly RefCountingTrieNode?[][] _cacheShards;
    private readonly RefCountingTrieNodePool _pool;
    private readonly RefCountingRlpNodePoolTracker[] _shardTrackers;
    private long _maxCacheMemoryThreshold;
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

        _pool = new RefCountingTrieNodePool((int)(totalNodeCount * 0.05));
        _cacheShards = new RefCountingTrieNode?[ShardCount][];
        _shardTrackers = new RefCountingRlpNodePoolTracker[ShardCount];
        for (int i = 0; i < ShardCount; i++)
        {
            _cacheShards[i] = new RefCountingTrieNode?[_bucketSize];
            _shardTrackers[i] = new RefCountingRlpNodePoolTracker(_pool);
        }

        _maxCacheMemoryThreshold = maxCacheMemoryThreshold;
    }

    private long GetTotalActiveCount()
    {
        long total = 0;
        for (int i = 0; i < ShardCount; i++) total += _shardTrackers[i].ActiveCount;
        return total;
    }

    private long GetTotalActiveMemory()
    {
        long total = 0;
        for (int i = 0; i < ShardCount; i++) total += _shardTrackers[i].ActiveMemory;
        return total;
    }

    /// <summary>Per-shard trackers. Exposed so that <see cref="ChildCache"/> can use the same trackers.</summary>
    public RefCountingRlpNodePoolTracker[] ShardTrackers => _shardTrackers;

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

    public RefCountingTrieNode? TryGet(Hash256? address, in TreePath path, in ValueHash256 hash)
    {
        (int shardIdx, int hashCode) = GetShardAndHashCode(address, in path);
        int bucketIdx = hashCode & _bucketMask;

        RefCountingTrieNode? maybeNode = Volatile.Read(ref _cacheShards[shardIdx][bucketIdx]);
        if (maybeNode is not null && maybeNode.TryAcquireLease())
        {
            if (maybeNode.Hash == hash) return maybeNode;
            maybeNode.Dispose();
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
                    Interlocked.Exchange(ref shard[j], null)?.Dispose();
            }
            return;
        }

        Parallel.For(0, ShardCount, (i) =>
        {
            RefCountingTrieNode?[] childShard = transientResource.Nodes.Shards[i];
            for (int j = 0; j < childShard.Length; j++)
            {
                // Atomically take the child node — no re-rent needed since child and main share the same shard pools.
                RefCountingTrieNode? childNode = Interlocked.Exchange(ref childShard[j], null);
                if (childNode is null) continue;

                int hashCode = transientResource.Nodes.HashCodes[i][j];
                int bucketIdx = hashCode & _bucketMask;

                RefCountingTrieNode? oldNode = Interlocked.Exchange(ref _cacheShards[i][bucketIdx], childNode);
                oldNode?.Dispose();
            }
        });

        long currentTotalMemory = GetTotalActiveMemory();
        long prevMemory = currentTotalMemory;
        bool wasPruned = false;

        int pruneAttempts = 0;
        int totalEvicted = 0;
        int totalRetained = 0;
        while (currentTotalMemory > _maxCacheMemoryThreshold && pruneAttempts < ShardCount)
        {
            pruneAttempts++;
            wasPruned = true;
            int shardToClear = _nextShardToClear;

            for (int i = 0; i < _bucketSize; i++)
            {
                RefCountingTrieNode? node = Interlocked.Exchange(ref _cacheShards[shardToClear][i], null);
                if (node is not null)
                {
                    if (node.LeaseCount <= 1) totalEvicted++;
                    else totalRetained++;
                    node.Dispose();
                }
            }

            currentTotalMemory = GetTotalActiveMemory();
            _nextShardToClear = (_nextShardToClear + 1) & 255;
        }

        if (wasPruned)
        {
            int total = totalEvicted + totalRetained;
            double retainedPct = total > 0 ? 100.0 * totalRetained / total : 0;
            _logger.Info($"Pruning trie cache: {prevMemory} -> {currentTotalMemory}, shards cleared: {pruneAttempts}, evicted: {totalEvicted}, retained (leased): {totalRetained} ({retainedPct:F1}%)");

            if (pruneAttempts > 16)
            {
                long increase = 1L * 1024 * 1024 * 1024; // 1 GB
                _maxCacheMemoryThreshold += increase;
                _logger.Warn($"Trie cache pruned {pruneAttempts} shards in one go — increasing memory budget by 1 GB to {_maxCacheMemoryThreshold}");
            }
        }

        UpdateMetrics();
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
        }
        _nextShardToClear = 0;
        UpdateMetrics();
    }

    private void UpdateMetrics()
    {
        long activeCount = GetTotalActiveCount();
        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = GetTotalActiveMemory();
        Nethermind.Trie.Pruning.Metrics.ActivePooledNodeCount = activeCount;

        long branchCount = 0, extensionCount = 0, leafCount = 0;
        for (int i = 0; i < ShardCount; i++)
        {
            branchCount += _shardTrackers[i].ActiveBranchCount;
            extensionCount += _shardTrackers[i].ActiveExtensionCount;
            leafCount += _shardTrackers[i].ActiveLeafCount;
        }
        Nethermind.Trie.Pruning.Metrics.ActivePooledNodeCountByType["branch"] = branchCount;
        Nethermind.Trie.Pruning.Metrics.ActivePooledNodeCountByType["extension"] = extensionCount;
        Nethermind.Trie.Pruning.Metrics.ActivePooledNodeCountByType["leaf"] = leafCount;
    }

    /// <summary>
    /// Small cache for use in <see cref="TransientResource"/>. Sharded with the same mechanics so that
    /// adding to trie node cache can be done in parallel.
    /// </summary>
    public class ChildCache
    {
        private static readonly RefCountingTrieNodePool s_defaultPool = new(256);

        private RefCountingTrieNode?[][] _shards;
        private int[][] _hashCodes;
        private RefCountingRlpNodePoolTracker[] _shardTrackers;
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
            // Default trackers until SetShardTrackers is called
            _shardTrackers = new RefCountingRlpNodePoolTracker[ShardCount];
            for (int i = 0; i < ShardCount; i++) _shardTrackers[i] = new RefCountingRlpNodePoolTracker(s_defaultPool);
            CreateCacheArray(_shardSize);
        }

        /// <summary>Binds this child cache to the shard trackers from <see cref="TrieNodeCache"/>.</summary>
        public void SetShardTrackers(RefCountingRlpNodePoolTracker[]? shardTrackers)
        {
            if (shardTrackers is not null) _shardTrackers = shardTrackers;
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

                // Dispose nodes in old shards before replacing with new arrays
                for (int i = 0; i < ShardCount; i++)
                {
                    RefCountingTrieNode?[] shard = _shards[i];
                    for (int j = 0; j < shard.Length; j++)
                        Interlocked.Exchange(ref shard[j], null)?.Dispose();
                }

                CreateCacheArray(_shardSize);
            }
            else
            {
                for (int i = 0; i < ShardCount; i++)
                {
                    RefCountingTrieNode?[] shard = _shards[i];
                    for (int j = 0; j < shard.Length; j++)
                        Interlocked.Exchange(ref shard[j], null)?.Dispose();
                    Array.Clear(_hashCodes[i]);
                }
            }

            _count = 0;
        }

        public RefCountingTrieNode? TryGet(Hash256? address, in TreePath path, in ValueHash256 hash)
        {
            (int shardIdx, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            RefCountingTrieNode? maybeNode = Volatile.Read(ref _shards[shardIdx][idx]);
            if (maybeNode is not null && _hashCodes[shardIdx][idx] == hashCode && maybeNode.TryAcquireLease())
            {
                if (maybeNode.Hash == hash) return maybeNode;
                maybeNode.Dispose();
            }

            return null;
        }

        public RefCountingTrieNode SetAndLease(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp)
        {
            if (rlp.Length > TrieNodeRlp.MaxRlpLength)
                throw new ArgumentException($"RLP too large: {rlp.Length} > {TrieNodeRlp.MaxRlpLength}");

            (int shard, int hashCode) = GetShardAndHashCode(address, path);
            int idx = hashCode & _mask;

            RefCountingTrieNode newNode = _shardTrackers[shard].Rent(hash, rlp);
            newNode.TryAcquireLease(); // Extra lease for caller (1→2): cache owns one, caller owns one
            _hashCodes[shard][idx] = hashCode;
            RefCountingTrieNode? oldNode = Interlocked.Exchange(ref _shards[shard][idx], newNode);
            oldNode?.Dispose();

            Interlocked.Increment(ref _count);
            return newNode;
        }
    }
}
