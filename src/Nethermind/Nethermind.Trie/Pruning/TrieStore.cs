// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Trie store helps to manage trie commits block by block.
    /// If persistence and pruning are needed they have a chance to execute their behaviour on commits.
    /// </summary>
    public class TrieStore : ITrieStore, IPruningTrieStore
    {
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

            /// <summary>
            /// This method is responsible for reviewing the nodes that are directly in the cache and
            /// removing ones that are either no longer referenced or already persisted.
            /// </summary>
            /// <exception cref="InvalidOperationException"></exception>
            public long PruneCache(bool skipRecalculateMemory = false)
            {
                bool shouldTrackPersistedNode = _pastPathHash is not null && !_trieStore.IsCurrentlyFullPruning;
                long newMemory = 0;

                foreach ((Key key, TrieNode node) in AllNodes)
                {
                    if (node.IsPersisted)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Removing persisted {node} from memory.");

                        if (shouldTrackPersistedNode)
                        {
                            TrackPersistedNode(key, node);
                        }

                        Hash256? keccak = node.Keccak;
                        if (keccak is null)
                        {
                            TreePath path2 = key.Path;
                            keccak = node.GenerateKey(_trieStore.GetTrieStore(key.AddressAsHash256), ref path2, isRoot: true);
                            if (keccak != key.Keccak)
                            {
                                throw new InvalidOperationException($"Persisted {node} {key} != {keccak}");
                            }

                            node.Keccak = keccak;
                        }
                        Remove(key);

                        Metrics.PrunedPersistedNodesCount++;
                    }
                    else if (_trieStore.IsNoLongerNeeded(node))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Removing {node} from memory (no longer referenced).");
                        if (node.Keccak is null)
                        {
                            throw new InvalidOperationException($"Removed {node}");
                        }
                        Remove(key);

                        Metrics.PrunedTransientNodesCount++;
                    }
                    else if (!skipRecalculateMemory)
                    {
                        node.PrunePersistedRecursively(1);
                        newMemory += node.GetMemorySize(false) + KeyMemoryUsage;
                    }
                }

                return newMemory + (_persistedLastSeen?.Count ?? 0) * 48;

                void TrackPersistedNode(in TrieStoreDirtyNodesCache.Key key, TrieNode node)
                {
                    if (key.Path.Length > TinyTreePath.MaxNibbleLength) return;
                    TinyTreePath treePath = new(key.Path);
                    // Persisted node with LastSeen is a node that has been re-committed, likely due to processing
                    // recalculated to the same hash.
                    if (node.LastSeen >= 0)
                    {
                        // Update _persistedLastSeen to later value.
                        _persistedLastSeen.AddOrUpdate(
                            new(key.Address, in treePath, key.Keccak),
                            (_, newValue) => newValue,
                            (_, newValue, currentLastSeen) => Math.Max(newValue, currentLastSeen),
                            node.LastSeen);
                    }

                    // This persisted node is being removed from cache. Keep it in mind in case of an update to the same
                    // path.
                    _pastPathHash.Set(new(key.Address, in treePath), key.Keccak);
                }
            }


            public void RemovePastKeys(ConcurrentDictionary<HashAndTinyPath, Hash256?>? persistedHashes, INodeStorage nodeStorage)
            {
                if (persistedHashes is null) return;

                bool CanRemove(in ValueHash256 address, TinyTreePath path, in TreePath fullPath, in ValueHash256 keccak, Hash256? currentlyPersistingKeccak)
                {
                    // Multiple current hash that we don't keep track for simplicity. Just ignore this case.
                    if (currentlyPersistingKeccak is null) return false;

                    // The persisted hash is the same as currently persisting hash. Do nothing.
                    if ((ValueHash256)currentlyPersistingKeccak == keccak) return false;

                    // We have it in cache and it is still needed.
                    if (TryGetValue(new TrieStoreDirtyNodesCache.Key(address, fullPath, keccak.ToCommitment()), out TrieNode node) &&
                        !_trieStore.IsNoLongerNeeded(node)) return false;

                    // We don't have it in cache, but we know it was re-committed, so if it is still needed, don't remove
                    if (_persistedLastSeen.TryGetValue(new(address, in path, in keccak), out long commitBlock) &&
                        !_trieStore.IsNoLongerNeeded(commitBlock)) return false;

                    return true;
                }

                INodeStorage.WriteBatch writeBatch = nodeStorage.StartWriteBatch();
                try
                {
                    int round = 0;
                    foreach (KeyValuePair<HashAndTinyPath, Hash256> keyValuePair in persistedHashes)
                    {
                        HashAndTinyPath key = keyValuePair.Key;
                        if (_pastPathHash.TryGet(key, out ValueHash256 prevHash))
                        {
                            TreePath fullPath = key.path.ToTreePath(); // Micro op to reduce double convert
                            if (CanRemove(key.addr, key.path, fullPath, prevHash, keyValuePair.Value))
                            {
                                Metrics.RemovedNodeCount++;
                                Hash256? address = key.addr == default ? null : key.addr.ToCommitment();
                                writeBatch.Set(address, fullPath, prevHash, default, WriteFlags.DisableWAL);
                                round++;
                            }
                        }

                        // Batches of 256
                        if (round > 256)
                        {
                            writeBatch.Dispose();
                            writeBatch = nodeStorage.StartWriteBatch();
                            round = 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Failed to remove past keys. {ex}");
                }
                finally
                {
                    writeBatch.Dispose();
                    nodeStorage.Compact();
                }
            }

            public void CleanObsoletePersistedLastSeen()
            {
                var persistedLastSeen = _persistedLastSeen;
                foreach (KeyValuePair<HashAndTinyPathAndHash, long> keyValuePair in persistedLastSeen)
                {
                    if (_trieStore.IsNoLongerNeeded(keyValuePair.Value))
                    {
                        persistedLastSeen.Remove(keyValuePair.Key, out _);
                    }
                }
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

            public void ClearLivePruningTracking()
            {
                _persistedLastSeen.NoResizeClear();
                _pastPathHash?.Clear();
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

        private const int ShardedDirtyNodeCount = 256;

        private int _isFirst;

        private INodeStorage.WriteBatch? _currentBatch = null;

        private TrieStoreDirtyNodesCache[] _dirtyNodes = Array.Empty<TrieStoreDirtyNodesCache>();

        // This seems to attempt prevent multiple block processing at the same time and along with pruning at the same time.
        private object _dirtyNodesLock = new object();

        // This seems to attempt to prevent the dirty nodes from being mutated by other thread at the same time.
        private object _dirtyNodesWriteLock = new object();

        private bool _livePruningEnabled = false;

        private bool _lastPersistedReachedReorgBoundary;
        private Task _pruningTask = Task.CompletedTask;
        private readonly CancellationTokenSource _pruningTaskCancellationTokenSource = new();

        public TrieStore(IKeyValueStoreWithBatching? keyValueStore, ILogManager? logManager)
            : this(keyValueStore, No.Pruning, Pruning.Persist.EveryBlock, logManager)
        {
        }

        public TrieStore(INodeStorage nodeStorage, ILogManager? logManager)
            : this(nodeStorage, No.Pruning, Pruning.Persist.EveryBlock, logManager)
        {
        }

        public TrieStore(
            IKeyValueStoreWithBatching? keyValueStore,
            IPruningStrategy? pruningStrategy,
            IPersistenceStrategy? persistenceStrategy,
            ILogManager? logManager) : this(new NodeStorage(keyValueStore), pruningStrategy, persistenceStrategy, logManager)
        {
        }

        public TrieStore(
            INodeStorage? nodeStorage,
            IPruningStrategy? pruningStrategy,
            IPersistenceStrategy? persistenceStrategy,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<TrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _nodeStorage = nodeStorage ?? throw new ArgumentNullException(nameof(nodeStorage));
            _pruningStrategy = pruningStrategy ?? throw new ArgumentNullException(nameof(pruningStrategy));
            _persistenceStrategy = persistenceStrategy ?? throw new ArgumentNullException(nameof(persistenceStrategy));
            _publicStore = new TrieKeyValueStore(this);

            if (pruningStrategy.PruningEnabled)
            {
                if (!nodeStorage.RequirePath)
                {
                    // Hash layout.
                    // All address and path is gonna be null patricia trie is not recording it..... I think.
                    _dirtyNodes = [
                        new TrieStoreDirtyNodesCache(this, _pruningStrategy.TrackedPastKeyCount, !_nodeStorage.RequirePath, _logger)
                    ];
                }
                else
                {
                    _dirtyNodes = new TrieStoreDirtyNodesCache[ShardedDirtyNodeCount];
                    for (int i = 0; i < ShardedDirtyNodeCount; i++)
                    {
                        _dirtyNodes[i] = new TrieStoreDirtyNodesCache(this, _pruningStrategy.TrackedPastKeyCount / ShardedDirtyNodeCount, !_nodeStorage.RequirePath, _logger);
                    }
                }
            }

            if (pruningStrategy.PruningEnabled && pruningStrategy.TrackedPastKeyCount > 0 && nodeStorage.RequirePath)
            {
                _livePruningEnabled = true;
            }
        }

        public IScopedTrieStore GetTrieStore(Hash256? address)
        {
            return new ScopedTrieStore(this, address);
        }

        public long LastPersistedBlockNumber
        {
            get => _latestPersistedBlockNumber;
            private set
            {
                if (value != _latestPersistedBlockNumber)
                {
                    Metrics.LastPersistedBlockNumber = value;
                    _latestPersistedBlockNumber = value;
                    _lastPersistedReachedReorgBoundary = false;
                }
            }
        }

        public long MemoryUsedByDirtyCache
        {
            get => _memoryUsedByDirtyCache;
            set
            {
                Metrics.MemoryUsedByCache = value;
                _memoryUsedByDirtyCache = value;
            }
        }

        public int CommittedNodesCount
        {
            get => _committedNodesCount;
            private set
            {
                Metrics.CommittedNodesCount = value;
                _committedNodesCount = value;
            }
        }

        public int PersistedNodesCount
        {
            get => _persistedNodesCount;
            private set
            {
                Metrics.PersistedNodeCount = value;
                _persistedNodesCount = value;
            }
        }

        public int CachedNodesCount
        {
            get
            {
                int count = DirtyNodesCount();
                Metrics.CachedNodesCount = count;
                return count;
            }
        }

        public void CommitNode(long blockNumber, Hash256? address, in NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
            EnsureCommitSetExistsForBlock(blockNumber);

            if (_logger.IsTrace) Trace(blockNumber, in nodeCommitInfo);
            if (!nodeCommitInfo.IsEmptyBlockMarker && !nodeCommitInfo.Node.IsBoundaryProofNode)
            {
                TrieNode node = nodeCommitInfo.Node!;

                if (node!.Keccak is null)
                {
                    ThrowUnknownHash(node);
                }

                if (CurrentPackage is null)
                {
                    ThrowUnknownPackage(blockNumber, node);
                }

                if (node!.LastSeen >= 0)
                {
                    ThrowNodeHasBeenSeen(blockNumber, node);
                }

                node = SaveOrReplaceInDirtyNodesCache(address, nodeCommitInfo, node);
                node.LastSeen = Math.Max(blockNumber, node.LastSeen);

                if (!_pruningStrategy.PruningEnabled)
                {
                    PersistNode(address, nodeCommitInfo.Path, node, blockNumber, writeFlags);
                }

                CommittedNodesCount++;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(long blockNumber, in NodeCommitInfo nodeCommitInfo)
            {
                _logger.Trace($"Committing {nodeCommitInfo} at {blockNumber}");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowUnknownHash(TrieNode node)
            {
                throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowUnknownPackage(long blockNumber, TrieNode node)
            {
                throw new TrieStoreException($"{nameof(CurrentPackage)} is NULL when committing {node} at {blockNumber}.");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowNodeHasBeenSeen(long blockNumber, TrieNode node)
            {
                throw new TrieStoreException($"{nameof(TrieNode.LastSeen)} set on {node} committed at {blockNumber}.");
            }
        }

        private int GetNodeShardIdx(Hash256? address, in TreePath path)
        {
            if (_dirtyNodes.Length == 1) return 0;
            return path.Path.Bytes[0];
        }

        private int GetNodeShardIdx(in TrieStoreDirtyNodesCache.Key key)
        {
            return GetNodeShardIdx(key.Address, key.Path);
        }

        private TrieStoreDirtyNodesCache GetDirtyNodeShard(in TrieStoreDirtyNodesCache.Key key)
        {
            return _dirtyNodes[GetNodeShardIdx(key)];
        }

        private int DirtyNodesCount()
        {
            int count = 0;
            foreach (TrieStoreDirtyNodesCache dirtyNode in _dirtyNodes)
            {
                count += dirtyNode.Count;
            }
            return count;
        }

        private bool DirtyNodesTryGetValue(in TrieStoreDirtyNodesCache.Key key, out TrieNode node)
        {
            TrieStoreDirtyNodesCache cache = GetDirtyNodeShard(key);
            return cache.TryGetValue(key, out node);
        }

        private void DirtyNodesSaveInCache(in TrieStoreDirtyNodesCache.Key key, TrieNode node)
        {
            TrieStoreDirtyNodesCache cache = GetDirtyNodeShard(key);
            cache.SaveInCache(key, node);
        }

        private bool DirtyNodesIsNodeCached(TrieStoreDirtyNodesCache.Key key)
        {
            TrieStoreDirtyNodesCache cache = GetDirtyNodeShard(key);
            return cache.IsNodeCached(key);
        }

        private TrieNode DirtyNodesFromCachedRlpOrUnknown(TrieStoreDirtyNodesCache.Key key)
        {
            TrieStoreDirtyNodesCache cache = GetDirtyNodeShard(key);
            return cache.FromCachedRlpOrUnknown(key);
        }

        private TrieNode DirtyNodesFindCachedOrUnknown(TrieStoreDirtyNodesCache.Key key)
        {
            TrieStoreDirtyNodesCache cache = GetDirtyNodeShard(key);
            return cache.FindCachedOrUnknown(key);
        }

        private TrieNode SaveOrReplaceInDirtyNodesCache(Hash256? address, NodeCommitInfo nodeCommitInfo, TrieNode node)
        {
            if (_pruningStrategy.PruningEnabled)
            {
                TrieStoreDirtyNodesCache.Key key = new TrieStoreDirtyNodesCache.Key(address, nodeCommitInfo.Path, node.Keccak);
                if (DirtyNodesTryGetValue(in key, out TrieNode cachedNodeCopy))
                {
                    Metrics.LoadedFromCacheNodesCount++;
                    if (!ReferenceEquals(cachedNodeCopy, node))
                    {
                        if (_logger.IsTrace) Trace(node, cachedNodeCopy);
                        TreePath path = nodeCommitInfo.Path;
                        cachedNodeCopy.ResolveKey(GetTrieStore(address), ref path, nodeCommitInfo.IsRoot);
                        if (node.Keccak != cachedNodeCopy.Keccak)
                        {
                            ThrowNodeIsNotSame(node, cachedNodeCopy);
                        }

                        if (!nodeCommitInfo.IsRoot)
                        {
                            nodeCommitInfo.NodeParent!.ReplaceChildRef(nodeCommitInfo.ChildPositionAtParent, cachedNodeCopy);
                        }

                        node = cachedNodeCopy;
                        Metrics.ReplacedNodesCount++;
                    }
                }
                else
                {
                    DirtyNodesSaveInCache(key, node);
                }
            }

            return node;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(TrieNode node, TrieNode cachedNodeCopy)
            {
                _logger.Trace($"Replacing {node} with its cached copy {cachedNodeCopy}.");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowNodeIsNotSame(TrieNode node, TrieNode cachedNodeCopy)
            {
                throw new InvalidOperationException($"The hash of replacement node {cachedNodeCopy} is not the same as the original {node}.");
            }

        }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, Hash256? address, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
            EnsureCommitSetExistsForBlock(blockNumber);

            try
            {
                if (trieType == TrieType.State) // storage tries happen before state commits
                {
                    if (_logger.IsTrace) _logger.Trace($"Enqueued blocks {_commitSetQueue?.Count ?? 0}");
                    BlockCommitSet set = CurrentPackage;
                    if (set is not null)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Current root (block {blockNumber}): {root}, block {set.BlockNumber}");
                        set.Seal(root);
                    }

                    bool shouldPersistSnapshot = _persistenceStrategy.ShouldPersist(set.BlockNumber);
                    if (shouldPersistSnapshot)
                    {
                        _currentBatch ??= _nodeStorage.StartWriteBatch();
                        try
                        {
                            PersistBlockCommitSet(address, set, _currentBatch, writeFlags: writeFlags);
                            PruneCurrentSet();
                        }
                        finally
                        {
                            // For safety we prefer to commit half of the batch rather than not commit at all.
                            // Generally hanging nodes are not a problem in the DB but anything missing from the DB is.
                            _currentBatch?.Dispose();
                            _currentBatch = null;
                        }
                    }
                    else
                    {
                        PruneCurrentSet();
                    }

                    CurrentPackage = null;
                    if (_pruningStrategy.PruningEnabled && Monitor.IsEntered(_dirtyNodesLock))
                    {
                        Monitor.Exit(_dirtyNodesLock);
                    }
                }
            }
            finally
            {
                _currentBatch?.Dispose();
                _currentBatch = null;
            }

            Prune();
        }

        public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 keccak, INodeStorage? nodeStorage, ReadFlags readFlags = ReadFlags.None)
        {
            nodeStorage ??= _nodeStorage;
            byte[]? rlp = nodeStorage.Get(address, path, keccak, readFlags);

            if (rlp is not null)
            {
                Metrics.LoadedFromDbNodesCount++;
            }

            return rlp;
        }


        public byte[] LoadRlp(Hash256? address, in TreePath path, Hash256 keccak, INodeStorage? nodeStorage, ReadFlags readFlags = ReadFlags.None)
        {
            byte[]? rlp = TryLoadRlp(address, path, keccak, nodeStorage, readFlags);
            if (rlp is null)
            {
                ThrowMissingNode(keccak);
            }

            return rlp;

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowMissingNode(Hash256 keccak)
            {
                throw new TrieNodeException($"Node {keccak} is missing from the DB", keccak);
            }
        }

        public virtual byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => LoadRlp(address, path, hash, null, flags);
        public virtual byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => TryLoadRlp(address, path, hash, null, flags);

        public virtual bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak)
        {
            byte[]? rlp = _nodeStorage.Get(address, path, keccak, ReadFlags.None);

            if (rlp is null)
            {
                return false;
            }

            Metrics.LoadedFromDbNodesCount++;

            return true;
        }

        public IReadOnlyTrieStore AsReadOnly(INodeStorage? store) =>
            new ReadOnlyTrieStore(this, store);

        public bool IsNodeCached(Hash256? address, in TreePath path, Hash256? hash) => DirtyNodesIsNodeCached(new TrieStoreDirtyNodesCache.Key(address, path, hash));

        public virtual TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256? hash) =>
            FindCachedOrUnknown(address, path, hash, false);

        internal TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256? hash, bool isReadOnly)
        {
            ArgumentNullException.ThrowIfNull(hash);

            if (!_pruningStrategy.PruningEnabled)
            {
                return new TrieNode(NodeType.Unknown, hash);
            }

            TrieStoreDirtyNodesCache.Key key = new TrieStoreDirtyNodesCache.Key(address, path, hash);
            return FindCachedOrUnknown(key, isReadOnly);
        }

        private TrieNode FindCachedOrUnknown(TrieStoreDirtyNodesCache.Key key, bool isReadOnly)
        {
            return isReadOnly ? DirtyNodesFromCachedRlpOrUnknown(key) : DirtyNodesFindCachedOrUnknown(key);
        }

        // Used only in tests
        public void Dump()
        {
            foreach (TrieStoreDirtyNodesCache? dirtyNode in _dirtyNodes)
            {
                dirtyNode.Dump();
            }
        }

        public void Prune()
        {
            if (_pruningStrategy.ShouldPrune(MemoryUsedByDirtyCache) && _pruningTask.IsCompleted)
            {
                _pruningTask = Task.Run(() =>
                {
                    try
                    {
                        // Flush ahead of time so that memtable is empty which prevent stalling when writing nodes.
                        // Note, the WriteBufferSize * WriteBufferNumber need to be more than about 20% of pruning cache
                        // otherwise, it may not fit the whole dirty cache.
                        // Additionally, if (WriteBufferSize * (WriteBufferNumber - 1)) is already more than 20% of pruning
                        // cache, it is likely that there are enough space for it on most time, except for syncing maybe.
                        _nodeStorage.Flush();
                        lock (_dirtyNodesLock)
                        {
                            lock (_dirtyNodesWriteLock)
                            {
                                Stopwatch sw = Stopwatch.StartNew();
                                if (_logger.IsDebug) _logger.Debug($"Locked {nameof(TrieStore)} for pruning.");

                                long memoryUsedByDirtyCache = MemoryUsedByDirtyCache;
                                if (!_pruningTaskCancellationTokenSource.IsCancellationRequested && _pruningStrategy.ShouldPrune(memoryUsedByDirtyCache))
                                {
                                    // Most of the time in memory pruning is on `PrunePersistedRecursively`. So its
                                    // usually faster to just SaveSnapshot causing most of the entry to be persisted.
                                    // Not saving snapshot just save about 5% of memory at most of the time, causing
                                    // an elevated pruning a few blocks after making it not very effective especially
                                    // on constant block processing such as during forward sync where it can take up to
                                    // 30% of the total time on halfpath as the block processing portion got faster.
                                    //
                                    // With halfpath's live pruning, there is a slight complication, the currently loaded
                                    // persisted node have a pretty good hit rate and tend to conflict with the persisted
                                    // nodes (address,path) entry on second PruneCache. So pruning them ahead of time
                                    // really helps increase nodes that can be removed.
                                    PruneCache(skipRecalculateMemory: true);

                                    SaveSnapshot();

                                    PruneCache();

                                    Metrics.PruningTime = sw.ElapsedMilliseconds;
                                    if (_logger.IsInfo) _logger.Info($"Executed memory prune. Took {sw.Elapsed.TotalSeconds:0.##} seconds. From {memoryUsedByDirtyCache / 1.MiB()}MB to {MemoryUsedByDirtyCache / 1.MiB()}MB");
                                }
                            }
                        }

                        if (_logger.IsDebug) _logger.Debug($"Pruning finished. Unlocked {nameof(TrieStore)}.");
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsError) _logger.Error("Pruning failed with exception.", e);
                    }
                });
            }
        }

        private bool SaveSnapshot()
        {
            if (_pruningStrategy.ShouldPrune(MemoryUsedByDirtyCache))
            {
                if (_logger.IsDebug) _logger.Debug("Elevated pruning starting");

                int count = _commitSetQueue?.Count ?? 0;
                if (count == 0) return false;

                using ArrayPoolList<BlockCommitSet> toAddBack = new(count);
                using ArrayPoolList<BlockCommitSet> candidateSets = new(count);
                while (_commitSetQueue.TryDequeue(out BlockCommitSet frontSet))
                {
                    if (frontSet!.BlockNumber >= LatestCommittedBlockNumber - _pruningStrategy.MaxDepth)
                    {
                        toAddBack.Add(frontSet);
                    }
                    else if (candidateSets.Count > 0 && candidateSets[0].BlockNumber == frontSet.BlockNumber)
                    {
                        candidateSets.Add(frontSet);
                    }
                    else if (candidateSets.Count == 0 || frontSet.BlockNumber > candidateSets[0].BlockNumber)
                    {
                        candidateSets.Clear();
                        candidateSets.Add(frontSet);
                    }
                }

                // TODO: Find a way to not have to re-add everything
                for (int index = 0; index < toAddBack.Count; index++)
                {
                    _commitSetQueue.Enqueue(toAddBack[index]);
                }


                bool shouldDeletePersistedNode =
                    // Its disabled
                    _livePruningEnabled &&
                    // Full pruning need to visit all node, so can't delete anything.
                    !_persistenceStrategy.IsFullPruning &&
                    // If more than one candidate set, its a reorg, we can't remove node as persisted node may not be canonical
                    candidateSets.Count == 1;

                ConcurrentDictionary<HashAndTinyPath, Hash256?>[]? persistedHashes = null;
                Action<TreePath, Hash256?, TrieNode>? persistedNodeRecorder = null;

                if (shouldDeletePersistedNode)
                {
                    persistedHashes = new ConcurrentDictionary<HashAndTinyPath, Hash256?>[ShardedDirtyNodeCount];
                    for (int i = 0; i < ShardedDirtyNodeCount; i++)
                    {
                        persistedHashes[i] = new ConcurrentDictionary<HashAndTinyPath, Hash256>();
                    }

                    persistedNodeRecorder = (TreePath treePath, Hash256? address, TrieNode tn) =>
                    {
                        if (persistedHashes is not null && treePath.Length <= TinyTreePath.MaxNibbleLength)
                        {
                            int shardIdx = GetNodeShardIdx(address, treePath);

                            HashAndTinyPath key = new(address, new TinyTreePath(treePath));

                            persistedHashes[shardIdx].AddOrUpdate(key, ((path) => tn.Keccak), (((path, hash256) => null)));
                        }
                    };
                }

                for (int index = 0; index < candidateSets.Count; index++)
                {
                    BlockCommitSet blockCommitSet = candidateSets[index];
                    if (_logger.IsDebug) _logger.Debug($"Elevated pruning for candidate {blockCommitSet.BlockNumber}");
                    ParallelPersistBlockCommitSet(null, blockCommitSet, persistedNodeRecorder);
                }

                Task deleteTask = Task.WhenAll(_dirtyNodes.Select((dirtyNode, idx) => Task.Run(() =>
                {
                    if (persistedHashes == null) return;
                    dirtyNode.RemovePastKeys(persistedHashes[idx], _nodeStorage);
                })));

                AnnounceReorgBoundaries();
                deleteTask.Wait();

                if (_livePruningEnabled)
                {
                    foreach (TrieStoreDirtyNodesCache dirtyNode in _dirtyNodes)
                    {
                        dirtyNode.CleanObsoletePersistedLastSeen();
                    }
                }

                if (candidateSets.Count > 0)
                {
                    return true;
                }

                _commitSetQueue.TryPeek(out BlockCommitSet? uselessFrontSet);
                if (_logger.IsDebug) _logger.Debug($"Found no candidate for elevated pruning (sets: {_commitSetQueue.Count}, earliest: {uselessFrontSet?.BlockNumber}, newest kept: {LatestCommittedBlockNumber}, reorg depth {_pruningStrategy.MaxDepth})");
            }

            return false;
        }

        /// <summary>
        /// Prunes persisted branches of the current commit set root.
        /// </summary>
        private void PruneCurrentSet()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            // We assume that the most recent package very likely resolved many persisted nodes and only replaced
            // some top level branches. Any of these persisted nodes are held in cache now so we just prune them here
            // to avoid the references still being held after we prune the cache.
            // We prune them here but just up to two levels deep which makes it a very lightweight operation.
            // Note that currently the TrieNode ResolveChild un-resolves any persisted child immediately which
            // may make this call unnecessary.
            CurrentPackage?.Root?.PrunePersistedRecursively(2);
            stopwatch.Stop();
            Metrics.DeepPruningTime = stopwatch.ElapsedMilliseconds;
        }

        /// <summary>
        /// This method is responsible for reviewing the nodes that are directly in the cache and
        /// removing ones that are either no longer referenced or already persisted.
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        private void PruneCache(bool skipRecalculateMemory = false)
        {
            if (_logger.IsDebug) _logger.Debug($"Pruning nodes {MemoryUsedByDirtyCache / 1.MB()} MB , last persisted block: {LastPersistedBlockNumber} current: {LatestCommittedBlockNumber}.");
            Stopwatch stopwatch = Stopwatch.StartNew();

            long newMemory = 0;
            Task.WaitAll(_dirtyNodes.Select((dirtyNode) => Task.Run(() =>
            {
                using (dirtyNode.AcquireMapLock())
                {
                    long shardSize = dirtyNode.PruneCache(skipRecalculateMemory);
                    Interlocked.Add(ref newMemory, shardSize);
                }
            })).ToArray());

            if (!skipRecalculateMemory) MemoryUsedByDirtyCache = newMemory;
            _ = CachedNodesCount; // Setter also update the count

            stopwatch.Stop();
            if (_logger.IsDebug) _logger.Debug($"Finished pruning nodes in {stopwatch.ElapsedMilliseconds}ms {MemoryUsedByDirtyCache / 1.MB()} MB, last persisted block: {LastPersistedBlockNumber} current: {LatestCommittedBlockNumber}.");
        }

        /// <summary>
        /// This method is here to support testing.
        /// </summary>
        public void ClearCache()
        {
            foreach (TrieStoreDirtyNodesCache dirtyNode in _dirtyNodes)
            {
                dirtyNode.Clear();
            }
        }

        public void Dispose()
        {
            if (_logger.IsDebug) _logger.Debug("Disposing trie");
            _pruningTaskCancellationTokenSource.Cancel();
            _pruningTask.Wait();
            PersistOnShutdown();
        }

        public void WaitForPruning()
        {
            _pruningTask.Wait();
        }

        protected readonly INodeStorage _nodeStorage;

        private readonly TrieKeyValueStore _publicStore;

        private readonly IPruningStrategy _pruningStrategy;

        private readonly IPersistenceStrategy _persistenceStrategy;

        private readonly ILogger _logger;

        private ConcurrentQueue<BlockCommitSet> _commitSetQueue;

        private long _memoryUsedByDirtyCache;

        private int _committedNodesCount;

        private int _persistedNodesCount;

        private long _latestPersistedBlockNumber;

        private BlockCommitSet? CurrentPackage { get; set; }

        private bool IsCurrentListSealed => CurrentPackage is null || CurrentPackage.IsSealed;

        private long LatestCommittedBlockNumber { get; set; }
        public INodeStorage.KeyScheme Scheme => _nodeStorage.Scheme;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ConcurrentQueue<BlockCommitSet> CreateQueueAtomic(ref ConcurrentQueue<BlockCommitSet> val)
        {
            ConcurrentQueue<BlockCommitSet> instance = new();
            ConcurrentQueue<BlockCommitSet>? prior = Interlocked.CompareExchange(ref val, instance, null);
            return prior ?? instance;
        }

        private void CreateCommitSet(long blockNumber)
        {
            if (_logger.IsDebug) _logger.Debug($"Beginning new {nameof(BlockCommitSet)} - {blockNumber}");

            // TODO: this throws on reorgs, does it not? let us recreate it in test
            Debug.Assert(CurrentPackage is null || blockNumber == CurrentPackage.BlockNumber + 1, "Newly begun block is not a successor of the last one");
            Debug.Assert(IsCurrentListSealed, "Not sealed when beginning new block");

            BlockCommitSet commitSet = new(blockNumber);
            (_commitSetQueue ?? CreateQueueAtomic(ref _commitSetQueue)).Enqueue(commitSet);
            LatestCommittedBlockNumber = Math.Max(blockNumber, LatestCommittedBlockNumber);
            AnnounceReorgBoundaries();
            DequeueOldCommitSets();

            CurrentPackage = commitSet;
            Debug.Assert(ReferenceEquals(CurrentPackage, commitSet), $"Current {nameof(BlockCommitSet)} is not same as the new package just after adding");
        }

        /// <summary>
        /// Persists all transient (not yet persisted) starting from <paramref name="commitSet"/> root.
        /// Already persisted nodes are skipped. After this action we are sure that the full state is available
        /// for the block represented by this commit set.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="commitSet">A commit set of a block which root is to be persisted.</param>
        /// <param name="writeBatch">The write batch to write to</param>
        /// <param name="persistedHashes">Track persisted hashes in this dictionary if not null</param>
        /// <param name="writeFlags"></param>
        private void PersistBlockCommitSet(
            Hash256? address,
            BlockCommitSet commitSet,
            INodeStorage.WriteBatch writeBatch,
            Action<TreePath, Hash256?, TrieNode>? persistedNodeRecorder = null,
            WriteFlags writeFlags = WriteFlags.None
        )
        {
            void PersistNode(TrieNode tn, Hash256? address2, TreePath path)
            {
                persistedNodeRecorder?.Invoke(path, address2, tn);
                this.PersistNode(address2, path, tn, commitSet.BlockNumber, writeFlags, writeBatch);
            }

            if (_logger.IsDebug) _logger.Debug($"Persisting from root {commitSet.Root} in {commitSet.BlockNumber}");

            Stopwatch stopwatch = Stopwatch.StartNew();
            TreePath path = TreePath.Empty;
            commitSet.Root?.CallRecursively(PersistNode, address, ref path, GetTrieStore(null), true, _logger);
            stopwatch.Stop();
            Metrics.SnapshotPersistenceTime = stopwatch.ElapsedMilliseconds;

            if (_logger.IsDebug) _logger.Debug($"Persisted trie from {commitSet.Root} at {commitSet.BlockNumber} in {stopwatch.ElapsedMilliseconds}ms (cache memory {MemoryUsedByDirtyCache})");

            LastPersistedBlockNumber = commitSet.BlockNumber;
        }

        private void ParallelPersistBlockCommitSet(
            Hash256? address,
            BlockCommitSet commitSet,
            Action<TreePath, Hash256?, TrieNode>? persistedNodeRecorder = null,
            WriteFlags writeFlags = WriteFlags.None
        )
        {
            INodeStorage.WriteBatch topLevelWriteBatch = _nodeStorage.StartWriteBatch();
            int parallelBoundaryPathLength = 2;

            List<(TrieNode trieNode, Hash256? address2, TreePath path)> parallelStartNodes = new();

            void TopLevelPersist(TrieNode tn, Hash256? address2, TreePath path)
            {
                if (path.Length < parallelBoundaryPathLength)
                {
                    persistedNodeRecorder?.Invoke(path, address2, tn);
                    this.PersistNode(address2, path, tn, commitSet.BlockNumber, writeFlags, topLevelWriteBatch);
                }
                else
                {
                    parallelStartNodes.Add((tn, address2, path));
                }
            }

            if (_logger.IsDebug) _logger.Debug($"Persisting from root {commitSet.Root} in {commitSet.BlockNumber}");

            Stopwatch stopwatch = Stopwatch.StartNew();

            // The first CallRecursive stop at two level, yielding 256 node in parallelStartNodes, which is run concurrently
            TreePath path = TreePath.Empty;
            commitSet.Root?.CallRecursively(TopLevelPersist, address, ref path, GetTrieStore(null), true, _logger, maxPathLength: parallelBoundaryPathLength);

            // The amount of change in the subtrees are not balanced at all. So their writes ares buffered here
            // which get disposed in parallel instead of being disposed in `PersistNodeStartingFrom`.
            BlockingCollection<INodeStorage.WriteBatch> disposeQueue = new BlockingCollection<INodeStorage.WriteBatch>(4);

            Task disposeTask = Task.WhenAll(Enumerable.Range(0, Environment.ProcessorCount).Select((_) => Task.Run(() =>
            {
                while (disposeQueue.TryTake(out INodeStorage.WriteBatch disposable, Timeout.Infinite))
                {
                    disposable.Dispose();
                }
            })).ToArray());

            Task.WaitAll(parallelStartNodes.Select((entry) => Task.Run(() =>
            {
                (TrieNode trieNode, Hash256? address2, TreePath path2) = entry;
                PersistNodeStartingFrom(trieNode, address2, path2, commitSet, persistedNodeRecorder, writeFlags, disposeQueue);
            })).ToArray());

            disposeQueue.CompleteAdding();
            disposeTask.Wait();

            // Dispose top level last in case something goes wrong, at least the root wont be stored
            topLevelWriteBatch.Dispose();

            stopwatch.Stop();
            Metrics.SnapshotPersistenceTime = stopwatch.ElapsedMilliseconds;

            if (_logger.IsDebug) _logger.Debug($"Persisted trie from {commitSet.Root} at {commitSet.BlockNumber} in {stopwatch.ElapsedMilliseconds}ms (cache memory {MemoryUsedByDirtyCache})");

            LastPersistedBlockNumber = commitSet.BlockNumber;
        }

        private void PersistNodeStartingFrom(TrieNode tn, Hash256 address2, TreePath path, BlockCommitSet commitSet,
            Action<TreePath, Hash256?, TrieNode>? persistedNodeRecorder,
            WriteFlags writeFlags, BlockingCollection<INodeStorage.WriteBatch> disposeQueue)
        {
            long persistedNodeCount = 0;
            INodeStorage.WriteBatch writeBatch = _nodeStorage.StartWriteBatch();

            void DoPersist(TrieNode node, Hash256? address3, TreePath path2)
            {
                persistedNodeRecorder?.Invoke(path2, address3, node);
                PersistNode(address3, path2, node, commitSet.BlockNumber, writeFlags, writeBatch);

                persistedNodeCount++;
                if (persistedNodeCount % 512 == 0)
                {
                    disposeQueue.Add(writeBatch);
                    writeBatch = _nodeStorage.StartWriteBatch();
                }
            }

            tn.CallRecursively(DoPersist, address2, ref path, GetTrieStore(address2), true, _logger);
            disposeQueue.Add(writeBatch);
        }

        private void PersistNode(Hash256? address, in TreePath path, TrieNode currentNode, long blockNumber, WriteFlags writeFlags = WriteFlags.None, INodeStorage.WriteBatch? writeBatch = null)
        {
            writeBatch ??= _currentBatch ??= _nodeStorage.StartWriteBatch();
            ArgumentNullException.ThrowIfNull(currentNode);

            if (currentNode.Keccak is not null)
            {
                Debug.Assert(currentNode.LastSeen >= 0, $"Cannot persist a dangling node (without {(nameof(TrieNode.LastSeen))} value set).");
                // Note that the LastSeen value here can be 'in the future' (greater than block number
                // if we replaced a newly added node with an older copy and updated the LastSeen value.
                // Here we reach it from the old root so it appears to be out of place but it is correct as we need
                // to prevent it from being removed from cache and also want to have it persisted.

                if (_logger.IsTrace) _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode} in snapshot {blockNumber}.");
                writeBatch.Set(address, path, currentNode.Keccak, currentNode.FullRlp, writeFlags);
                currentNode.IsPersisted = true;
                currentNode.LastSeen = Math.Max(blockNumber, currentNode.LastSeen);
                PersistedNodesCount++;
            }
            else
            {
                Debug.Assert(currentNode.FullRlp.IsNotNull && currentNode.FullRlp.Length < 32,
                    "We only expect persistence call without Keccak for the nodes that are kept inside the parent RLP (less than 32 bytes).");
            }
        }

        public bool IsNoLongerNeeded(TrieNode node)
        {
            return IsNoLongerNeeded(node.LastSeen);
        }

        public bool IsNoLongerNeeded(long lastSeen)
        {
            Debug.Assert(lastSeen >= 0, $"Any node that is cache should have {nameof(TrieNode.LastSeen)} set.");
            return lastSeen < LastPersistedBlockNumber
                   && lastSeen < LatestCommittedBlockNumber - _pruningStrategy.MaxDepth;
        }

        private void DequeueOldCommitSets()
        {
            if (_commitSetQueue?.IsEmpty ?? true) return;

            while (_commitSetQueue.TryPeek(out BlockCommitSet blockCommitSet))
            {
                if (blockCommitSet.BlockNumber < LatestCommittedBlockNumber - _pruningStrategy.MaxDepth - 1)
                {
                    if (_logger.IsDebug) _logger.Debug($"Removing historical ({_commitSetQueue.Count}) {blockCommitSet.BlockNumber} < {LatestCommittedBlockNumber} - {_pruningStrategy.MaxDepth}");
                    _commitSetQueue.TryDequeue(out _);
                }
                else
                {
                    break;
                }
            }
        }

        private void EnsureCommitSetExistsForBlock(long blockNumber)
        {
            if (CurrentPackage is null)
            {
                if (_pruningStrategy.PruningEnabled && !Monitor.IsEntered(_dirtyNodesLock))
                {
                    Monitor.Enter(_dirtyNodesLock);
                }

                CreateCommitSet(blockNumber);
            }
        }

        private void AnnounceReorgBoundaries()
        {
            if (LatestCommittedBlockNumber < 1)
            {
                return;
            }

            bool shouldAnnounceReorgBoundary = !_pruningStrategy.PruningEnabled;
            bool isFirstCommit = Interlocked.Exchange(ref _isFirst, 1) == 0;
            if (isFirstCommit)
            {
                if (_logger.IsDebug) _logger.Debug($"Reached first commit - newest {LatestCommittedBlockNumber}, last persisted {LastPersistedBlockNumber}");
                // this is important when transitioning from fast sync
                // imagine that we transition at block 1200000
                // and then we close the app at 1200010
                // in such case we would try to continue at Head - 1200010
                // because head is loaded if there is no persistence checkpoint
                // so we need to force the persistence checkpoint
                long baseBlock = Math.Max(0, LatestCommittedBlockNumber - 1);
                LastPersistedBlockNumber = baseBlock;
                shouldAnnounceReorgBoundary = true;
            }
            else if (!_lastPersistedReachedReorgBoundary)
            {
                // even after we persist a block we do not really remember it as a safe checkpoint
                // until max reorgs blocks after
                if (LatestCommittedBlockNumber >= LastPersistedBlockNumber + _pruningStrategy.MaxDepth)
                {
                    shouldAnnounceReorgBoundary = true;
                }
            }

            if (shouldAnnounceReorgBoundary)
            {
                ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(LastPersistedBlockNumber));
                _lastPersistedReachedReorgBoundary = true;
            }
        }

        private void PersistOnShutdown()
        {
            // If we are in archive mode, we don't need to change reorg boundaries.
            if (_pruningStrategy.PruningEnabled)
            {
                if (_commitSetQueue?.IsEmpty ?? true) return;
                // here we try to shorten the number of blocks recalculated when restarting (so we force persist)
                // and we need to speed up the standard announcement procedure so we persists a block

                using ArrayPoolList<BlockCommitSet> candidateSets = new(_commitSetQueue.Count);
                while (_commitSetQueue.TryDequeue(out BlockCommitSet frontSet))
                {
                    if (!frontSet.IsSealed || candidateSets.Count == 0 || candidateSets[0].BlockNumber == frontSet!.BlockNumber)
                    {
                        candidateSets.Add(frontSet);
                    }
                    else if (frontSet!.BlockNumber < LatestCommittedBlockNumber - _pruningStrategy.MaxDepth
                             && frontSet!.BlockNumber > candidateSets[0].BlockNumber)
                    {
                        candidateSets.Clear();
                        candidateSets.Add(frontSet);
                    }
                }

                INodeStorage.WriteBatch writeBatch = _nodeStorage.StartWriteBatch();
                for (int index = 0; index < candidateSets.Count; index++)
                {
                    BlockCommitSet blockCommitSet = candidateSets[index];
                    if (_logger.IsDebug) _logger.Debug($"Persisting on disposal {blockCommitSet} (cache memory at {MemoryUsedByDirtyCache})");
                    PersistBlockCommitSet(null, blockCommitSet, writeBatch);
                }
                writeBatch.Dispose();

                if (candidateSets.Count == 0)
                {
                    if (_logger.IsDebug) _logger.Debug("No commitset to persist at all.");
                }
                else
                {
                    AnnounceReorgBoundaries();
                }
            }
        }

        public void PersistCache(CancellationToken cancellationToken)
        {
            if (_logger.IsInfo) _logger.Info($"Full Pruning Persist Cache started.");

            int commitSetCount = 0;
            Stopwatch stopwatch = Stopwatch.StartNew();
            // We persist all sealed Commitset causing PruneCache to almost completely clear the cache. Any new block that
            // need existing node will have to read back from db causing copy-on-read mechanism to copy the node.
            void ClearCommitSetQueue()
            {
                while (_commitSetQueue.TryPeek(out BlockCommitSet commitSet) && commitSet.IsSealed)
                {
                    if (!_commitSetQueue.TryDequeue(out commitSet)) break;
                    if (!commitSet.IsSealed)
                    {
                        // Oops
                        _commitSetQueue.Enqueue(commitSet);
                        break;
                    }

                    commitSetCount++;
                    using INodeStorage.WriteBatch writeBatch = _nodeStorage.StartWriteBatch();
                    PersistBlockCommitSet(null, commitSet, writeBatch);
                }
                PruneCurrentSet();
            }

            if (!(_commitSetQueue?.IsEmpty ?? true))
            {
                // We persist outside of lock first.
                ClearCommitSetQueue();
            }

            if (_logger.IsInfo) _logger.Info($"Saving all commit set took {stopwatch.Elapsed} for {commitSetCount} commit sets.");

            stopwatch.Restart();

            lock (_dirtyNodesLock)
            {
                lock (_dirtyNodesWriteLock)
                {
                    // Double check
                    ClearCommitSetQueue();
                    if (cancellationToken.IsCancellationRequested) return;

                    // This should clear most nodes. For some reason, not all.
                    PruneCache(skipRecalculateMemory: true);
                    if (cancellationToken.IsCancellationRequested) return;

                    foreach (TrieStoreDirtyNodesCache dirtyNode in _dirtyNodes)
                    {
                        PersistAllInCache(dirtyNode);
                    }
                    if (cancellationToken.IsCancellationRequested) return;

                    PruneCache();

                    if (DirtyNodesCount() != 0)
                    {
                        if (_logger.IsWarn) _logger.Warn($"{DirtyNodesCount()} cache entry remains.");
                    }

                    foreach (TrieStoreDirtyNodesCache dirtyNode in _dirtyNodes)
                    {
                        dirtyNode.ClearLivePruningTracking();
                    }
                }
            }

            if (_logger.IsInfo) _logger.Info($"Clear cache took {stopwatch.Elapsed}.");

            void PersistAllInCache(TrieStoreDirtyNodesCache cache)
            {
                ConcurrentDictionary<TrieStoreDirtyNodesCache.Key, bool> wasPersisted = new();

                void PersistNode(TrieNode n, Hash256? address, TreePath path)
                {
                    if (n.Keccak is null) return;
                    TrieStoreDirtyNodesCache.Key key = new TrieStoreDirtyNodesCache.Key(address, path, n.Keccak);
                    if (wasPersisted.TryAdd(key, true))
                    {
                        _nodeStorage.Set(address, path, n.Keccak, n.FullRlp);
                        n.IsPersisted = true;
                    }
                }

                foreach (KeyValuePair<TrieStoreDirtyNodesCache.Key, TrieNode> kv in cache.AllNodes)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    TrieStoreDirtyNodesCache.Key key = kv.Key;
                    TreePath path = key.Path;
                    Hash256? address = key.AddressAsHash256;
                    kv.Value.CallRecursively(PersistNode, address, ref path, GetTrieStore(address), false, _logger, resolveStorageRoot: false);
                }
            }
        }

        // Used to serve node by hash
        private byte[]? GetByHash(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            Hash256 asHash = new Hash256(key);
            return _pruningStrategy.PruningEnabled
                   && DirtyNodesTryGetValue(new TrieStoreDirtyNodesCache.Key(null, TreePath.Empty, asHash), out TrieNode? trieNode)
                   && trieNode is not null
                   && trieNode.NodeType != NodeType.Unknown
                   && trieNode.FullRlp.IsNotNull
                ? trieNode.FullRlp.ToArray()
                : _nodeStorage.Get(null, TreePath.Empty, asHash, flags);
        }

        public IReadOnlyKeyValueStore TrieNodeRlpStore => _publicStore;
        public bool IsCurrentlyFullPruning => _persistenceStrategy.IsFullPruning;

        public void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, byte[] rlp)
        {
            _nodeStorage.Set(address, path, keccak, rlp);
        }

        private class TrieKeyValueStore : IReadOnlyKeyValueStore
        {
            private readonly TrieStore _trieStore;

            public TrieKeyValueStore(TrieStore trieStore)
            {
                _trieStore = trieStore;
            }

            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => _trieStore.GetByHash(key, flags);
        }

        public bool HasRoot(Hash256 stateRoot)
        {
            if (stateRoot == Keccak.EmptyTreeHash) return true;
            TrieNode node = FindCachedOrUnknown(null, TreePath.Empty, stateRoot, true);
            if (node.NodeType == NodeType.Unknown)
            {
                return TryLoadRlp(null, TreePath.Empty, node.Keccak, ReadFlags.None) is not null;
            }

            return true;
        }

        internal static class HashHelpers
        {
            private const int HashPrime = 101;

            private static bool IsPrime(int candidate)
            {
                if ((candidate & 1) != 0)
                {
                    int limit = (int)Math.Sqrt(candidate);
                    for (int divisor = 3; divisor <= limit; divisor += 2)
                    {
                        if ((candidate % divisor) == 0)
                            return false;
                    }
                    return true;
                }
                return candidate == 2;
            }

            public static int GetPrime(int min)
            {
                foreach (int prime in Primes)
                {
                    if (prime >= min)
                        return prime;
                }

                // Outside of our predefined table. Compute the hard way.
                for (int i = (min | 1); i < int.MaxValue; i += 2)
                {
                    if (IsPrime(i) && ((i - 1) % HashPrime != 0))
                        return i;
                }
                return min;
            }

            // Table of prime numbers to use as hash table sizes.
            // A typical resize algorithm would pick the smallest prime number in this array
            // that is larger than twice the previous capacity.
            // Suppose our Hashtable currently has capacity x and enough elements are added
            // such that a resize needs to occur. Resizing first computes 2x then finds the
            // first prime in the table greater than 2x, i.e. if primes are ordered
            // p_1, p_2, ..., p_i, ..., it finds p_n such that p_n-1 < 2x < p_n.
            // Doubling is important for preserving the asymptotic complexity of the
            // hashtable operations such as add.  Having a prime guarantees that double
            // hashing does not lead to infinite loops.  IE, your hash function will be
            // h1(key) + i*h2(key), 0 <= i < size.  h2 and the size must be relatively prime.
            // We prefer the low computation costs of higher prime numbers over the increased
            // memory allocation of a fixed prime number i.e. when right sizing a HashSet.
            private static ReadOnlySpan<int> Primes =>
            [
                3,
                7,
                11,
                17,
                23,
                29,
                37,
                47,
                59,
                71,
                89,
                107,
                131,
                163,
                197,
                239,
                293,
                353,
                431,
                521,
                631,
                761,
                919,
                1103,
                1327,
                1597,
                1931,
                2333,
                2801,
                3371,
                4049,
                4861,
                5839,
                7013,
                8419,
                10103,
                12143,
                14591,
                17519,
                21023,
                25229,
                30293,
                36353,
                43627,
                52361,
                62851,
                75431,
                90523,
                108631,
                130363,
                156437,
                187751,
                225307,
                270371,
                324449,
                389357,
                467237,
                560689,
                672827,
                807403,
                968897,
                1162687,
                1395263,
                1674319,
                2009191,
                2411033,
                2893249,
                3471899,
                4166287,
                4999559,
                5999471,
                7199369
            ];
        }
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly struct HashAndTinyPathAndHash : IEquatable<HashAndTinyPathAndHash>
    {
        public readonly ValueHash256 hash;
        public readonly TinyTreePath path;
        public readonly ValueHash256 valueHash;

        public HashAndTinyPathAndHash(Hash256? hash, in TinyTreePath path, in ValueHash256 valueHash)
        {
            this.hash = hash ?? default;
            this.path = path;
            this.valueHash = valueHash;
        }
        public HashAndTinyPathAndHash(in ValueHash256 hash, in TinyTreePath path, in ValueHash256 valueHash)
        {
            this.hash = hash;
            this.path = path;
            this.valueHash = valueHash;
        }

        public bool Equals(HashAndTinyPathAndHash other) => hash == other.hash && path.Equals(in other.path) && valueHash.Equals(in other.valueHash);
        public override bool Equals(object? obj) => obj is HashAndTinyPath other && Equals(other);
        public override int GetHashCode()
        {
            var hashHash = hash != default ? hash.GetHashCode() : 1;
            return valueHash.GetChainedHashCode((uint)path.GetHashCode()) ^ hashHash;
        }
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly struct HashAndTinyPath : IEquatable<HashAndTinyPath>
    {
        public readonly ValueHash256 addr;
        public readonly TinyTreePath path;

        public HashAndTinyPath(Hash256? hash, in TinyTreePath path)
        {
            addr = hash ?? default;
            this.path = path;
        }
        public HashAndTinyPath(in ValueHash256 hash, in TinyTreePath path)
        {
            addr = hash;
            this.path = path;
        }

        public bool Equals(HashAndTinyPath other) => addr == other.addr && path.Equals(in other.path);
        public override bool Equals(object? obj) => obj is HashAndTinyPath other && Equals(other);
        public override int GetHashCode()
        {
            var addressHash = addr != default ? addr.GetHashCode() : 1;
            return path.GetHashCode() ^ addressHash;
        }
    }
}
