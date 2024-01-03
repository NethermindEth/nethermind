// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Trie store helps to manage trie commits block by block.
    /// If persistence and pruning are needed they have a chance to execute their behaviour on commits.
    /// </summary>
    public class TrieStore : ITrieStore
    {
        internal class DirtyNodesCache
        {
            private readonly TrieStore _trieStore;
            private readonly bool _storeByHash;
            public readonly long KeyMemoryUsage;

            public DirtyNodesCache(TrieStore trieStore)
            {
                _trieStore = trieStore;
                // If the nodestore indicated that path is not required,
                // we will use a map with hash as its key instead of the full Key to reduce memory usage.
                _storeByHash = !trieStore._nodeStorage.RequirePath;
                KeyMemoryUsage = _storeByHash ? 0 : Key.MemoryUsage; // 0 because previously it was not counted.
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
                    if (_trieStore._logger.IsTrace) _trieStore._logger.Trace($"Creating new node {trieNode}");
                    SaveInCache(key, trieNode);
                }

                return trieNode;
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

                if (_trieStore._logger.IsTrace) _trieStore._logger.Trace($"Creating new node {trieNode}");
                return trieNode;
            }

            private readonly ConcurrentDictionary<Key, TrieNode> _byKeyObjectCache = new();
            private readonly ConcurrentDictionary<ValueHash256, TrieNode> _byHashObjectCache = new();

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
                            pair => new KeyValuePair<Key, TrieNode>(new Key(null, TreePath.Empty, pair.Key.ToCommitment()), pair.Value));
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
                if (_byKeyObjectCache.Remove(key, out _))
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
                    _byKeyLock = _byKeyObjectCache.AcquireLock()
                };
            }

            private int _count = 0;

            public int Count => _count;

            public void Dump()
            {
                if (_trieStore._logger.IsTrace)
                {
                    _trieStore._logger.Trace($"Trie node dirty cache ({Count})");
                    foreach (KeyValuePair<Key, TrieNode> keyValuePair in AllNodes)
                    {
                        _trieStore._logger.Trace($"  {keyValuePair.Value}");
                    }
                }
            }

            public void Clear()
            {
                _byHashObjectCache.Clear();
                _byKeyObjectCache.Clear();
                Interlocked.Exchange(ref _count, 0);
                Metrics.CachedNodesCount = 0;
                _trieStore.MemoryUsedByDirtyCache = 0;
            }

            internal readonly struct Key
            {
                internal const long MemoryUsage = 8 + 36 + 8; // (address (probably shared), path, keccak pointer (shared with TrieNode))
                public Hash256? Address { get; }
                public TreePath Path { get; }
                public Hash256 Keccak { get; }

                public Key(Hash256? address, in TreePath path, Hash256 keccak)
                {
                    Address = address;
                    Path = path;
                    Keccak = keccak;
                }

                public override int GetHashCode()
                {
                    return Keccak.GetHashCode();
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
                public ConcurrentDictionaryLock<ValueHash256, TrieNode>.Lock _byHashLock;
                public ConcurrentDictionaryLock<Key, TrieNode>.Lock _byKeyLock;

                public void Dispose()
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

        private int _isFirst;

        private INodeStorage.WriteBatch? _currentBatch = null;

        private readonly DirtyNodesCache _dirtyNodes;

        // Track some of the persisted path hash. Used to be able to remove keys when it is replaced.
        // If null, disable removing key.
        private LruCache<(Hash256?, TinyTreePath), ValueHash256>? _pastPathHash;

        // Track ALL of the recently re-committed persisted nodes. This is so that we don't accidentally remove
        // recommitted persisted nodes (which will not get re-persisted).
        private ConcurrentDictionary<(Hash256?, TinyTreePath, ValueHash256), long> _persistedLastSeens = new();

        private bool _lastPersistedReachedReorgBoundary;
        private Task _pruningTask = Task.CompletedTask;
        private readonly CancellationTokenSource _pruningTaskCancellationTokenSource = new();

        private long _reorgDepth = Reorganization.MaxDepth;

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
            ILogManager? logManager,
            long? reorgDepthOverride = null)
        {
            _logger = logManager?.GetClassLogger<TrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _nodeStorage = nodeStorage ?? throw new ArgumentNullException(nameof(nodeStorage));
            _pruningStrategy = pruningStrategy ?? throw new ArgumentNullException(nameof(pruningStrategy));
            _persistenceStrategy = persistenceStrategy ?? throw new ArgumentNullException(nameof(persistenceStrategy));
            _dirtyNodes = new DirtyNodesCache(this);
            _publicStore = new TrieKeyValueStore(this);

            if (reorgDepthOverride != null) _reorgDepth = reorgDepthOverride.Value;

            if (pruningStrategy.TrackedPastKeyCount > 0 && nodeStorage.RequirePath)
            {
                _pastPathHash = new(pruningStrategy.TrackedPastKeyCount, "");
            }
            else
            {
                _pastPathHash = null;
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
            private set
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
                Metrics.CachedNodesCount = _dirtyNodes.Count;
                return _dirtyNodes.Count;
            }
        }

        public void CommitNode(long blockNumber, Hash256? address, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
            EnsureCommitSetExistsForBlock(blockNumber);

            if (_logger.IsTrace) _logger.Trace($"Committing {nodeCommitInfo} at {blockNumber}");
            if (!nodeCommitInfo.IsEmptyBlockMarker && !nodeCommitInfo.Node.IsBoundaryProofNode)
            {
                TrieNode node = nodeCommitInfo.Node!;

                if (node!.Keccak is null)
                {
                    throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");
                }

                if (CurrentPackage is null)
                {
                    throw new TrieStoreException($"{nameof(CurrentPackage)} is NULL when committing {node} at {blockNumber}.");
                }

                if (node!.LastSeen.HasValue)
                {
                    throw new TrieStoreException($"{nameof(TrieNode.LastSeen)} set on {node} committed at {blockNumber}.");
                }

                node = SaveOrReplaceInDirtyNodesCache(address, nodeCommitInfo, node);
                node.LastSeen = Math.Max(blockNumber, node.LastSeen ?? 0);

                if (!_pruningStrategy.PruningEnabled)
                {
                    Persist(address, nodeCommitInfo.Path, node, blockNumber, writeFlags);
                }

                CommittedNodesCount++;
            }
        }

        private TrieNode SaveOrReplaceInDirtyNodesCache(Hash256? address, NodeCommitInfo nodeCommitInfo, TrieNode node)
        {
            if (_pruningStrategy.PruningEnabled)
            {
                DirtyNodesCache.Key key = new DirtyNodesCache.Key(address, nodeCommitInfo.Path, node.Keccak);
                if (IsNodeCached(key))
                {
                    TrieNode cachedNodeCopy = FindCachedOrUnknown(address, nodeCommitInfo.Path, node.Keccak);
                    if (!ReferenceEquals(cachedNodeCopy, node))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Replacing {node} with its cached copy {cachedNodeCopy}.");
                        TreePath path = nodeCommitInfo.Path;
                        cachedNodeCopy.ResolveKey(GetTrieStore(address), ref path, nodeCommitInfo.IsRoot);
                        if (node.Keccak != cachedNodeCopy.Keccak)
                        {
                            throw new InvalidOperationException($"The hash of replacement node {cachedNodeCopy} is not the same as the original {node}.");
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
                    _dirtyNodes.SaveInCache(key, node);
                }
            }

            return node;
        }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, Hash256? address, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
            EnsureCommitSetExistsForBlock(blockNumber);

            try
            {
                if (trieType == TrieType.State) // storage tries happen before state commits
                {
                    if (_logger.IsTrace) _logger.Trace($"Enqueued blocks {_commitSetQueue.Count}");
                    BlockCommitSet set = CurrentPackage;
                    if (set is not null)
                    {
                        set.Root = root;
                        if (_logger.IsTrace) _logger.Trace($"Current root (block {blockNumber}): {set.Root}, block {set.BlockNumber}");
                        set.Seal();
                    }

                    bool shouldPersistSnapshot = _persistenceStrategy.ShouldPersist(set.BlockNumber);
                    if (shouldPersistSnapshot)
                    {
                        PersistBlockCommitSet(address, set, writeFlags: writeFlags);
                    }
                    else
                    {
                        PruneCurrentSet();
                    }

                    CurrentPackage = null;
                    if (_pruningStrategy.PruningEnabled && Monitor.IsEntered(_dirtyNodes))
                    {
                        Monitor.Exit(_dirtyNodes);
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

        public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            return TryLoadRlp(address, path, hash, null, flags);
        }

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

        public virtual byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            return LoadRlp(address, path, hash, null, flags);
        }

        public byte[] LoadRlp(Hash256? address, in TreePath path, Hash256 keccak, INodeStorage? nodeStorage, ReadFlags readFlags = ReadFlags.None)
        {
            byte[]? rlp = TryLoadRlp(address, path, keccak, nodeStorage, readFlags);
            if (rlp is null)
            {
                throw new TrieNodeException($"Node {keccak} is missing from the DB", keccak);
            }

            return rlp;
        }

        public bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak)
        {
            byte[]? rlp = _nodeStorage.Get(address, path, keccak, ReadFlags.None);

            if (rlp is null)
            {
                return false;
            }

            Metrics.LoadedFromDbNodesCount++;

            return true;
        }


        public IReadOnlyTrieStore AsReadOnly(INodeStorage? store)
        {
            return new ReadOnlyTrieStore(this, store);
        }

        private bool IsNodeCached(DirtyNodesCache.Key key) => _dirtyNodes.IsNodeCached(key);
        public bool IsNodeCached(Hash256? address, in TreePath path, Hash256? hash) => _dirtyNodes.IsNodeCached(new DirtyNodesCache.Key(address, path, hash));

        public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256? hash)
        {
            return FindCachedOrUnknown(address, path, hash, false);
        }

        internal TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256? hash, bool isReadOnly)
        {
            ArgumentNullException.ThrowIfNull(hash);

            if (!_pruningStrategy.PruningEnabled)
            {
                return new TrieNode(NodeType.Unknown, hash);
            }

            DirtyNodesCache.Key key = new DirtyNodesCache.Key(address, path, hash);
            return isReadOnly ? _dirtyNodes.FromCachedRlpOrUnknown(key) : _dirtyNodes.FindCachedOrUnknown(key);
        }

        public void Dump() => _dirtyNodes.Dump();

        public void Prune()
        {
            if (_pruningStrategy.ShouldPrune(MemoryUsedByDirtyCache) && _pruningTask.IsCompleted)
            {
                _pruningTask = Task.Run(() =>
                {
                    try
                    {
                        lock (_dirtyNodes)
                        {
                            using (_dirtyNodes.AcquireMapLock())
                            {
                                if (_logger.IsDebug) _logger.Debug($"Locked {nameof(TrieStore)} for pruning.");

                                while (!_pruningTaskCancellationTokenSource.IsCancellationRequested && _pruningStrategy.ShouldPrune(MemoryUsedByDirtyCache))
                                {
                                    PruneCache();

                                    if (_pruningTaskCancellationTokenSource.IsCancellationRequested || !CanPruneCacheFurther())
                                    {
                                        break;
                                    }
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

        private bool CanPruneCacheFurther()
        {
            if (_pruningStrategy.ShouldPrune(MemoryUsedByDirtyCache))
            {
                if (_logger.IsDebug) _logger.Debug("Elevated pruning starting");

                using ArrayPoolList<BlockCommitSet> toAddBack = new(_commitSetQueue.Count);
                using ArrayPoolList<BlockCommitSet> candidateSets = new(_commitSetQueue.Count);
                while (_commitSetQueue.TryDequeue(out BlockCommitSet frontSet))
                {
                    if (frontSet!.BlockNumber >= LatestCommittedBlockNumber - _reorgDepth)
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

                Dictionary<(Hash256?, TinyTreePath), Hash256?>? persistedHashes = _pastPathHash != null
                    ? new Dictionary<(Hash256?, TinyTreePath), Hash256?>()
                    : null;

                for (int index = 0; index < candidateSets.Count; index++)
                {
                    BlockCommitSet blockCommitSet = candidateSets[index];
                    if (_logger.IsDebug) _logger.Debug($"Elevated pruning for candidate {blockCommitSet.BlockNumber}");
                    PersistBlockCommitSet(null, blockCommitSet, persistedHashes: persistedHashes);
                }

                RemovePastKeys(persistedHashes);

                foreach (KeyValuePair<(Hash256, TinyTreePath, ValueHash256), long> keyValuePair in _persistedLastSeens)
                {
                    if (IsNoLongerNeeded(keyValuePair.Value))
                    {
                        _persistedLastSeens.Remove(keyValuePair.Key, out _);
                    }
                }

                if (candidateSets.Count > 0)
                {
                    return true;
                }

                _commitSetQueue.TryPeek(out BlockCommitSet? uselessFrontSet);
                if (_logger.IsDebug) _logger.Debug($"Found no candidate for elevated pruning (sets: {_commitSetQueue.Count}, earliest: {uselessFrontSet?.BlockNumber}, newest kept: {LatestCommittedBlockNumber}, reorg depth {_reorgDepth})");
            }

            return false;
        }

        private void RemovePastKeys(Dictionary<(Hash256, TinyTreePath), Hash256?>? persistedHashes)
        {
            if (persistedHashes == null) return;

            bool CanRemove(Hash256? address, TinyTreePath path, TreePath fullPath, ValueHash256 keccak, Hash256? currentlyPersistingKeccak)
            {
                // Multiple current hash that we don't keep track for simplicity. Just ignore this case.
                if (currentlyPersistingKeccak == null) return false;

                // The persisted hash is the same as currently persisting hash. Do nothing.
                if (currentlyPersistingKeccak == keccak) return false;

                // We have is in cache and it is still needed.
                if (_dirtyNodes.TryGetValue(new DirtyNodesCache.Key(address, fullPath, keccak.ToCommitment()), out TrieNode node) &&
                    !IsNoLongerNeeded(node)) return false;

                // We don't have it in cache, but we know it was re-committed, so if it is still needed, don't remove
                if (_persistedLastSeens.TryGetValue((address, path, keccak), out long commitBlock) &&
                    !IsNoLongerNeeded(commitBlock)) return false;

                return true;
            }

            using INodeStorage.WriteBatch deleteNodeBatch = _nodeStorage.StartWriteBatch();
            foreach (KeyValuePair<(Hash256?, TinyTreePath), Hash256> keyValuePair in persistedHashes)
            {
                (Hash256? addr, TinyTreePath path) key = keyValuePair.Key;
                if (key.path.Length > TinyTreePath.MaxNibbleLength) continue;
                if (_pastPathHash.TryGet((key.addr, key.path), out ValueHash256 prevHash))
                {
                    TreePath fullPath = key.path.ToTreePath(); // Micro op to reduce double convert
                    if (CanRemove(key.addr, key.path, fullPath, prevHash, keyValuePair.Value))
                    {
                        deleteNodeBatch.Remove(key.addr, fullPath, prevHash);
                    }
                }
            }
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
        private void PruneCache()
        {
            if (_logger.IsDebug) _logger.Debug($"Pruning nodes {MemoryUsedByDirtyCache / 1.MB()} MB , last persisted block: {LastPersistedBlockNumber} current: {LatestCommittedBlockNumber}.");
            Stopwatch stopwatch = Stopwatch.StartNew();

            long newMemory = 0;
            foreach ((DirtyNodesCache.Key key, TrieNode node) in _dirtyNodes.AllNodes)
            {
                if (node.IsPersisted)
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing persisted {node} from memory.");

                    TrackPrunedPersistedNodes(key, node);

                    Hash256? keccak = node.Keccak;
                    if (keccak is null)
                    {
                        TreePath path2 = key.Path;
                        Hash256? newKeccak = node.GenerateKey(this.GetTrieStore(key.Address), ref path2, isRoot: true);
                        if (newKeccak != key.Keccak)
                        {
                            throw new InvalidOperationException($"Persisted {node} {newKeccak} != {keccak}");
                        }
                    }
                    _dirtyNodes.Remove(key);

                    Metrics.PrunedPersistedNodesCount++;
                }
                else if (IsNoLongerNeeded(node))
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing {node} from memory (no longer referenced).");
                    if (node.Keccak is null)
                    {
                        throw new InvalidOperationException($"Removed {node}");
                    }
                    _dirtyNodes.Remove(key);

                    Metrics.PrunedTransientNodesCount++;
                }
                else
                {
                    node.PrunePersistedRecursively(1);
                    newMemory += node.GetMemorySize(false) + _dirtyNodes.KeyMemoryUsage;
                }
            }

            MemoryUsedByDirtyCache = newMemory + _persistedLastSeens.Count * 48;
            Metrics.CachedNodesCount = _dirtyNodes.Count;

            stopwatch.Stop();
            Metrics.PruningTime = stopwatch.ElapsedMilliseconds;
            if (_logger.IsDebug) _logger.Debug($"Finished pruning nodes in {stopwatch.ElapsedMilliseconds}ms {MemoryUsedByDirtyCache / 1.MB()} MB, last persisted block: {LastPersistedBlockNumber} current: {LatestCommittedBlockNumber}.");
        }

        private void TrackPrunedPersistedNodes(in DirtyNodesCache.Key key, TrieNode node)
        {
            if (key.Path.Length > TinyTreePath.MaxNibbleLength) return;
            if (_pastPathHash == null) return;

            TinyTreePath treePath = new TinyTreePath(key.Path);
            // Persisted node with LastSeen is a node that has been re-committed, likely due to processing
            // recalculated to the same hash.
            if (node.LastSeen != null)
            {
                // Update _persistedLastSeen to later value.
                if (!_persistedLastSeens.TryGetValue((key.Address, treePath, key.Keccak), out long currentLastSeen) || currentLastSeen < node.LastSeen.Value)
                {
                    _persistedLastSeens[(key.Address, treePath, key.Keccak)] = node.LastSeen.Value;
                }
            }

            // This persisted node is being removed from cache. Keep it in mind in case of an update to the same
            // path.
            _pastPathHash.Set((key.Address, treePath), key.Keccak);
        }

        /// <summary>
        /// This method is here to support testing.
        /// </summary>
        public void ClearCache() => _dirtyNodes.Clear();

        public void Dispose()
        {
            if (_logger.IsDebug) _logger.Debug("Disposing trie");
            _pruningTaskCancellationTokenSource.Cancel();
            _pruningTask.Wait();
            PersistOnShutdown();
        }

        #region Private

        protected readonly INodeStorage _nodeStorage;

        private readonly TrieKeyValueStore _publicStore;

        private readonly IPruningStrategy _pruningStrategy;

        private readonly IPersistenceStrategy _persistenceStrategy;

        private readonly ILogger _logger;

        private readonly ConcurrentQueue<BlockCommitSet> _commitSetQueue = new();

        private long _memoryUsedByDirtyCache;

        private int _committedNodesCount;

        private int _persistedNodesCount;

        private long _latestPersistedBlockNumber;

        private BlockCommitSet? CurrentPackage { get; set; }

        private bool IsCurrentListSealed => CurrentPackage is null || CurrentPackage.IsSealed;

        private long LatestCommittedBlockNumber { get; set; }
        public INodeStorage.KeyScheme Scheme => _nodeStorage.Scheme;

        private void CreateCommitSet(long blockNumber)
        {
            if (_logger.IsDebug) _logger.Debug($"Beginning new {nameof(BlockCommitSet)} - {blockNumber}");

            // TODO: this throws on reorgs, does it not? let us recreate it in test
            Debug.Assert(CurrentPackage is null || blockNumber == CurrentPackage.BlockNumber + 1, "Newly begun block is not a successor of the last one");
            Debug.Assert(IsCurrentListSealed, "Not sealed when beginning new block");

            BlockCommitSet commitSet = new(blockNumber);
            _commitSetQueue.Enqueue(commitSet);
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
        /// <param name="persistedHashes">Track persisted hashes in this dictionary if not null</param>
        /// <param name="writeFlags"></param>
        private void PersistBlockCommitSet(
            Hash256? address,
            BlockCommitSet commitSet,
            Dictionary<(Hash256, TinyTreePath), Hash256?>? persistedHashes = null,
            WriteFlags writeFlags = WriteFlags.None)
        {
            void PersistNode(TrieNode tn, Hash256? address2, TreePath path)
            {
                if (persistedHashes != null && path.Length <= TinyTreePath.MaxNibbleLength)
                {
                    (Hash256 address2, TinyTreePath path) key = (address2, new TinyTreePath(path));
                    if (persistedHashes.ContainsKey(key))
                    {
                        // Null mark that there are multiple saved hash for this path. So we don't attempt to remove anything.
                        // Otherwise this would have to be a list, which is such a rare case that its not worth it to have a list.
                        persistedHashes[key] = null;
                    }
                    else
                    {
                        persistedHashes[key] = tn.Keccak;
                    }
                }
                Persist(address2, path, tn, commitSet.BlockNumber, writeFlags);
            }

            try
            {
                _currentBatch ??= _nodeStorage.StartWriteBatch();
                if (_logger.IsDebug) _logger.Debug($"Persisting from root {commitSet.Root} in {commitSet.BlockNumber}");

                Stopwatch stopwatch = Stopwatch.StartNew();
                TreePath path = TreePath.Empty;
                commitSet.Root?.CallRecursively(PersistNode, address, ref path, GetTrieStore(null), true, _logger);
                stopwatch.Stop();
                Metrics.SnapshotPersistenceTime = stopwatch.ElapsedMilliseconds;

                if (_logger.IsDebug) _logger.Debug($"Persisted trie from {commitSet.Root} at {commitSet.BlockNumber} in {stopwatch.ElapsedMilliseconds}ms (cache memory {MemoryUsedByDirtyCache})");

                LastPersistedBlockNumber = commitSet.BlockNumber;
            }
            finally
            {
                // For safety we prefer to commit half of the batch rather than not commit at all.
                // Generally hanging nodes are not a problem in the DB but anything missing from the DB is.
                _currentBatch?.Dispose();
                _currentBatch = null;
            }

            PruneCurrentSet();
        }

        private void Persist(Hash256? address, in TreePath path, TrieNode currentNode, long blockNumber, WriteFlags writeFlags = WriteFlags.None)
        {
            _currentBatch ??= _nodeStorage.StartWriteBatch();
            ArgumentNullException.ThrowIfNull(currentNode);

            if (currentNode.Keccak is not null)
            {
                Debug.Assert(currentNode.LastSeen.HasValue, $"Cannot persist a dangling node (without {(nameof(TrieNode.LastSeen))} value set).");
                // Note that the LastSeen value here can be 'in the future' (greater than block number
                // if we replaced a newly added node with an older copy and updated the LastSeen value.
                // Here we reach it from the old root so it appears to be out of place but it is correct as we need
                // to prevent it from being removed from cache and also want to have it persisted.

                if (_logger.IsTrace) _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode} in snapshot {blockNumber}.");
                _currentBatch.Set(address, path, currentNode.Keccak, currentNode.FullRlp.ToArray(), writeFlags);
                currentNode.IsPersisted = true;
                currentNode.LastSeen = Math.Max(blockNumber, currentNode.LastSeen ?? 0);
                PersistedNodesCount++;
            }
            else
            {
                Debug.Assert(currentNode.FullRlp.IsNotNull && currentNode.FullRlp.Length < 32,
                    "We only expect persistence call without Keccak for the nodes that are kept inside the parent RLP (less than 32 bytes).");
            }
        }

        private bool IsNoLongerNeeded(TrieNode node)
        {
            return IsNoLongerNeeded(node.LastSeen);
        }

        private bool IsNoLongerNeeded(long? lastSeen)
        {
            Debug.Assert(lastSeen.HasValue, $"Any node that is cache should have {nameof(TrieNode.LastSeen)} set.");
            return lastSeen < LastPersistedBlockNumber
                   && lastSeen < LatestCommittedBlockNumber - _reorgDepth;
        }

        private void DequeueOldCommitSets()
        {
            while (_commitSetQueue.TryPeek(out BlockCommitSet blockCommitSet))
            {
                if (blockCommitSet.BlockNumber < LatestCommittedBlockNumber - _reorgDepth - 1)
                {
                    if (_logger.IsDebug) _logger.Debug($"Removing historical ({_commitSetQueue.Count}) {blockCommitSet.BlockNumber} < {LatestCommittedBlockNumber} - {_reorgDepth}");
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
                if (_pruningStrategy.PruningEnabled && !Monitor.IsEntered(_dirtyNodes))
                {
                    Monitor.Enter(_dirtyNodes);
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
                if (LatestCommittedBlockNumber >= LastPersistedBlockNumber + _reorgDepth)
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
                // here we try to shorten the number of blocks recalculated when restarting (so we force persist)
                // and we need to speed up the standard announcement procedure so we persists a block

                using ArrayPoolList<BlockCommitSet> candidateSets = new(_commitSetQueue.Count);
                while (_commitSetQueue.TryDequeue(out BlockCommitSet frontSet))
                {
                    if (!frontSet.IsSealed || candidateSets.Count == 0 || candidateSets[0].BlockNumber == frontSet!.BlockNumber)
                    {
                        candidateSets.Add(frontSet);
                    }
                    else if (frontSet!.BlockNumber < LatestCommittedBlockNumber - _reorgDepth
                             && frontSet!.BlockNumber > candidateSets[0].BlockNumber)
                    {
                        candidateSets.Clear();
                        candidateSets.Add(frontSet);
                    }
                }

                for (int index = 0; index < candidateSets.Count; index++)
                {
                    BlockCommitSet blockCommitSet = candidateSets[index];
                    if (_logger.IsDebug) _logger.Debug($"Persisting on disposal {blockCommitSet} (cache memory at {MemoryUsedByDirtyCache})");
                    PersistBlockCommitSet(null, blockCommitSet);
                }

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

        #endregion

        public void PersistCache(INodeStorage store, CancellationToken cancellationToken)
        {
            Task.Run(() =>
            {
                const int million = 1_000_000;
                int persistedNodes = 0;
                Stopwatch stopwatch = Stopwatch.StartNew();

                void PersistNode(TrieNode n, Hash256? address, TreePath path)
                {
                    Hash256? hash = n.Keccak;
                    if (hash is not null)
                    {
                        store.Set(address, path, hash, n.FullRlp.ToArray());
                        int persistedNodesCount = Interlocked.Increment(ref persistedNodes);
                        if (_logger.IsInfo && persistedNodesCount % million == 0)
                        {
                            _logger.Info($"Full Pruning Persist Cache in progress: {stopwatch.Elapsed} {persistedNodesCount / million:N} mln nodes persisted.");
                        }
                    }
                }

                if (_logger.IsInfo) _logger.Info($"Full Pruning Persist Cache started.");
                KeyValuePair<DirtyNodesCache.Key, TrieNode>[] nodesCopy = _dirtyNodes.AllNodes.ToArray();
                Parallel.For(0, nodesCopy.Length, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount / 2 }, i =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        TreePath path = nodesCopy[i].Key.Path;
                        nodesCopy[i].Value.CallRecursively(PersistNode, null, ref path, this.GetTrieStore(nodesCopy[i].Key.Address), false, _logger, false);
                    }
                });

                if (_logger.IsInfo) _logger.Info($"Full Pruning Persist Cache finished: {stopwatch.Elapsed} {persistedNodes / (double)million:N} mln nodes persisted.");
            });
        }

        // Used to serve node by hash
        private byte[]? GetByHash(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            Hash256 asHash = new Hash256(key);
            return _pruningStrategy.PruningEnabled
                   && _dirtyNodes.TryGetValue(new DirtyNodesCache.Key(null, TreePath.Empty, asHash), out TrieNode? trieNode)
                   && trieNode is not null
                   && trieNode.NodeType != NodeType.Unknown
                   && trieNode.FullRlp.IsNotNull
                ? trieNode.FullRlp.ToArray()
                : _nodeStorage.Get(null, TreePath.Empty, asHash, flags);
        }

        public IReadOnlyKeyValueStore TrieNodeRlpStore => _publicStore;

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
    }
}
