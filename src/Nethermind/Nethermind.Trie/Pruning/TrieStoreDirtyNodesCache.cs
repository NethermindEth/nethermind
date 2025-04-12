// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning;

internal class TrieStoreDirtyNodesCache
{
    private readonly TrieStore _trieStore;
    private long _count = 0;
    private long _dirtyCount = 0;
    private long _totalMemory = 0;
    private long _totalDirtyMemory = 0;
    private readonly ILogger _logger;
    private readonly bool _storeByHash;
    private readonly ConcurrentDictionary<Key, TrieNode> _byKeyObjectCache;
    private readonly ConcurrentDictionary<Hash256AsKey, TrieNode> _byHashObjectCache;

    public long Count => _count;
    public long DirtyCount => _dirtyCount;
    public long TotalMemory => _totalMemory;
    public long TotalDirtyMemory => _totalDirtyMemory;

    public readonly long KeyMemoryUsage;

    public TrieStoreDirtyNodesCache(TrieStore trieStore, bool storeByHash, ILogger logger)
    {
        _trieStore = trieStore;
        _logger = logger;
        // If the nodestore indicated that path is not required,
        // we will use a map with hash as its key instead of the full Key to reduce memory usage.
        _storeByHash = storeByHash;
        // NOTE: DirtyNodesCache is already sharded.
        int concurrencyLevel = Math.Min(Environment.ProcessorCount * 4, 32);
        int initialBuckets = TrieStore.HashHelpers.GetPrime(Math.Max(31, concurrencyLevel));
        if (_storeByHash)
        {
            _byHashObjectCache = new(concurrencyLevel, initialBuckets);
        }
        else
        {
            _byKeyObjectCache = new(concurrencyLevel, initialBuckets);
        }
        KeyMemoryUsage = _storeByHash ? 0 : Key.MemoryUsage; // 0 because previously it was not counted.

        // Overhead for each key in concurrent dictionary. The key is stored in a "node" for the hashtable.
        // <object header> + <value ref> + <hashcode> + <next node ref>
        KeyMemoryUsage += MemorySizes.ObjectHeaderMethodTable + MemorySizes.RefSize + 4 + MemorySizes.RefSize;
    }

    public void SaveInCache(in Key key, TrieNode node)
    {
        Debug.Assert(node.Keccak is not null, "Cannot store in cache nodes without resolved key.");
        if (TryAdd(key, node))
        {
            IncrementMemory(node);
        }
    }

    public TrieNode FindCachedOrUnknown(in Key key)
    {
        if (TryGetValue(key, out TrieNode trieNode))
        {
            Metrics.LoadedFromCacheNodesCount++;
        }
        else
        {
            trieNode = new TrieNode(NodeType.Unknown, key.Keccak);
            if (_logger.IsTrace) Trace(trieNode);
            SaveInCache(key, trieNode);
        }

        return trieNode;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(TrieNode trieNode)
        {
            _logger.Trace($"Creating new node {trieNode}");
        }
    }

    public TrieNode FromCachedRlpOrUnknown(in Key key)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
        if (TryGetValue(key, out TrieNode trieNode))
        {
            if (trieNode!.FullRlp.IsNull)
            {
                // // this happens in SyncProgressResolver
                // throw new InvalidAsynchronousStateException("Read only trie store is trying to read a transient node.");
                return new TrieNode(NodeType.Unknown, key.Keccak);
            }

            // we returning a copy to avoid multithreaded access
            trieNode = new TrieNode(NodeType.Unknown, key.Keccak, trieNode.FullRlp);
            trieNode.ResolveNode(_trieStore.GetTrieStore(key.Address), key.Path);
            trieNode.Keccak = key.Keccak;

            Metrics.LoadedFromCacheNodesCount++;
        }
        else
        {
            trieNode = new TrieNode(NodeType.Unknown, key.Keccak);
        }

        if (_logger.IsTrace) Trace(trieNode);
        return trieNode;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(TrieNode trieNode)
        {
            _logger.Trace($"Creating new node {trieNode}");
        }
    }

    public bool IsNodeCached(in Key key)
    {
        if (_storeByHash) return _byHashObjectCache.ContainsKey(key.Keccak);
        return _byKeyObjectCache.ContainsKey(key);
    }

    public IEnumerable<KeyValuePair<Key, TrieNode>> AllNodes
    {
        get
        {
            if (_storeByHash)
            {
                return _byHashObjectCache.Select(
                    static pair => new KeyValuePair<Key, TrieNode>(new Key(null, TreePath.Empty, pair.Key.Value), pair.Value));
            }

            return _byKeyObjectCache;
        }
    }

    public bool TryGetValue(in Key key, out TrieNode node) => _storeByHash
        ? _byHashObjectCache.TryGetValue(key.Keccak, out node)
        : _byKeyObjectCache.TryGetValue(key, out node);

    private bool TryAdd(in Key key, TrieNode node) => _storeByHash
        ? _byHashObjectCache.TryAdd(key.Keccak, node)
        : _byKeyObjectCache.TryAdd(key, node);

    private void IncrementMemory(TrieNode node)
    {
        long memoryUsage = node.GetMemorySize(false) + KeyMemoryUsage;
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _totalMemory, memoryUsage);
        if (!node.IsPersisted)
        {
            Interlocked.Increment(ref _dirtyCount);
            Interlocked.Add(ref _totalDirtyMemory, memoryUsage);
        }
        _trieStore.IncrementMemoryUsedByDirtyCache(memoryUsage, node.IsPersisted);
    }

    private void DecrementMemory(TrieNode node)
    {
        long memoryUsage = node.GetMemorySize(false) + KeyMemoryUsage;
        Interlocked.Decrement(ref _count);
        Interlocked.Add(ref _totalMemory, -memoryUsage);
        if (!node.IsPersisted)
        {
            Interlocked.Decrement(ref _dirtyCount);
            Interlocked.Add(ref _totalDirtyMemory, -memoryUsage);
        }
        _trieStore.DecreaseMemoryUsedByDirtyCache(memoryUsage, node.IsPersisted);
    }

    private void Remove(in Key key)
    {
        if (_storeByHash)
        {
            if (_byHashObjectCache.Remove(key.Keccak, out TrieNode node))
            {
                DecrementMemory(node);
            }

            return;
        }
        if (_byKeyObjectCache.Remove<Key, TrieNode>(key, out TrieNode node2))
        {
            DecrementMemory(node2);
        }
    }

    private MapLock AcquireMapLock()
    {
        if (_storeByHash)
        {
            return new MapLock()
            {
                _storeByHash = _storeByHash,
                _byHashLock = _byHashObjectCache.AcquireLock()
            };
        }
        return new MapLock()
        {
            _storeByHash = _storeByHash,
            _byKeyLock = _byKeyObjectCache.AcquireLock<Key, TrieNode>()
        };
    }

    /// <summary>
    /// This method is responsible for reviewing the nodes that are directly in the cache and
    /// removing ones that are either no longer referenced or already persisted.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public void PruneCache(
        bool prunePersisted = false,
        bool forceRemovePersistedNodes = false,
        ConcurrentDictionary<HashAndTinyPath, Hash256?>? persistedHashes = null,
        INodeStorage? nodeStorage = null)
    {

        ConcurrentNodeWriteBatcher? writeBatcher = nodeStorage is not null
            ? new ConcurrentNodeWriteBatcher(nodeStorage, 256) : null;

        long totalMemory, dirtyMemory, totalNode, dirtyNode;
        using (AcquireMapLock())
        {
            (totalMemory, dirtyMemory, totalNode, dirtyNode) = PruneCacheUnlocked(prunePersisted, forceRemovePersistedNodes, persistedHashes, writeBatcher);
        }

        writeBatcher?.Dispose();

        _count = totalNode;
        _dirtyCount = dirtyNode;
        _totalMemory = totalMemory;
        _totalDirtyMemory = dirtyMemory;
    }

    private (long totalMemory, long dirtyMemory, long totalNode, long dirtyNode) PruneCacheUnlocked(
        bool prunePersisted,
        bool forceRemovePersistedNodes,
        ConcurrentDictionary<HashAndTinyPath, Hash256?>? persistedHashes,
        ConcurrentNodeWriteBatcher? writeBatcher)
    {
        long totalMemory = 0;
        long dirtyMemory = 0;
        long totalNode = 0;
        long dirtyNode = 0;
        foreach ((Key key, TrieNode node) in AllNodes)
        {
            if (node.IsPersisted)
            {
                // Remove persisted node based on `persistedHashes` if available.
                if (persistedHashes is not null && key.Path.Length <= TinyTreePath.MaxNibbleLength)
                {
                    HashAndTinyPath tinyKey = new(key.Address, new TinyTreePath(key.Path));
                    if (persistedHashes.TryGetValue(tinyKey, out Hash256? lastPersistedHash))
                    {
                        if (CanDelete(key, lastPersistedHash))
                        {
                            Delete(key, writeBatcher);
                            continue;
                        }
                    }
                }

                if (prunePersisted)
                {
                    // If its persisted and has last seen meaning it was recommitted,
                    // we keep it to prevent key removal from removing it from DB.
                    if (node.LastSeen == -1 || forceRemovePersistedNodes)
                    {
                        if (_logger.IsTrace) LogPersistedNodeRemoval(node);

                        Hash256? keccak = (node.Keccak ??= GenerateKeccak(key, node));
                        RemoveNodeFromCache(key, node, ref Metrics.PrunedPersistedNodesCount);
                        continue;
                    }

                    if (_trieStore.IsNoLongerNeeded(node))
                    {
                        RemoveNodeFromCache(key, node, ref Metrics.PrunedPersistedNodesCount);
                        continue;
                    }
                }
            }
            else if (_trieStore.IsNoLongerNeeded(node))
            {
                RemoveNodeFromCache(key, node, ref Metrics.DeepPrunedPersistedNodesCount);
                continue;
            }

            node.PrunePersistedRecursively(1);
            long memory = node.GetMemorySize(false) + KeyMemoryUsage;
            totalMemory += memory;
            totalNode++;

            if (!node.IsPersisted)
            {
                dirtyMemory += memory;
                dirtyNode++;
            }
        }

        return (totalMemory, dirtyMemory, totalNode, dirtyNode);

        Hash256 GenerateKeccak(in Key key, TrieNode node)
        {
            Hash256 keccak;
            TreePath path2 = key.Path;
            keccak = node.GenerateKey(_trieStore.GetTrieStore(key.Address), ref path2, isRoot: true);
            if (keccak != key.Keccak)
            {
                ThrowPersistedNodeDoesNotMatch(key, node, keccak);
            }

            return keccak;
        }

        void RemoveNodeFromCache(in Key key, TrieNode node, ref long metric)
        {
            if (_logger.IsTrace) LogNodeRemoval(node);
            if (node.Keccak is null)
            {
                ThrowKeccakIsNull(node);
            }

            metric++;

            Remove(key);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogPersistedNodeRemoval(TrieNode node) => _logger.Trace($"Removing persisted {node} from memory.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogNodeRemoval(TrieNode node) => _logger.Trace($"Removing {node} from memory.");

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowKeccakIsNull(TrieNode node) => throw new InvalidOperationException($"Removed {node}");

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowPersistedNodeDoesNotMatch(in Key key, TrieNode node, Hash256 keccak)
            => throw new InvalidOperationException($"Persisted {node} {key} != {keccak}");
    }

    private void Delete(Key key, ConcurrentNodeWriteBatcher? writeBatch)
    {
        Metrics.RemovedNodeCount++;
        Remove(key);
        writeBatch?.Set(key.Address, key.Path, key.Keccak, default, WriteFlags.DisableWAL);
    }

    bool CanDelete(in Key key, Hash256? currentlyPersistingKeccak)
    {
        // Multiple current hash that we don't keep track for simplicity. Just ignore this case.
        if (currentlyPersistingKeccak is null) return false;

        // The persisted hash is the same as currently persisting hash. Do nothing.
        if (currentlyPersistingKeccak == key.Keccak) return false;

        // We have it in cache and it is still needed.
        if (TryGetValue(in key, out TrieNode node) &&
            !_trieStore.IsNoLongerNeeded(node)) return false;

        return true;
    }

    public int PersistAll(INodeStorage nodeStorage, CancellationToken cancellationToken)
    {
        ConcurrentDictionary<Key, bool> wasPersisted = new();
        int persistedCount = 0;

        void PersistNode(TrieNode n, Hash256? address, TreePath path)
        {
            if (n.Keccak is null) return;
            if (n.NodeType == NodeType.Unknown) return;
            Key key = new Key(address, path, n.Keccak);
            if (wasPersisted.TryAdd(key, true))
            {
                nodeStorage.Set(address, path, n.Keccak, n.FullRlp);
                n.IsPersisted = true;
                persistedCount++;
            }
        }

        using (AcquireMapLock())
        {
            foreach (KeyValuePair<Key, TrieNode> kv in AllNodes)
            {
                if (cancellationToken.IsCancellationRequested) return persistedCount;
                Key key = kv.Key;
                TreePath path = key.Path;
                Hash256? address = key.Address;
                kv.Value.CallRecursively(PersistNode, address, ref path, _trieStore.GetTrieStore(address), false, _logger, resolveStorageRoot: false);
            }
        }

        return persistedCount;
    }

    public void Dump()
    {
        if (_logger.IsTrace)
        {
            _logger.Trace($"Trie node dirty cache ({Count})");
            foreach (KeyValuePair<Key, TrieNode> keyValuePair in AllNodes)
            {
                _logger.Trace($"  {keyValuePair.Value}");
            }
        }
    }

    public void Clear()
    {
        _byHashObjectCache.NoResizeClear();
        _byKeyObjectCache.NoResizeClear<Key, TrieNode>();
        Interlocked.Exchange(ref _count, 0);
        _trieStore.MemoryUsedByDirtyCache = 0;
    }

    internal readonly struct Key : IEquatable<Key>
    {
        internal const long MemoryUsage = 8 + 36 + 8; // (address (probably shared), path, keccak pointer (shared with TrieNode))
        public readonly Hash256? Address;
        // Direct member rather than property for large struct, so members are called directly,
        // rather than struct copy through the property. Could also return a ref through property.
        public readonly TreePath Path;
        public Hash256 Keccak { get; }

        public Key(Hash256? address, in TreePath path, Hash256 keccak)
        {
            Address = address;
            Path = path;
            Keccak = keccak;
        }

        [SkipLocalsInit]
        public override int GetHashCode()
        {
            var addressHash = Address != default ? Address.GetHashCode() : 1;
            return Keccak.ValueHash256.GetChainedHashCode((uint)Path.GetHashCode()) ^ addressHash;
        }

        public bool Equals(Key other)
        {
            return other.Keccak == Keccak && other.Path == Path && other.Address == Address;
        }

        public override bool Equals(object? obj)
        {
            return obj is Key other && Equals(other);
        }

        public override string ToString()
        {
            return $"A:{Address} P:{Path} K:{Keccak}";
        }
    }

    internal ref struct MapLock
    {
        public bool _storeByHash;
        public ConcurrentDictionaryLock<Hash256AsKey, TrieNode>.Lock _byHashLock;
        public ConcurrentDictionaryLock<Key, TrieNode>.Lock _byKeyLock;

        public readonly void Dispose()
        {
            if (_storeByHash)
            {
                _byHashLock.Dispose();
            }
            else
            {
                _byKeyLock.Dispose();
            }
        }
    }
}
