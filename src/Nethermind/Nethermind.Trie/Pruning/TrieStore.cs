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
using System.Threading.Tasks.Dataflow;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    using Nethermind.Core.Cpu;
    using CollectionExtensions = Core.Collections.CollectionExtensions;

    /// <summary>
    /// Trie store helps to manage trie commits block by block.
    /// If persistence and pruning are needed they have a chance to execute their behaviour on commits.
    /// </summary>
    public class TrieStore : ITrieStore, IPruningTrieStore
    {
        private int _isFirst;

        private INodeStorage.WriteBatch? _currentBatch = null;

        private TrieStoreDirtyNodesCache? _dirtyNodes;

        private object _dirtyNodesLock = new object();
        private TrieStoreDirtyNodesCache DirtyNodes => _dirtyNodes ?? CreateCacheAtomic(ref _dirtyNodes);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TrieStoreDirtyNodesCache CreateCacheAtomic(ref TrieStoreDirtyNodesCache val)
        {
            TrieStoreDirtyNodesCache instance = new(this, _pruningStrategy.TrackedPastKeyCount, !_nodeStorage.RequirePath, _logger);
            TrieStoreDirtyNodesCache? prior = Interlocked.CompareExchange(ref val, instance, null);
            return prior ?? instance;
        }

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

        private TrieStoreDirtyNodesCache GetDirtyNodes(Hash256? address)
        {
            return DirtyNodes;
        }

        private int DirtyNodesCount()
        {
            TrieStoreDirtyNodesCache cache = DirtyNodes;
            return cache.Count;
        }

        private bool DirtyNodesTryGetValue(in TrieStoreDirtyNodesCache.Key key, out TrieNode node)
        {
            TrieStoreDirtyNodesCache cache = GetDirtyNodes(key.Address);
            return cache.TryGetValue(key, out node);
        }

        private void DirtyNodesSaveInCache(in TrieStoreDirtyNodesCache.Key key, TrieNode node)
        {
            TrieStoreDirtyNodesCache cache = GetDirtyNodes(key.Address);
            cache.SaveInCache(key, node);
        }

        private bool DirtyNodesIsNodeCached(TrieStoreDirtyNodesCache.Key key)
        {
            TrieStoreDirtyNodesCache cache = GetDirtyNodes(key.Address);
            return cache.IsNodeCached(key);
        }

        private TrieNode DirtyNodesFromCachedRlpOrUnknown(TrieStoreDirtyNodesCache.Key key)
        {
            TrieStoreDirtyNodesCache cache = GetDirtyNodes(key.Address);
            return cache.FromCachedRlpOrUnknown(key);
        }

        private TrieNode DirtyNodesFindCachedOrUnknown(TrieStoreDirtyNodesCache.Key key)
        {
            TrieStoreDirtyNodesCache cache = GetDirtyNodes(key.Address);
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
        public void Dump() => DirtyNodes.Dump();

        public void Prune()
        {
            if (_pruningStrategy.ShouldPrune(MemoryUsedByDirtyCache) && _pruningTask.IsCompleted)
            {
                _pruningTask = Task.Run(() =>
                {
                    try
                    {
                        lock (_dirtyNodesLock)
                        {
                            TrieStoreDirtyNodesCache cache = DirtyNodes;
                            using (cache.AcquireMapLock())
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

                var cache = DirtyNodes;

                bool shouldDeletePersistedNode =
                    // Its disabled
                    _livePruningEnabled &&
                    // Full pruning need to visit all node, so can't delete anything.
                    !_persistenceStrategy.IsFullPruning &&
                    // If more than one candidate set, its a reorg, we can't remove node as persisted node may not be canonical
                    candidateSets.Count == 1;

                Dictionary<HashAndTinyPath, Hash256?>? persistedHashes =
                    shouldDeletePersistedNode
                    ? new Dictionary<HashAndTinyPath, Hash256?>()
                    : null;

                INodeStorage.WriteBatch writeBatch = _nodeStorage.StartWriteBatch();
                for (int index = 0; index < candidateSets.Count; index++)
                {
                    BlockCommitSet blockCommitSet = candidateSets[index];
                    if (_logger.IsDebug) _logger.Debug($"Elevated pruning for candidate {blockCommitSet.BlockNumber}");
                    PersistBlockCommitSet(null, blockCommitSet, writeBatch, persistedHashes: persistedHashes);
                }

                // Run in parallel. Reduce time by about 30%.
                Task deleteTask = Task.Run(() =>
                {
                    cache.RemovePastKeys(persistedHashes, _nodeStorage);
                });

                writeBatch.Dispose();
                AnnounceReorgBoundaries();
                deleteTask.Wait();

                if (_livePruningEnabled)
                {
                    cache.CleanObsoletePersistedLastSeen();
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

            long newMemory = DirtyNodes.PruneCache(skipRecalculateMemory);

            if (!skipRecalculateMemory) MemoryUsedByDirtyCache = newMemory;
            _ = CachedNodesCount; // Setter also update the count

            stopwatch.Stop();
            if (_logger.IsDebug) _logger.Debug($"Finished pruning nodes in {stopwatch.ElapsedMilliseconds}ms {MemoryUsedByDirtyCache / 1.MB()} MB, last persisted block: {LastPersistedBlockNumber} current: {LatestCommittedBlockNumber}.");
        }

        /// <summary>
        /// This method is here to support testing.
        /// </summary>
        public void ClearCache() => DirtyNodes.Clear();

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
            Dictionary<HashAndTinyPath, Hash256?>? persistedHashes = null,
            WriteFlags writeFlags = WriteFlags.None
        )
        {
            void PersistNode(TrieNode tn, Hash256? address2, TreePath path)
            {
                if (persistedHashes is not null && path.Length <= TinyTreePath.MaxNibbleLength)
                {
                    HashAndTinyPath key = new(address2, new TinyTreePath(path));
                    ref Hash256? hash = ref CollectionsMarshal.GetValueRefOrAddDefault(persistedHashes, key, out bool exists);
                    if (exists)
                    {
                        // Null mark that there are multiple saved hash for this path. So we don't attempt to remove anything.
                        // Otherwise this would have to be a list, which is such a rare case that its not worth it to have a list.
                        hash = null;
                    }
                    else
                    {
                        hash = tn.Keccak;
                    }
                }
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
                TrieStoreDirtyNodesCache cache = DirtyNodes;
                using (cache.AcquireMapLock())
                {
                    // Double check
                    ClearCommitSetQueue();
                    if (cancellationToken.IsCancellationRequested) return;

                    // This should clear most nodes. For some reason, not all.
                    PruneCache(skipRecalculateMemory: true);
                    if (cancellationToken.IsCancellationRequested) return;

                    PersistAllInCache(cache);
                    if (cancellationToken.IsCancellationRequested) return;

                    PruneCache();

                    if (cache.Count != 0)
                    {
                        if (_logger.IsWarn) _logger.Warn($"{cache.Count} cache entry remains.");
                    }

                    cache.ClearLivePruningTracking();
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
                    kv.Value.CallRecursively(PersistNode, address, ref path, GetTrieStore(address), false, _logger, false);
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
