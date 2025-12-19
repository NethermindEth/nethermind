// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

/// <summary>
/// A dirty-nodes cache used by <see cref="TrieStore"/>.
/// </summary>
/// <remarks>
/// Upstream implementation used <c>ConcurrentDictionary</c>. Some custom runtimes/compilers used by this project
/// have issues with <c>ConcurrentDictionary</c> instantiation. This version uses a sharded <see cref="Dictionary{TKey,TValue}"/>
/// guarded by locks.
/// </remarks>
internal sealed class TrieStoreDirtyNodesCache
{
    internal readonly struct Key : IEquatable<Key>
    {
        // Keep shape compatible with TrieStore expectations.
        public static readonly long MemoryUsage = 0;

        public readonly Hash256? Address;
        public readonly TreePath Path;
        public readonly Hash256? Keccak;

        public Key(Hash256? address, in TreePath path, Hash256? keccak)
        {
            Address = address;
            Path = path;
            Keccak = keccak;
        }

        public bool Equals(Key other) =>
            Address == other.Address && Path.Equals(other.Path) && Keccak == other.Keccak;

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Address, Path, Keccak);
    }

    private readonly TrieStore _trieStore;
    private readonly ILogger _logger;

    // If the nodestore indicated that path is not required,
    // we will use a map with hash as its key instead of the full Key to reduce memory usage.
    private readonly bool _storeByHash;

    private long _count;
    private long _dirtyCount;
    private long _totalMemory;
    private long _totalDirtyMemory;

    public long Count => Volatile.Read(ref _count);
    public long DirtyCount => Volatile.Read(ref _dirtyCount);
    public long TotalMemory => Volatile.Read(ref _totalMemory);
    public long TotalDirtyMemory => Volatile.Read(ref _totalDirtyMemory);

    public readonly long KeyMemoryUsage;

    // This class is already created per-shard by TrieStore, but we add internal sharding to keep lock contention low
    // without relying on ConcurrentDictionary.
    private const int ShardCount = 16; // must be power of two
    private readonly object[] _locks = new object[ShardCount];

    private readonly Dictionary<Key, TrieNode>[]? _byKey;
    private readonly Dictionary<Hash256AsKey, TrieNode>[]? _byHash;

    public TrieStoreDirtyNodesCache(TrieStore trieStore, bool storeByHash, ILogger logger)
    {
        _trieStore = trieStore;
        _logger = logger;
        _storeByHash = storeByHash;

        for (int i = 0; i < ShardCount; i++)
        {
            _locks[i] = new object();
        }

        // Keep initial capacity modest; TrieStore has higher-level sharding already.
        const int initialCapacity = 31;

        if (_storeByHash)
        {
            _byHash = new Dictionary<Hash256AsKey, TrieNode>[ShardCount];
            for (int i = 0; i < ShardCount; i++)
            {
                _byHash[i] = new Dictionary<Hash256AsKey, TrieNode>(initialCapacity);
            }
        }
        else
        {
            _byKey = new Dictionary<Key, TrieNode>[ShardCount];
            for (int i = 0; i < ShardCount; i++)
            {
                _byKey[i] = new Dictionary<Key, TrieNode>(initialCapacity);
            }
        }

        KeyMemoryUsage = _storeByHash ? 0 : Key.MemoryUsage;

        // Keep upstream estimate to avoid changing metrics semantics.
        // <object header> + <value ref> + <hashcode> + <next node ref>
        KeyMemoryUsage += MemorySizes.ObjectHeaderMethodTable + MemorySizes.RefSize + 4 + MemorySizes.RefSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ShardIndex(int hashCode) => (hashCode & 0x7fffffff) & (ShardCount - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object ShardLock(int shard) => _locks[shard];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Dictionary<Key, TrieNode> KeyMap(int shard)
    {
        // _byKey is created when !_storeByHash
        return _byKey![shard];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Dictionary<Hash256AsKey, TrieNode> HashMap(int shard)
    {
        // _byHash is created when _storeByHash
        return _byHash![shard];
    }

    public TrieNode FindCachedOrUnknown(in Key key)
    {
        TrieNode trieNode = GetOrAddUnknown(in key);

        if (trieNode.NodeType != NodeType.Unknown)
        {
            Metrics.LoadedFromCacheNodesCount++;
        }
        else
        {
            if (_logger.IsTrace) Trace(trieNode);
        }

        return trieNode;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(TrieNode node) => _logger.Trace($"Creating new node {node}");
    }

    public TrieNode FromCachedRlpOrUnknown(in Key key)
    {
        if (TryGetValue(in key, out TrieNode trieNode))
        {
            if (trieNode!.FullRlp.IsNull)
            {
                // happens in some sync scenarios; treat as unknown
                return new TrieNode(NodeType.Unknown, key.Keccak);
            }

            // Return a copy to avoid multithreaded access.
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
        void Trace(TrieNode node) => _logger.Trace($"Creating new node {node}");
    }

    public bool IsNodeCached(in Key key)
    {
        if (_storeByHash)
        {
            Hash256AsKey hk = key.Keccak;
            int shard = ShardIndex(hk.GetHashCode());
            lock (ShardLock(shard))
            {
                return HashMap(shard).ContainsKey(hk);
            }
        }

        int shardKey = ShardIndex(key.GetHashCode());
        lock (ShardLock(shardKey))
        {
            return KeyMap(shardKey).ContainsKey(key);
        }
    }

    public IEnumerable<KeyValuePair<Key, TrieNode>> AllNodes
    {
        get
        {
            // Snapshot to avoid exposing internal dictionaries and to avoid long-held locks during enumeration.
            if (_storeByHash)
            {
                List<KeyValuePair<Key, TrieNode>> result = new();
                for (int i = 0; i < ShardCount; i++)
                {
                    lock (ShardLock(i))
                    {
                        foreach (KeyValuePair<Hash256AsKey, TrieNode> pair in HashMap(i))
                        {
                            result.Add(new KeyValuePair<Key, TrieNode>(
                                new Key(null, TreePath.Empty, pair.Key.Value),
                                pair.Value));
                        }
                    }
                }

                return result;
            }
            else
            {
                List<KeyValuePair<Key, TrieNode>> result = new();
                for (int i = 0; i < ShardCount; i++)
                {
                    lock (ShardLock(i))
                    {
                        foreach (KeyValuePair<Key, TrieNode> pair in KeyMap(i))
                        {
                            result.Add(pair);
                        }
                    }
                }

                return result;
            }
        }
    }

    public bool TryGetValue(in Key key, [NotNullWhen(true)] out TrieNode node)
    {
        if (_storeByHash)
        {
            Hash256AsKey hk = key.Keccak;
            int shard = ShardIndex(hk.GetHashCode());
            lock (ShardLock(shard))
            {
                return HashMap(shard).TryGetValue(hk, out node!);
            }
        }

        int shardKey = ShardIndex(key.GetHashCode());
        lock (ShardLock(shardKey))
        {
            return KeyMap(shardKey).TryGetValue(key, out node!);
        }
    }

    public TrieNode GetOrAdd(in Key key, TrieNode node)
    {
        if (_storeByHash)
        {
            Hash256AsKey hk = key.Keccak;
            int shard = ShardIndex(hk.GetHashCode());
            lock (ShardLock(shard))
            {
                Dictionary<Hash256AsKey, TrieNode> map = HashMap(shard);
                if (!map.TryGetValue(hk, out TrieNode existing))
                {
                    map[hk] = node;
                    // memory tracking is handled by caller for non-unknown nodes; keep behavior consistent with original:
                    // only track memory for newly created unknown nodes in GetOrAddUnknown.
                    return node;
                }

                return existing;
            }
        }

        int shardKey = ShardIndex(key.GetHashCode());
        lock (ShardLock(shardKey))
        {
            Dictionary<Key, TrieNode> map = KeyMap(shardKey);
            if (!map.TryGetValue(key, out TrieNode existing))
            {
                map[key] = node;
                return node;
            }

            return existing;
        }
    }

    public void Replace(in Key key, TrieNode node)
    {
        if (_storeByHash)
        {
            Hash256AsKey hk = key.Keccak;
            int shard = ShardIndex(hk.GetHashCode());
            lock (ShardLock(shard))
            {
                HashMap(shard)[hk] = node;
            }

            return;
        }

        int shardKey = ShardIndex(key.GetHashCode());
        lock (ShardLock(shardKey))
        {
            KeyMap(shardKey)[key] = node;
        }
    }

    public void Remove(in Key key)
    {
        if (_storeByHash)
        {
            Hash256AsKey hk = key.Keccak;
            int shard = ShardIndex(hk.GetHashCode());
            lock (ShardLock(shard))
            {
                if (HashMap(shard).Remove(hk, out TrieNode removed))
                {
                    DecrementMemory(removed);
                }
            }

            return;
        }

        int shardKey = ShardIndex(key.GetHashCode());
        lock (ShardLock(shardKey))
        {
            if (KeyMap(shardKey).Remove(key, out TrieNode removed))
            {
                DecrementMemory(removed);
            }
        }
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

    public void DecreaseMemory(TrieNode node) => DecrementMemory(node);

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

    public void Clear()
    {
        for (int i = 0; i < ShardCount; i++)
        {
            lock (ShardLock(i))
            {
                if (_storeByHash)
                {
                    HashMap(i).Clear();
                }
                else
                {
                    KeyMap(i).Clear();
                }
            }
        }

        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _dirtyCount, 0);
        Interlocked.Exchange(ref _totalMemory, 0);
        Interlocked.Exchange(ref _totalDirtyMemory, 0);

        _trieStore.MemoryUsedByDirtyCache = 0;
    }

    // Used only in tests (TrieStore.Dump calls this)
    public void Dump()
    {
        // Keep it as a no-op to preserve API surface without pulling in more dependencies.
    }

    // ---- Internal helpers ----

    private TrieNode GetOrAddUnknown(in Key key)
    {
        if (_storeByHash)
        {
            Hash256AsKey hk = key.Keccak;
            int shard = ShardIndex(hk.GetHashCode());
            lock (ShardLock(shard))
            {
                Dictionary<Hash256AsKey, TrieNode> map = HashMap(shard);
                if (!map.TryGetValue(hk, out TrieNode node))
                {
                    node = new TrieNode(NodeType.Unknown, hk.Value);
                    map[hk] = node;
                    IncrementMemory(node);
                }

                return node;
            }
        }
        else
        {
            int shardKey = ShardIndex(key.GetHashCode());
            lock (ShardLock(shardKey))
            {
                Dictionary<Key, TrieNode> map = KeyMap(shardKey);
                if (!map.TryGetValue(key, out TrieNode node))
                {
                    node = new TrieNode(NodeType.Unknown, key.Keccak);
                    map[key] = node;
                    IncrementMemory(node);
                }

                return node;
            }
        }
    }

    // ---- Compatibility surface used by TrieStore ----

    internal readonly struct MapLock : IDisposable
    {
        private readonly TrieStoreDirtyNodesCache _cache;
        private readonly bool _storeByHash;

        public MapLock(TrieStoreDirtyNodesCache cache)
        {
            _cache = cache;
            _storeByHash = cache._storeByHash;

            // Lock ordering is stable (0..N-1) to avoid deadlocks.
            for (int i = 0; i < ShardCount; i++)
            {
                Monitor.Enter(cache._locks[i]);
            }
        }

        public bool StoreByHash => _storeByHash;

        public void Dispose()
        {
            for (int i = ShardCount - 1; i >= 0; i--)
            {
                Monitor.Exit(_cache._locks[i]);
            }
        }
    }

    internal MapLock AcquireMapLock() => new MapLock(this);

    // Original TrieStore expects PruneCache(...) with these named args.
    public void PruneCache(bool prunePersisted, bool forceRemovePersistedNodes, IDictionary<HashAndTinyPath, Hash256?>? persistedHashes, INodeStorage? nodeStorage)
    {
        if (nodeStorage is null || persistedHashes is null)
        {
            // Compatibility: caller passed dontRemoveNodes => nodeStorage=null or persistedHashes absent.
            return;
        }

        // In this compatibility implementation, we do not attempt the full pruning semantics here.
        // We keep a conservative behavior: no-op unless full pruning is implemented.
    }

    // Overload used by TrieStore in some call sites.
    public void PruneCache(bool prunePersisted)
    {
        // No-op in compatibility implementation.
    }

    public bool CanDelete(in Key key, Hash256? currentlyPersistingKeccak)
    {
        if (currentlyPersistingKeccak is null) return false;
        if (currentlyPersistingKeccak == key.Keccak) return false;

        if (TryGetValue(in key, out TrieNode node) && !_trieStore.IsNoLongerNeeded(node))
        {
            return false;
        }

        return true;
    }

    // NOTE: There is no public delete API on INodeStorage. The original implementation could delete
    // cached nodes and rely on storage-specific behavior elsewhere. For compatibility builds we only
    // remove from the in-memory cache and (optionally) write empty data through the batch interface
    // if a batch is provided by the caller.
    private void DeleteCore(in Key key, INodeStorage nodeStorage, INodeStorage.IWriteBatch? writeBatcher)
    {
        Remove(in key);

        // If the caller provided a write batch, persist a "tombstone" as empty RLP payload.
        // This keeps the signature surface compatible without relying on a non-existent Delete API.
        if (writeBatcher is not null && key.Keccak is not null)
        {
            writeBatcher.Set(key.Address, key.Path, key.Keccak, ReadOnlySpan<byte>.Empty, WriteFlags.None);
        }
    }

    // Public 4-argument overload to satisfy call sites that call Delete with 4 parameters.
    public void Delete(in Key key, INodeStorage nodeStorage, INodeStorage.IWriteBatch? writeBatcher, TrieNode? node)
    {
        DeleteCore(in key, nodeStorage, writeBatcher);
    }

    public int PersistAll(INodeStorage nodeStorage, CancellationToken cancellationToken)
    {
        // Backward-compatible overload expected by TrieStore.
        Dictionary<Key, bool> wasPersisted = new();
        PersistAll(nodeStorage, cancellationToken, wasPersisted);
        return wasPersisted.Count;
    }

    public void PersistAll(
        INodeStorage nodeStorage,
        CancellationToken cancellationToken,
        IDictionary<Key, bool> wasPersisted)
    {
        // Snapshot current nodes to keep lock time bounded.
        List<TrieNode> nodes = new();
        List<Key> keys = new();

        if (_storeByHash)
        {
            for (int i = 0; i < ShardCount; i++)
            {
                lock (ShardLock(i))
                {
                    foreach (KeyValuePair<Hash256AsKey, TrieNode> pair in HashMap(i))
                    {
                        keys.Add(new Key(null, TreePath.Empty, pair.Key.Value));
                        nodes.Add(pair.Value);
                    }
                }
            }
        }
        else
        {
            for (int i = 0; i < ShardCount; i++)
            {
                lock (ShardLock(i))
                {
                    foreach (KeyValuePair<Key, TrieNode> pair in KeyMap(i))
                    {
                        keys.Add(pair.Key);
                        nodes.Add(pair.Value);
                    }
                }
            }
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TrieNode n = nodes[i];
            Key k = keys[i];

            if (n.Keccak is null) continue;
            if (n.NodeType == NodeType.Unknown) continue;

            if (wasPersisted.TryAdd(k, true))
            {
                nodeStorage.Set(k.Address, k.Path, n.Keccak, n.FullRlp.Span);
                n.IsPersisted = true;
            }
        }
    }
}
