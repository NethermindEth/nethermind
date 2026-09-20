// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

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
    // One byte per bucket, set when the node is read and cleared by an eviction sweep. Null unless hit-aware
    // eviction is enabled, so the default path keeps the plain round-robin shard clear.
    private readonly byte[][]? _shardAccessFlags;
    private readonly long[] _shardMemoryUsages;
    private readonly long _maxCacheMemoryThreshold;
    private readonly int _bucketSize;
    private readonly int _bucketMask;

    private int _nextShardToClear = 0;

    public TrieNodeCache(IFlatDbConfig flatDbConfig, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<TrieNodeCache>();

        long maxCacheMemoryThreshold = (long)flatDbConfig.TrieCacheMemoryBudget;
        long totalNodeCount = (maxCacheMemoryThreshold / EstimatedSizePerNode);

        int targetBucketSize = (int)((totalNodeCount / UtilRatio) / ShardCount);
        _bucketSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, targetBucketSize));
        _bucketMask = _bucketSize - 1;

        _cacheShards = new TrieNode[ShardCount][];
        for (int i = 0; i < ShardCount; i++)
        {
            _cacheShards[i] = new TrieNode[_bucketSize];
        }

        if (flatDbConfig.TrieCacheHitAwareEviction)
        {
            _shardAccessFlags = new byte[ShardCount][];
            for (int i = 0; i < ShardCount; i++)
            {
                _shardAccessFlags[i] = new byte[_bucketSize];
            }
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

    public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        (int shardIdx, int hashCode) = GetShardAndHashCode(address, in path);
        int bucketIdx = hashCode & _bucketMask;

        TrieNode? maybeNode = _cacheShards[shardIdx][bucketIdx];
        if (maybeNode is not null && maybeNode.Keccak == hash)
        {
            // Give the node another round in the next eviction sweep. Skipping the store when the flag is already
            // set keeps repeated reads of a hot node off the write path.
            byte[][]? accessFlags = _shardAccessFlags;
            if (accessFlags is not null && accessFlags[shardIdx][bucketIdx] == 0) accessFlags[shardIdx][bucketIdx] = 1;

            node = maybeNode;
            return true;
        }

        node = null;
        return false;
    }

    public void Add(TransientResource transientResource)
    {
        transientResource.WaitForExclusiveLease();

        if (_maxCacheMemoryThreshold == 0)
        {
            for (int i = 0; i < ShardCount; i++)
            {
                (int hashCode, TrieNode? node)[] shard = transientResource.Nodes.Shards[i];
                for (int j = 0; j < shard.Length; j++)
                {
                    if (shard[j].node is { } newNode && !newNode.IsWarmerOwned) newNode.PrunePersistedRecursively(1);

                }
            }
            return;
        }

        void AddToCacheWithHashCode(int shardIdx, int hashCode, TrieNode newNode)
        {
            int bucketIdx = hashCode & _bucketMask;
            newNode.PrunePersistedRecursively(1);
            Interlocked.Add(ref _shardMemoryUsages[shardIdx], newNode.GetMemorySize(false));

            // A new occupant starts cold, whatever the previous one's reads were.
            if (_shardAccessFlags is not null) _shardAccessFlags[shardIdx][bucketIdx] = 0;

            TrieNode? oldNode = Interlocked.Exchange(ref _cacheShards[shardIdx][bucketIdx], newNode);
            if (oldNode is not null)
            {
                long oldMemory = oldNode.GetMemorySize(false);
                oldNode.PrunePersistedRecursively(1);

                Interlocked.Add(ref _shardMemoryUsages[shardIdx], -oldMemory);
            }
        }

        static TrieNode? TryMaterializeResolvedWarmerNode(TrieNode source)
        {
            if (!source.IsWarmerResolved) return null;

            CappedArray<byte> fullRlp = source.FullRlp;
            if (fullRlp.IsNull) return null;

            Hash256? keccak = source.Keccak;
            if (keccak is not null && ValueKeccak.Compute(fullRlp.AsSpan()) != keccak) return null;

            TrieNode detached = keccak is null
                ? new TrieNode(NodeType.Unknown, fullRlp)
                : new TrieNode(NodeType.Unknown, keccak, fullRlp);
            TreePath path = TreePath.Empty;

            try
            {
                return detached.TryResolveNode(NullTrieNodeResolver.Instance, ref path) ? detached : null;
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        Parallel.For(0, ShardCount, (i) =>
        {
            (int hashCode, TrieNode? node)[] shard = transientResource.Nodes.Shards[i];
            for (int j = 0; j < shard.Length; j++)
            {
                if (shard[j].node is not { } source) continue;

                TrieNode? newNode = source.IsWarmerOwned
                    ? TryMaterializeResolvedWarmerNode(source)
                    : source;
                if (newNode is not null)
                {
                    AddToCacheWithHashCode(i, shard[j].hashCode, newNode);
                }
            }
        });

        long currentTotalMemory = 0;
        for (int i = 0; i < ShardCount; i++) currentTotalMemory += _shardMemoryUsages[i];

        long prevMemory = currentTotalMemory;
        bool wasPruned = false;

        // A sweep that frees nothing has still cleared that shard's read flags, so the nodes are evictable on the next
        // pass over it; only once a whole cycle has freed nothing does clearing a shard outright become the way out.
        int unproductiveSweeps = 0;

        while (currentTotalMemory > _maxCacheMemoryThreshold)
        {
            wasPruned = true;
            int shardToClear = _nextShardToClear;
            _nextShardToClear = (_nextShardToClear + 1) & 255; // Fast modulo 256

            long freedMemory;
            if (_shardAccessFlags is null || unproductiveSweeps >= ShardCount)
            {
                freedMemory = ClearShard(shardToClear);
            }
            else
            {
                freedMemory = SweepShard(shardToClear);
                unproductiveSweeps = freedMemory > 0 ? 0 : unproductiveSweeps + 1;
            }

            currentTotalMemory -= freedMemory;
        }

        if (wasPruned && _logger.IsTrace) _logger.Trace($"Pruning trie cache from {prevMemory} to {currentTotalMemory}");

        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = currentTotalMemory;
    }

    /// <summary>Drops every node of a shard and returns the memory it accounted for.</summary>
    private long ClearShard(int shardIdx)
    {
        TrieNode?[] shard = _cacheShards[shardIdx];

        // Prune any remaining reference
        for (int i = 0; i < _bucketSize; i++)
        {
            shard[i]?.PrunePersistedRecursively(1);
        }

        Array.Clear(shard);
        _shardAccessFlags?[shardIdx].AsSpan().Clear();

        return Interlocked.Exchange(ref _shardMemoryUsages[shardIdx], 0);
    }

    /// <summary>
    /// Drops the nodes of a shard that were not read since the previous sweep and gives the rest another round.
    /// </summary>
    /// <remarks>
    /// The shard's accounted memory is rebuilt from the survivors, so it cannot drift as nodes grow after being
    /// cached. Returns the memory freed, which is zero or less when every node of the shard was read since the
    /// previous sweep; the read flags are cleared either way, so the next pass over the shard can evict them.
    /// </remarks>
    private long SweepShard(int shardIdx)
    {
        TrieNode?[] shard = _cacheShards[shardIdx];
        byte[] accessFlags = _shardAccessFlags![shardIdx];
        long retainedMemory = 0;
        int evicted = 0;
        int retained = 0;

        for (int i = 0; i < _bucketSize; i++)
        {
            TrieNode? node = shard[i];
            if (node is null) continue;

            if (accessFlags[i] != 0)
            {
                accessFlags[i] = 0;
                retained++;
                retainedMemory += node.GetMemorySize(false);
                continue;
            }

            if (Interlocked.CompareExchange(ref shard[i], null, node) != node) continue;

            node.PrunePersistedRecursively(1);
            evicted++;
        }

        Nethermind.Trie.Pruning.Metrics.TrieCacheEvictedNodesCount += evicted;
        Nethermind.Trie.Pruning.Metrics.TrieCacheRetainedNodesCount += retained;

        return Interlocked.Exchange(ref _shardMemoryUsages[shardIdx], retainedMemory) - retainedMemory;
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
            _shardAccessFlags?[i].AsSpan().Clear();
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
        private readonly (int hashCode, TrieNode? node)[][] _shards;
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
