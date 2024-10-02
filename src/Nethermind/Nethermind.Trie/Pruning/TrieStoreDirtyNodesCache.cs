// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Trie.Pruning;

internal class TrieStoreDirtyNodesCache
{
    private readonly TrieStore _trieStore;
    private int _count = 0;
    private readonly ILogger _logger;
    private readonly bool _storeByHash;
    private readonly ConcurrentDictionary<Key, TrieNode> _byKeyObjectCache;
    private readonly ConcurrentDictionary<Hash256AsKey, TrieNode> _byHashObjectCache;

    // Track some of the persisted path hash. Used to be able to remove keys when it is replaced.
    // If null, disable removing key.
    private readonly ClockCache<HashAndTinyPath, ValueHash256>? _pastPathHash;

    // Track ALL of the recently re-committed persisted nodes. This is so that we don't accidentally remove
    // recommitted persisted nodes (which will not get re-persisted).
    private readonly ConcurrentDictionary<HashAndTinyPathAndHash, long>? _persistedLastSeen;

    public readonly long KeyMemoryUsage;

    public TrieStoreDirtyNodesCache(TrieStore trieStore, int trackedPastKeyCount, bool storeByHash, ILogger logger)
    {
        _trieStore = trieStore;
        _logger = logger;
        // If the nodestore indicated that path is not required,
        // we will use a map with hash as its key instead of the full Key to reduce memory usage.
        _storeByHash = storeByHash;
        int initialBuckets = TrieStore.HashHelpers.GetPrime(Math.Max(31, Environment.ProcessorCount * 16));
        if (_storeByHash)
        {
            _byHashObjectCache = new(CollectionExtensions.LockPartitions, initialBuckets);
        }
        else
        {
            _byKeyObjectCache = new(CollectionExtensions.LockPartitions, initialBuckets);
        }
        KeyMemoryUsage = _storeByHash ? 0 : Key.MemoryUsage; // 0 because previously it was not counted.

        _persistedLastSeen = new(CollectionExtensions.LockPartitions, 4 * 4096);
        if (trackedPastKeyCount > 0 && !storeByHash)
        {
            _pastPathHash = new(trackedPastKeyCount);
        }
    }

    public void SaveInCache(in Key key, TrieNode node)
    {
        Debug.Assert(node.Keccak is not null, "Cannot store in cache nodes without resolved key.");
        if (TryAdd(key, node))
        {
            Metrics.CachedNodesCount = Interlocked.Increment(ref _count);
            _trieStore.MemoryUsedByDirtyCache += node.GetMemorySize(false) + KeyMemoryUsage;
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
            trieNode.ResolveNode(_trieStore.GetTrieStore(key.AddressAsHash256), key.Path);
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
                    pair => new KeyValuePair<Key, TrieNode>(new Key(null, TreePath.Empty, pair.Key.Value), pair.Value));
            }

            return _byKeyObjectCache;
        }
    }

    public bool TryGetValue(in Key key, out TrieNode node)
    {
        if (_storeByHash)
        {
            return _byHashObjectCache.TryGetValue(key.Keccak, out node);
        }
        return _byKeyObjectCache.TryGetValue(key, out node);
    }

    public bool TryAdd(in Key key, TrieNode node)
    {
        if (_storeByHash)
        {
            return _byHashObjectCache.TryAdd(key.Keccak, node);
        }
        return _byKeyObjectCache.TryAdd(key, node);
    }

    public void Remove(in Key key)
    {
        if (_storeByHash)
        {
            if (_byHashObjectCache.Remove(key.Keccak, out _))
            {
                Metrics.CachedNodesCount = Interlocked.Decrement(ref _count);
            }

            return;
        }
        if (_byKeyObjectCache.Remove<Key, TrieNode>(key, out _))
        {
            Metrics.CachedNodesCount = Interlocked.Decrement(ref _count);
        }
    }

    public MapLock AcquireMapLock()
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

    public int Count => _count;
    public ConcurrentDictionary<HashAndTinyPathAndHash, long>? PersistedLastSeen => _persistedLastSeen;
    public ClockCache<HashAndTinyPath, ValueHash256>? PastPathHash => _pastPathHash;

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
        Metrics.CachedNodesCount = 0;
        _trieStore.MemoryUsedByDirtyCache = 0;
    }

    internal readonly struct Key : IEquatable<Key>
    {
        internal const long MemoryUsage = 8 + 36 + 8; // (address (probably shared), path, keccak pointer (shared with TrieNode))
        public readonly ValueHash256 Address;
        public Hash256? AddressAsHash256 => Address == default ? null : Address.ToCommitment();
        // Direct member rather than property for large struct, so members are called directly,
        // rather than struct copy through the property. Could also return a ref through property.
        public readonly TreePath Path;
        public Hash256 Keccak { get; }

        public Key(Hash256? address, in TreePath path, Hash256 keccak)
        {
            Address = address ?? default;
            Path = path;
            Keccak = keccak;
        }
        public Key(in ValueHash256 address, in TreePath path, Hash256 keccak)
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
