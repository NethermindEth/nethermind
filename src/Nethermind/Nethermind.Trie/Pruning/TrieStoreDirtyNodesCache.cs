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
using Nethermind.Core.Cpu;
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

    public long Count => _count;
    public long DirtyCount => _dirtyCount;
    public long TotalMemory => _totalMemory;
    public long TotalDirtyMemory => _totalDirtyMemory;

    public readonly long KeyMemoryUsage;
    private readonly bool _keepRoot;

    internal static void GetDictionarySizing(out int concurrencyLevel, out int initialBuckets)
    {
        // Core.Cpu.RuntimeInformation.ProcessorCount floors to 1 where Environment.ProcessorCount
        // can report 0 (zk-evm). ConcurrentDictionary requires concurrencyLevel >= 1.
        concurrencyLevel = Math.Min(RuntimeInformation.ProcessorCount * 4, 32);
        initialBuckets = TrieStore.HashHelpers.GetPrime(Math.Max(31, concurrencyLevel));
    }

    public TrieStoreDirtyNodesCache(TrieStore trieStore, bool storeByHash, bool keepRoot, ILogger logger)
    {
        _trieStore = trieStore;
        _logger = logger;
        // If the nodestore indicated that path is not required,
        // we will use a map with hash as its key instead of the full Key to reduce memory usage.
        _storeByHash = storeByHash;

        // Keep root causes persisted root nodes to not get pruned out of the cache. This ensure that it will
        // be deleted when another canonical state is persisted which prevent having incomplete state which can happen
        // when inner nodes get deleted but the root does not.
        _keepRoot = keepRoot;

        // NOTE: DirtyNodesCache is already sharded.
        GetDictionarySizing(out int concurrencyLevel, out int initialBuckets);
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
        void Trace(TrieNode trieNode) => _logger.Trace($"Creating new node {trieNode}");
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
        void Trace(TrieNode trieNode) => _logger.Trace($"Creating new node {trieNode}");
    }

    public bool IsNodeCached(in Key key)
    {
        if (_storeByHash) return _byHashObjectCache.ContainsKey(key.Keccak);
        return _byKeyObjectCache.ContainsKey(key);
    }

    public readonly struct NodeRecord(TrieNode node, long lastCommit) : IEquatable<NodeRecord>
    {
        public readonly TrieNode Node = node;
        public readonly long LastCommit = lastCommit;

        public bool Equals(NodeRecord other) => other.Node == Node && other.LastCommit == LastCommit;
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

    public bool TryGetRecord(Key key, out NodeRecord nodeRecord) => _storeByHash
            ? _byHashObjectCache.TryGetValue(key.Keccak, out nodeRecord)
            : _byKeyObjectCache.TryGetValue(key, out nodeRecord);

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

    public NodeRecord GetOrAdd(in Key key, NodeRecord record) => _storeByHash
            ? GetOrAdd(_byHashObjectCache, key.Keccak, record)
            : GetOrAdd(_byKeyObjectCache, key, record);

    private static NodeRecord GetOrAdd<TKey>(ConcurrentDictionary<TKey, NodeRecord> dictionary, TKey key, NodeRecord record)
        where TKey : notnull
    {
        // Avoid AddOrUpdate here: an existing key often merges to the same logical NodeRecord,
        // and this fast path returns without forcing ConcurrentDictionary's update path.
        while (true)
        {
            if (dictionary.TryGetValue(key, out NodeRecord current))
            {
                NodeRecord merged = MergeRecords(current, record);
                if (merged.Equals(current) || dictionary.TryUpdate(key, merged, current))
                {
                    return merged;
                }

                continue;
            }

            if (dictionary.TryAdd(key, record))
            {
                return record;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static NodeRecord MergeRecords(NodeRecord current, NodeRecord candidate)
    {
        long lastCommit = current.LastCommit;
        if (candidate.LastCommit > lastCommit)
        {
            lastCommit = candidate.LastCommit;
        }

        TrieNode node = current.Node;
        if (node.IsPersisted && !candidate.Node.IsPersisted)
        {
            // This code path happens around 0.8% of the time at 4GB of dirty cache and 16GB total cache.
            //
            // If the cache node is persisted, we replace it completely.
            // This is because although very rare, it is possible that this node is persisted, but its child is not
            // persisted. This can happen when a path is not replaced with another node, but its child is and hence,
            // the child is removed, but the parent is not and remain in the cache as persisted node.
            // Additionally, it may hold a reference to its child which is marked as persisted even though it was
            // deleted from the cached map.
            node = candidate.Node;
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
            TrieNode node = nodeRecord.Node;
            long lastCommit = nodeRecord.LastCommit;
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

                    if (_trieStore.IsNoLongerNeeded(lastCommit) && !(_keepRoot && key.IsRoot()))
                    {
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
            Key key = new(address, path, n.Keccak);
            if (wasPersisted.TryAdd(key, true))
            {
                nodeStorage.Set(address, path, n.Keccak, n.FullRlp.AsSpan());
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

    internal readonly struct Key(Hash256? address, in TreePath path, Hash256 keccak) : IEquatable<Key>
    {
        internal const long MemoryUsage = 8 + 36 + 8; // (address (probably shared), path, keccak pointer (shared with TrieNode))
        public readonly Hash256? Address = address;
        // Direct member rather than property for large struct, so members are called directly,
        // rather than struct copy through the property. Could also return a ref through property.
        public readonly TreePath Path = path;
        public Hash256 Keccak { get; } = keccak;

        [SkipLocalsInit]
        public override int GetHashCode()
        {
            ulong chainedHash = ((ulong)(uint)Path.GetHashCode() << 32) | (uint)(Address?.GetHashCode() ?? 1);
            return Keccak.ValueHash256.GetChainedHashCode(chainedHash);
        }

        public bool Equals(Key other) => other.Keccak == Keccak && other.Path == Path && other.Address == Address;

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override string ToString() => $"A:{Address} P:{Path} K:{Keccak}";

        public bool IsRoot() => Address is null && Path.Length == 0;
    }

    public void CopyTo(TrieStoreDirtyNodesCache otherCache)
    {
        foreach (KeyValuePair<Key, NodeRecord> kv in AllNodes)
        {
            kv.Value.Node.PrunePersistedRecursively(1);
            otherCache.GetOrAdd(kv.Key, kv.Value);
        }
        Clear();
    }
}
