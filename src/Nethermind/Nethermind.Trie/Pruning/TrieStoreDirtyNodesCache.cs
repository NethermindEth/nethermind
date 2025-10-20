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
    private readonly ConcurrentDictionary<Key, NodeRecord> _byKeyObjectCache;
    private readonly ConcurrentDictionary<Hash256AsKey, NodeRecord> _byHashObjectCache;
    private readonly int _keyTopLevel;

    public long Count => _count;
    public long DirtyCount => _dirtyCount;
    public long TotalMemory => _totalMemory;
    public long TotalDirtyMemory => _totalDirtyMemory;

    public readonly long KeyMemoryUsage;
    private readonly bool _isForCommitBuffer;

    public TrieStoreDirtyNodesCache(TrieStore trieStore, bool storeByHash, int keyTopLevel, ILogger logger, bool isForCommitBuffer = false)
    {
        _trieStore = trieStore;
        _logger = logger;
        // If the nodestore indicated that path is not required,
        // we will use a map with hash as its key instead of the full Key to reduce memory usage.
        _storeByHash = storeByHash;
        // NOTE: DirtyNodesCache is already sharded.
        _keyTopLevel = keyTopLevel;
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

        _isForCommitBuffer = isForCommitBuffer;
    }

    public TrieNode FindCachedOrUnknown(in Key key)
    {
        NodeRecord nodeRecord = GetOrAdd(in key, this);
        if (nodeRecord.Node.NodeType != NodeType.Unknown)
        {
            Metrics.LoadedFromCacheNodesCount++;
        }
        else
        {
            if (_logger.IsTrace) Trace(nodeRecord.Node);
        }

        return nodeRecord.Node;

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
            trieNode = _trieStore.CloneForReadOnly(key, trieNode);

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

    public readonly struct NodeRecord(TrieNode node, long lastCommit)
    {
        public readonly TrieNode Node = node;
        public readonly long LastCommit = lastCommit;
    }

    public IEnumerable<KeyValuePair<Key, NodeRecord>> AllNodes
    {
        get
        {
            if (_storeByHash)
            {
                return _byHashObjectCache.Select(
                    static pair => new KeyValuePair<Key, NodeRecord>(new Key(null, TreePath.Empty, pair.Key.Value), pair.Value));
            }

            return _byKeyObjectCache;
        }
    }

    public bool TryGetValue(in Key key, out TrieNode node)
    {
        bool ok = TryGetRecord(key, out NodeRecord nodeRecord);

        if (ok)
        {
            node = nodeRecord.Node;
            return true;
        }

        node = null;
        return false;
    }

    public bool TryGetRecord(Key key, out NodeRecord nodeRecord)
    {
        return _storeByHash
            ? _byHashObjectCache.TryGetValue(key.Keccak, out nodeRecord)
            : _byKeyObjectCache.TryGetValue(key, out nodeRecord);
    }

    private NodeRecord GetOrAdd(in Key key, TrieStoreDirtyNodesCache cache) => _storeByHash
        ? _byHashObjectCache.GetOrAdd(key.Keccak, static (keccak, cache) =>
        {
            TrieNode trieNode = new(NodeType.Unknown, keccak);
            cache.IncrementMemory(trieNode);
            return new NodeRecord(trieNode, -1);
        }, cache)
        : _byKeyObjectCache.GetOrAdd(key, static (key, cache) =>
        {
            TrieNode trieNode = new(NodeType.Unknown, key.Keccak);
            cache.IncrementMemory(trieNode);
            return new NodeRecord(trieNode, -1);
        }, cache);

    public NodeRecord GetOrAdd(in Key key, NodeRecord record)
    {
        if (_isForCommitBuffer) return GetOrAddForCommitBuffer(key, record);

        return _storeByHash
            ? _byHashObjectCache.AddOrUpdate(key.Keccak, static (key, arg) => arg,
                RecordReplacementLogic, record)
            : _byKeyObjectCache.AddOrUpdate(key, static (key, arg) => arg,
                RecordReplacementLogic, record);
    }

    private static NodeRecord RecordReplacementLogic(Key key, NodeRecord current, NodeRecord arg)
    {
        return RecordReplacementLogic(null, current, arg);
    }

    private static NodeRecord RecordReplacementLogic(Hash256AsKey keyHash, NodeRecord current, NodeRecord arg)
    {
        long lastCommit = current.LastCommit;
        if (arg.LastCommit > lastCommit)
        {
            lastCommit = arg.LastCommit;
        }

        return new NodeRecord(current.Node, lastCommit);
    }

    internal NodeRecord GetOrAddForCommitBuffer(in Key key, NodeRecord record)
    {
        return _storeByHash
            ? _byHashObjectCache.AddOrUpdate(key.Keccak, static (key, arg) => arg,
                RecordReplacementLogicForCommitBuffer, record)
            : _byKeyObjectCache.AddOrUpdate(key, static (key, arg) => arg,
                RecordReplacementLogicForCommitBuffer, record);
    }

    private static NodeRecord RecordReplacementLogicForCommitBuffer(Key key, NodeRecord current, NodeRecord arg)
    {
        return RecordReplacementLogicForCommitBuffer(null, current, arg);
    }

    private static NodeRecord RecordReplacementLogicForCommitBuffer(Hash256AsKey keyHash, NodeRecord current, NodeRecord arg)
    {
        long lastCommit = current.LastCommit;
        if (arg.LastCommit > lastCommit)
        {
            lastCommit = arg.LastCommit;
        }

        TrieNode? node = current.Node;
        if (node.IsPersisted && !arg.Node.IsPersisted)
        {
            // For commit buffer, always replace persisted node with unpersisted node
            // This is because in the main trie store, the node may be removed concurrently so we need it
            // to be re-persisted later.
            node = arg.Node;
        }

        return new NodeRecord(node, lastCommit);
    }

    public void IncrementMemory(TrieNode node)
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
            if (_byHashObjectCache.Remove(key.Keccak, out NodeRecord nodeRecord))
            {
                DecrementMemory(nodeRecord.Node);
            }

            return;
        }
        if (_byKeyObjectCache.Remove(key, out NodeRecord nodeRecord2))
        {
            DecrementMemory(nodeRecord2.Node);
        }
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
        (totalMemory, dirtyMemory, totalNode, dirtyNode) = PruneCacheUnlocked(prunePersisted, forceRemovePersistedNodes, persistedHashes, writeBatcher);

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
        foreach ((Key key, NodeRecord nodeRecord) in AllNodes)
        {
            var node = nodeRecord.Node;
            var lastCommit = nodeRecord.LastCommit;
            if (node.IsPersisted)
            {
                // Remove persisted node based on `persistedHashes` if available.
                if (persistedHashes is not null && key.Path.Length <= TinyTreePath.MaxNibbleLength)
                {
                    HashAndTinyPath tinyKey = new(key.Address, new TinyTreePath(key.Path));
                    if (persistedHashes.TryGetValue(tinyKey, out Hash256? lastPersistedHash))
                    {
                        if (CanDelete(key, lastCommit, lastPersistedHash))
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
                    if (lastCommit == -1 || forceRemovePersistedNodes)
                    {
                        if (_logger.IsTrace) LogPersistedNodeRemoval(node);

                        Hash256? keccak = (node.Keccak ??= GenerateKeccak(key, node));
                        RemoveNodeFromCache(key, node, ref Metrics.PrunedPersistedNodesCount);
                        continue;
                    }

                    if (_trieStore.IsNoLongerNeeded(lastCommit))
                    {
                        if (!_storeByHash && key.Address == null)
                        {
                            if (key.Path.Length <= _keyTopLevel)
                            {
                                // Do not remove top level persisted node. This is so that it always get
                                // removed via key removal and not due to memory limitation.
                                totalMemory += node.GetMemorySize(false) + KeyMemoryUsage;
                                totalNode++;
                                continue;
                            }
                        }

                        RemoveNodeFromCache(key, node, ref Metrics.PrunedPersistedNodesCount);
                        continue;
                    }
                }
            }
            else if (_trieStore.IsNoLongerNeeded(lastCommit))
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
            keccak = node.GenerateKey(_trieStore.GetTrieStore(key.Address), ref path2);
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

        [DoesNotReturn, StackTraceHidden]
        static void ThrowKeccakIsNull(TrieNode node) => throw new InvalidOperationException($"Removed {node}");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowPersistedNodeDoesNotMatch(in Key key, TrieNode node, Hash256 keccak)
            => throw new InvalidOperationException($"Persisted {node} {key} != {keccak}");
    }

    private void Delete(Key key, ConcurrentNodeWriteBatcher? writeBatch)
    {
        Metrics.RemovedNodeCount++;
        Remove(key);
        writeBatch?.Set(key.Address, key.Path, key.Keccak, default, WriteFlags.DisableWAL);
    }

    bool CanDelete(in Key key, long lastCommit, Hash256? currentlyPersistingKeccak)
    {
        // Multiple current hash that we don't keep track for simplicity. Just ignore this case.
        if (currentlyPersistingKeccak is null) return false;

        // The persisted hash is the same as currently persisting hash. Do nothing.
        if (currentlyPersistingKeccak == key.Keccak) return false;

        // We have it in cache and it is still needed.
        if (!_trieStore.IsNoLongerNeeded(lastCommit)) return false;

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
                nodeStorage.Set(address, path, n.Keccak, n.FullRlp.Span);
                n.IsPersisted = true;
                persistedCount++;
            }
        }

        foreach (KeyValuePair<Key, NodeRecord> kv in AllNodes)
        {
            if (cancellationToken.IsCancellationRequested) return persistedCount;
            Key key = kv.Key;
            TreePath path = key.Path;
            Hash256? address = key.Address;
            kv.Value.Node.CallRecursively(PersistNode, address, ref path, _trieStore.GetTrieStore(address), false, _logger, resolveStorageRoot: false);
        }

        return persistedCount;
    }

    public void Dump()
    {
        if (_logger.IsTrace)
        {
            _logger.Trace($"Trie node dirty cache ({Count})");
            foreach (KeyValuePair<Key, NodeRecord> keyValuePair in AllNodes)
            {
                _logger.Trace($"  {keyValuePair.Value}");
            }
        }
    }

    public void Clear()
    {
        _byHashObjectCache.NoResizeClear();
        _byKeyObjectCache.NoResizeClear();
        Interlocked.Exchange(ref _count, 0);
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

    public void FlushCommitBuffer(TrieStoreDirtyNodesCache otherCache)
    {
        foreach (var kv in AllNodes)
        {
            otherCache.GetOrAddForCommitBuffer(kv.Key, kv.Value);
        }
        Clear();
    }
}
