// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// The trienode cache is a populated on the latest snapshot instead of on the last one.
/// This has a slightly better hit rate and uses less memory overall.
/// </summary>
public class TrieNodeCache
{
    // Not the nonblocking variant as it use slightly less memory
    private ConcurrentDictionary<Key, TrieNode>[] _cacheShards;
    private long[] _shardMemoryUsages;
    private int _shardCount = 256;
    private long _estimatedMemoryUsage = 0;
    private int _nextShardToClear = 0;
    private long _maxCacheMemoryThreshold;
    private readonly ILogger _logger;

    public TrieNodeCache(long maxCacheMemoryThreshold, ILogManager logManager)
    {
        _cacheShards = new ConcurrentDictionary<Key, TrieNode>[_shardCount];
        for (int i = 0; i < _shardCount; i++)
        {
            _cacheShards[i] = new ConcurrentDictionary<Key, TrieNode>();
        }
        _shardMemoryUsages = new long[_shardCount];
        _maxCacheMemoryThreshold = maxCacheMemoryThreshold;
        _logger = logManager.GetClassLogger<TrieNodeCache>();
    }

    public bool TryGet(Hash256? address, TreePath path, Hash256 hash, out TrieNode node)
    {
        Key key = new Key(address, path);
        int shardIdx = GetShardIdx(key);

        if (_cacheShards[shardIdx].TryGetValue(key, out var maybeNode))
        {
            if (maybeNode.Keccak != hash)
            {
                // TODO: Double check if this is ever expected?
            }
            else
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                node = maybeNode;
                return true;
            }
        }

        node = null;
        return false;
    }

    public void Add(Snapshot snapshot, CachedResource cachedResource)
    {
        if (_maxCacheMemoryThreshold == 0)
        {
            // Note: still need to be done
            // this was explicitly skipped during block processing to make commit run faster,
            // but if this is not done, it will ccause OOM.
            foreach (var kv in cachedResource.TrieWarmerLoadedNodes)
            {
                kv.Value.PrunePersistedRecursively(1);
            }

            foreach (var kv in cachedResource.LoadedStorageNodes)
            {
                kv.Value.PrunePersistedRecursively(1);
            }

            foreach (var kv in snapshot.StateNodes)
            {
                kv.Value.PrunePersistedRecursively(1);
            }

            foreach (var kv in snapshot.StorageNodes)
            {
                kv.Value.PrunePersistedRecursively(1);
            }

            return;
        }

        void AddtoCache(Key key, TrieNode newNode)
        {
            int shardIdx = GetShardIdx(key);
            if (_cacheShards[shardIdx].TryRemove(key, out var node))
            {
                node.PrunePersistedRecursively(1);
                long memory = node.GetMemorySize(false);
                _shardMemoryUsages[shardIdx] -= memory;
                _estimatedMemoryUsage -= memory;
            }

            node = newNode;
            if (_cacheShards[shardIdx].TryAdd(key, node))
            {
                long memory = node.GetMemorySize(false);
                _shardMemoryUsages[shardIdx] += memory;
                _estimatedMemoryUsage += memory;
            }
        }

        foreach (var kv in cachedResource.TrieWarmerLoadedNodes)
        {
            kv.Value.PrunePersistedRecursively(1);
            Key key = new Key(null, kv.Key);
            if (!snapshot.TryGetStateNode(kv.Key, out _))
            {
                AddtoCache(key, kv.Value);
            }
        }

        foreach (var kv in cachedResource.LoadedStorageNodes)
        {
            kv.Value.PrunePersistedRecursively(1);
            Key key = new Key(kv.Key.Item1.Value, kv.Key.Item2);
            if (!snapshot.TryGetStorageNode(kv.Key.Item1, kv.Key.Item2, out _))
            {
                AddtoCache(key, kv.Value);
            }
        }

        foreach (var kv in snapshot.StateNodes)
        {
            kv.Value.PrunePersistedRecursively(1);
            Key key = new Key(null, kv.Key);
            AddtoCache(key, kv.Value);
        }

        foreach (var kv in snapshot.StorageNodes)
        {
            kv.Value.PrunePersistedRecursively(1);
            Key key = new Key(kv.Key.Item1.Value, kv.Key.Item2);
            AddtoCache(key, kv.Value);
        }

        long prevMemory = _estimatedMemoryUsage;
        bool wasPruned = false;
        // TODO: Make 16 parameter configurable.
        while (snapshot.To.blockNumber % 16 == 0 && _estimatedMemoryUsage > _maxCacheMemoryThreshold)
        {
            wasPruned = true;
            int shardToClear = _nextShardToClear;

            foreach (var kv in _cacheShards[shardToClear])
            {
                var node = kv.Value;
                node.PrunePersistedRecursively(1);
            }

            _cacheShards[shardToClear].Clear();
            _shardMemoryUsages[shardToClear] = 0;
            long recalculatedTotalMemory = 0;
            foreach (var shardMemoryUsage in _shardMemoryUsages)
            {
                recalculatedTotalMemory += shardMemoryUsage;
            }

            _estimatedMemoryUsage = recalculatedTotalMemory;

            _nextShardToClear += 1;
            _nextShardToClear %= _shardCount;
        }

        if (wasPruned)
        {
            _logger.Info($"Pruning trie cache from {prevMemory} to {_estimatedMemoryUsage}");
        }

        Nethermind.Trie.Pruning.Metrics.MemoryUsedByCache = _estimatedMemoryUsage;
    }

    private int GetShardIdx(Key key)
    {
        // Separate by tree partition so that when pruned, whole partition is removed. This is because it is
        // more efficient to load nodes from the same partition.
        if (key.Address is null)
        {
            return key.Path.Path.Bytes[0];
        }

        return key.Address.Bytes[0];
    }

    public readonly struct Key : IEquatable<Key>
    {
        internal const long MemoryUsage = 8 + 36 + 8; // (address (probably shared), path, keccak pointer (shared with TrieNode))
        public readonly Hash256? Address;
        // Direct member rather than property for large struct, so members are called directly,
        // rather than struct copy through the property. Could also return a ref through property.
        public readonly TreePath Path;
        public Key(Hash256? address, in TreePath path)
        {
            Address = address;
            Path = path;
        }

        [SkipLocalsInit]
        public override int GetHashCode()
        {
            var addressHash = Address != default ? Address.GetHashCode() : 1;
            return HashCode.Combine(addressHash, Path.GetHashCode());
        }

        public bool Equals(Key other)
        {
            return other.Path == Path && other.Address == Address;
        }

        public override bool Equals(object? obj)
        {
            return obj is Key other && Equals(other);
        }

        public override string ToString()
        {
            return $"A:{Address} P:{Path}";
        }
    }
}
