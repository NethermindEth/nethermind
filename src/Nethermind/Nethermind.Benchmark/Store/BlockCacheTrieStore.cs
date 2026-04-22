// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Wraps an <see cref="IScopedTrieStore"/> to simulate block-cache (disk-cache) latency in benchmarks.
/// Nodes are grouped into fixed-size path blocks; a miss in the per-shard LRU spin-waits ~100µs to
/// approximate an I/O round trip, so benchmarks measure cold-trie cost realistically instead of the
/// near-zero latency of a fully in-memory store.
/// </summary>
internal sealed class BlockCacheTrieStore(IScopedTrieStore inner, Dictionary<TreePath, int> pathIndex) : IScopedTrieStore
{
    private const int SpinIterations = 5000; // ~100µs I/O latency on cache miss
    private const int LruSize = 416;
    private const int BlockSize = 24;
    private const int ShardCount = 16;

    private readonly ConcurrentDictionary<Hash256, TrieNode> _nodeCache = new();
    private readonly LruKeyCache<int>[] _shards = CreateShards();
    private long _hits;
    private long _misses;
    private long _nodeGets;
    private long _nodeHits;

    private static LruKeyCache<int>[] CreateShards()
    {
        LruKeyCache<int>[] shards = new LruKeyCache<int>[ShardCount];
        for (int i = 0; i < ShardCount; i++)
            shards[i] = new LruKeyCache<int>(LruSize / ShardCount, $"BlockCache_{i}");
        return shards;
    }

    public void ResetBlockCache()
    {
        for (int i = 0; i < ShardCount; i++)
            _shards[i].Clear();
        _nodeCache.Clear();
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
        Interlocked.Exchange(ref _nodeGets, 0);
        Interlocked.Exchange(ref _nodeHits, 0);
    }

    public (long RlpHits, long RlpMisses, long NodeGets, long NodeHits) GetAndResetStats()
    {
        long h = Interlocked.Exchange(ref _hits, 0);
        long m = Interlocked.Exchange(ref _misses, 0);
        long ng = Interlocked.Exchange(ref _nodeGets, 0);
        long nh = Interlocked.Exchange(ref _nodeHits, 0);
        return (h, m, ng, nh);
    }

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        Interlocked.Increment(ref _nodeGets);
        bool existed = _nodeCache.TryGetValue(hash, out TrieNode? node);
        if (existed)
        {
            Interlocked.Increment(ref _nodeHits);
            return node!;
        }
        return _nodeCache.GetOrAdd(hash, static h => new TrieNode(NodeType.Unknown, h));
    }

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        SimulateBlockCache(in path);
        return inner.LoadRlp(in path, hash, flags);
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        SimulateBlockCache(in path);
        return inner.TryLoadRlp(in path, hash, flags);
    }

    private void SimulateBlockCache(in TreePath path)
    {
        if (!pathIndex.TryGetValue(path, out int index))
            throw new InvalidOperationException($"Unknown path: {path}");

        int blockId = index / BlockSize;
        LruKeyCache<int> shard = _shards[(uint)blockId % ShardCount];

        if (shard.Get(blockId))
        {
            Interlocked.Increment(ref _hits);
            return;
        }

        Interlocked.Increment(ref _misses);
        Thread.SpinWait(SpinIterations);
        shard.Set(blockId);
    }

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => inner.GetStorageTrieNodeResolver(address);
    public INodeStorage.KeyScheme Scheme => inner.Scheme;
    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => inner.BeginCommit(root, writeFlags);

    /// <summary>
    /// Walks the trie from <paramref name="root"/> and records each node's <see cref="TreePath"/> in visit order.
    /// Sort the result by path and use the resulting index as block assignment for <see cref="BlockCacheTrieStore"/>.
    /// </summary>
    public static void CollectNodes(IScopedTrieStore store, TrieNode node, TreePath path, List<(TreePath Path, Hash256 Hash)> result)
    {
        node.ResolveNode(store, path);
        if (node.Keccak is Hash256 keccak)
            result.Add((path, keccak));

        if (node.IsBranch)
        {
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                TreePath childPath = path.Append(i);
                TrieNode? child = node.GetChildWithChildPath(store, ref childPath, i);
                if (child is not null)
                    CollectNodes(store, child, childPath, result);
            }
        }
        else if (node.IsExtension)
        {
            TreePath childPath = path;
            childPath.AppendMut(node.Key);
            TrieNode? child = node.GetChildWithChildPath(store, ref childPath, 0);
            if (child is not null)
                CollectNodes(store, child, childPath, result);
        }
    }
}
