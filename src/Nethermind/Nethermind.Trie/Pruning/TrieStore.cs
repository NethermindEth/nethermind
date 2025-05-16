// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Trie store helps to manage trie commits block by block.
/// If persistence and pruning are needed they have a chance to execute their behaviour on commits.
/// </summary>
public sealed class TrieStore : ITrieStore, IPruningTrieStore
{
    private readonly int _shardedDirtyNodeCount = 256;
    private readonly int _shardBit = 8;
    private readonly int _maxDepth;
    private readonly double _prunePersistedNodePortion;
    private readonly long _prunePersistedNodeMinimumTarget;

    private int _isFirst;

    private readonly TrieStoreDirtyNodesCache[] _dirtyNodes = [];
    private readonly Task[] _dirtyNodesTasks = [];
    private readonly ConcurrentDictionary<HashAndTinyPath, Hash256?>[] _persistedHashes = [];
    private readonly Action<TreePath, Hash256?, TrieNode> _persistedNodeRecorder;
    private readonly Task[] _disposeTasks = new Task[RuntimeInformation.PhysicalCoreCount];

    // This seems to attempt prevent multiple block processing at the same time and along with pruning at the same time.
    private readonly object _dirtyNodesLock = new object();

    private readonly bool _livePruningEnabled = false;

    private bool _lastPersistedReachedReorgBoundary;
    private Task _pruningTask = Task.CompletedTask;
    private readonly CancellationTokenSource _pruningTaskCancellationTokenSource = new();

    public TrieStore(
        INodeStorage nodeStorage,
        IPruningStrategy pruningStrategy,
        IPersistenceStrategy persistenceStrategy,
        IPruningConfig pruningConfig,
        ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<TrieStore>();
        _nodeStorage = nodeStorage;
        _pruningStrategy = pruningStrategy;
        _persistenceStrategy = persistenceStrategy;
        _publicStore = new TrieKeyValueStore(this);
        _persistedNodeRecorder = PersistedNodeRecorder;
        _maxDepth = pruningConfig.PruningBoundary;
        _prunePersistedNodePortion = pruningConfig.PrunePersistedNodePortion;
        _prunePersistedNodeMinimumTarget = pruningConfig.PrunePersistedNodeMinimumTarget;

        if (pruningStrategy.PruningEnabled)
        {
            _shardBit = pruningConfig.DirtyNodeShardBit;
            _shardedDirtyNodeCount = 1 << _shardBit;

            // 30 because of the 1 << 31 become negative
            if (_shardBit is <= 0 or > 30)
            {
                throw new InvalidOperationException($"Shard bit count must be between 0 and 30.");
            }

            _dirtyNodes = new TrieStoreDirtyNodesCache[_shardedDirtyNodeCount];
            _dirtyNodesTasks = new Task[_shardedDirtyNodeCount];
            _persistedHashes = new ConcurrentDictionary<HashAndTinyPath, Hash256?>[_shardedDirtyNodeCount];
            for (int i = 0; i < _shardedDirtyNodeCount; i++)
            {
                _dirtyNodes[i] = new TrieStoreDirtyNodesCache(this, !_nodeStorage.RequirePath, _logger);
                _persistedHashes[i] = new ConcurrentDictionary<HashAndTinyPath, Hash256>();
            }
        }

        if (pruningStrategy.PruningEnabled && pruningConfig.TrackPastKeys && nodeStorage.RequirePath)
        {
            _livePruningEnabled = true;
        }
    }

    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

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

    public long DirtyMemoryUsedByDirtyCache
    {
        get => _dirtyMemoryUsedByDirtyCache;
        set
        {
            Metrics.DirtyMemoryUsedByCache = value;
            _dirtyMemoryUsedByDirtyCache = value;
        }
    }

    public long PersistedMemoryUsedByDirtyCache => MemoryUsedByDirtyCache - DirtyMemoryUsedByDirtyCache;

    public void IncrementMemoryUsedByDirtyCache(long nodeMemoryUsage, bool persisted)
    {
        Metrics.CachedNodesCount = Interlocked.Increment(ref _totalCachedNodesCount);
        Metrics.MemoryUsedByCache = Interlocked.Add(ref _memoryUsedByDirtyCache, nodeMemoryUsage);
        if (!persisted)
        {
            Metrics.DirtyNodesCount = Interlocked.Increment(ref _dirtyNodesCount);
            Metrics.DirtyMemoryUsedByCache = Interlocked.Add(ref _dirtyMemoryUsedByDirtyCache, nodeMemoryUsage);
        }
    }

    public void DecreaseMemoryUsedByDirtyCache(long nodeMemoryUsage, bool persisted)
    {
        Metrics.CachedNodesCount = Interlocked.Decrement(ref _totalCachedNodesCount);
        Metrics.MemoryUsedByCache = Interlocked.Add(ref _memoryUsedByDirtyCache, -nodeMemoryUsage);
        if (!persisted)
        {
            Metrics.DirtyNodesCount = Interlocked.Decrement(ref _dirtyNodesCount);
            Metrics.DirtyMemoryUsedByCache = Interlocked.Add(ref _dirtyMemoryUsedByDirtyCache, -nodeMemoryUsage);
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

    private void IncrementCommittedNodesCount()
    {
        Metrics.CommittedNodesCount = Interlocked.Increment(ref _committedNodesCount);
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

    private void IncrementPersistedNodesCount()
    {
        Metrics.PersistedNodeCount = Interlocked.Increment(ref _persistedNodesCount);
    }

    public long CachedNodesCount
    {
        get
        {
            long count = NodesCount();
            Metrics.CachedNodesCount = count;
            return count;
        }
    }

    public long DirtyCachedNodesCount
    {
        get
        {
            long count = DirtyNodesCount();
            Metrics.DirtyNodesCount = count;
            return count;
        }
    }

    private void CommitAndInsertToDirtyNodes(long blockNumber, Hash256? address, ref TreePath path, in NodeCommitInfo nodeCommitInfo)
    {
        Debug.Assert(_pruningStrategy.PruningEnabled);

        if (_logger.IsTrace) Trace(blockNumber, in nodeCommitInfo);
        if (!nodeCommitInfo.IsEmptyBlockMarker && !nodeCommitInfo.Node.IsBoundaryProofNode)
        {
            TrieNode node = nodeCommitInfo.Node;

            if (node.Keccak is null)
            {
                ThrowUnknownHash(node);
            }

            if (node.LastSeen >= 0)
            {
                ThrowNodeHasBeenSeen(blockNumber, node);
            }

            node = SaveOrReplaceInDirtyNodesCache(address, ref path, nodeCommitInfo, node);
            node.LastSeen = Math.Max(blockNumber, node.LastSeen);

            IncrementCommittedNodesCount();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(long blockNumber, in NodeCommitInfo nodeCommitInfo)
        {
            _logger.Trace($"Committing {nodeCommitInfo} at {blockNumber}");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowUnknownHash(TrieNode node) => throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowNodeHasBeenSeen(long blockNumber, TrieNode node) => throw new TrieStoreException($"{nameof(TrieNode.LastSeen)} set on {node} committed at {blockNumber}.");
    }

    private void CommitAndPersistNode(Hash256? address, ref TreePath path, in NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags, INodeStorage.IWriteBatch? writeBatch)
    {
        Debug.Assert(!_pruningStrategy.PruningEnabled);

        if (!nodeCommitInfo.IsEmptyBlockMarker && !nodeCommitInfo.Node.IsBoundaryProofNode)
        {
            TrieNode node = nodeCommitInfo.Node;

            if (node.Keccak is null)
            {
                ThrowUnknownHash(node);
            }

            PersistNode(address, path, node, writeBatch!, writeFlags);

            IncrementCommittedNodesCount();
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowUnknownHash(TrieNode node) => throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");
    }

    private int GetNodeShardIdx(in TreePath path, Hash256 hash)
    {
        // When enabled, the shard have dictionaries for tracking past path hash also.
        // So the same path need to be in the same shard for the remove logic to work.
        uint hashCode = (uint)(_livePruningEnabled
            ? path.GetHashCode()
            : hash.GetHashCode());

        return (int)(hashCode % _shardedDirtyNodeCount);
    }

    private int GetNodeShardIdx(in TrieStoreDirtyNodesCache.Key key) => GetNodeShardIdx(key.Path, key.Keccak);

    private TrieStoreDirtyNodesCache GetDirtyNodeShard(in TrieStoreDirtyNodesCache.Key key) => _dirtyNodes[GetNodeShardIdx(key)];

    private long NodesCount()
    {
        long count = 0;
        foreach (TrieStoreDirtyNodesCache dirtyNode in _dirtyNodes)
        {
            count += dirtyNode.Count;
        }
        return count;
    }

    private long DirtyNodesCount()
    {
        long count = 0;
        foreach (TrieStoreDirtyNodesCache dirtyNode in _dirtyNodes)
        {
            count += dirtyNode.DirtyCount;
        }
        return count;
    }

    private bool DirtyNodesTryGetValue(in TrieStoreDirtyNodesCache.Key key, out TrieNode? node) =>
        GetDirtyNodeShard(key).TryGetValue(key, out node);

    private void DirtyNodesSaveInCache(in TrieStoreDirtyNodesCache.Key key, TrieNode node) =>
        GetDirtyNodeShard(key).SaveInCache(key, node);

    private bool DirtyNodesIsNodeCached(TrieStoreDirtyNodesCache.Key key) =>
        GetDirtyNodeShard(key).IsNodeCached(key);

    private TrieNode DirtyNodesFromCachedRlpOrUnknown(TrieStoreDirtyNodesCache.Key key) =>
        GetDirtyNodeShard(key).FromCachedRlpOrUnknown(key);

    private TrieNode DirtyNodesFindCachedOrUnknown(TrieStoreDirtyNodesCache.Key key) =>
        GetDirtyNodeShard(key).FindCachedOrUnknown(key);

    private TrieNode SaveOrReplaceInDirtyNodesCache(Hash256? address, ref TreePath path, NodeCommitInfo nodeCommitInfo, TrieNode node)
    {
        TrieStoreDirtyNodesCache.Key key = new(address, path, node.Keccak);
        if (DirtyNodesTryGetValue(in key, out TrieNode cachedNodeCopy))
        {
            Metrics.LoadedFromCacheNodesCount++;
            if (!ReferenceEquals(cachedNodeCopy, node))
            {
                if (_logger.IsTrace) Trace(node, cachedNodeCopy);
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

        return node;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(TrieNode node, TrieNode cachedNodeCopy)
        {
            _logger.Trace($"Replacing {node} with its cached copy {cachedNodeCopy}.");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowNodeIsNotSame(TrieNode node, TrieNode cachedNodeCopy) =>
            throw new InvalidOperationException($"The hash of replacement node {cachedNodeCopy} is not the same as the original {node}.");
    }

    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags)
    {
        if (_pruningStrategy.PruningEnabled)
        {
            if (_currentBlockCommitter is null) throw new InvalidOperationException($"With pruning triestore, {nameof(BeginBlockCommit)} must be called.");
        }

        return _currentBlockCommitter is not null
            // Note, archive node still use this path. This is because it needs the block commit set to handle reorg announcement.
            ? _currentBlockCommitter.GetTrieCommitter(address, root, writeFlags)
            // This happens when there are no block involved, such as during snap sync or just calculating patricia root.
            : new NonPruningTrieStoreCommitter(this, address, _nodeStorage.StartWriteBatch(), writeFlags);
    }

    public IBlockCommitter BeginBlockCommit(long blockNumber)
    {
        if (_pruningStrategy.PruningEnabled)
        {
            while (!Monitor.TryEnter(_dirtyNodesLock, TimeSpan.FromSeconds(10)))
            {
                if (_logger.IsInfo) _logger.Info("Waiting for state to be unlocked.");
            }

            if (_currentBlockCommitter is not null)
            {
                throw new InvalidOperationException("Cannot start a new block commit when an existing one is still not closed");
            }
        }

        _currentBlockCommitter = new BlockCommitter(this, CreateCommitSet(blockNumber));
        return _currentBlockCommitter;
    }

    private void FinishBlockCommit(BlockCommitSet set, TrieNode? root)
    {
        if (_logger.IsTrace) _logger.Trace($"Enqueued blocks {_commitSetQueue?.Count ?? 0}");
        set.Seal(root);

        bool shouldPersistSnapshot = _persistenceStrategy.ShouldPersist(set.BlockNumber);
        if (shouldPersistSnapshot)
        {
            // For safety we prefer to commit half of the batch rather than not commit at all.
            // Generally hanging nodes are not a problem in the DB but anything missing from the DB is.
            using INodeStorage.IWriteBatch currentBatch = _nodeStorage.StartWriteBatch();
            ParallelPersistBlockCommitSet(set);
        }

        set.Prune();

        _currentBlockCommitter = null;

        if (_pruningStrategy.PruningEnabled)
            Monitor.Exit(_dirtyNodesLock);

        Prune();
    }


    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    // Used in testing to not have to wait for condition.
    public event EventHandler? OnMemoryPruneCompleted;

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
            ThrowMissingNode(address, path, keccak);
        }

        return rlp;

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowMissingNode(Hash256? address, in TreePath path, Hash256 keccak)
        {
            throw new MissingTrieNodeException($"Node A:{address} P:{path} H:{keccak} is missing from the DB", address, path, keccak);
        }
    }

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => LoadRlp(address, path, hash, null, flags);
    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => TryLoadRlp(address, path, hash, null, flags);

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

    public IReadOnlyTrieStore AsReadOnly(INodeStorage? store = null) =>
        new ReadOnlyTrieStore(this, store);

    public bool IsNodeCached(Hash256? address, in TreePath path, Hash256? hash) => DirtyNodesIsNodeCached(new TrieStoreDirtyNodesCache.Key(address, path, hash));

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256? hash) =>
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
        if ((_pruningStrategy.ShouldPruneDirtyNode(DirtyMemoryUsedByDirtyCache) || _pruningStrategy.ShouldPrunePersistedNode(PersistedMemoryUsedByDirtyCache)) && _pruningTask.IsCompleted)
        {
            _pruningTask = Task.Run(() =>
            {
                lock (_dirtyNodesLock)
                {
                    if (_pruningStrategy.ShouldPruneDirtyNode(DirtyMemoryUsedByDirtyCache))
                    {
                        PersistAndPruneDirtyCache();
                    }

                    if (_pruningStrategy.ShouldPrunePersistedNode(PersistedMemoryUsedByDirtyCache))
                    {
                        PrunePersistedNodes();
                    }
                }
            });

            _pruningTask.ContinueWith(_ =>
            {
                OnMemoryPruneCompleted?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    private void PersistAndPruneDirtyCache()
    {
        try
        {
            // Flush ahead of time so that memtable is empty which prevent stalling when writing nodes.
            // Note, the WriteBufferSize * WriteBufferNumber need to be more than about 20% of pruning cache
            // otherwise, it may not fit the whole dirty cache.
            // Additionally, if (WriteBufferSize * (WriteBufferNumber - 1)) is already more than 20% of pruning
            // cache, it is likely that there are enough space for it on most time, except for syncing maybe.
            _nodeStorage.Flush(onlyWal: false);

            long start = Stopwatch.GetTimestamp();
            if (_logger.IsDebug) _logger.Debug($"Locked {nameof(TrieStore)} for pruning.");

            long memoryUsedByDirtyCache = DirtyMemoryUsedByDirtyCache;
            if (!_pruningTaskCancellationTokenSource.IsCancellationRequested &&
                _pruningStrategy.ShouldPruneDirtyNode(memoryUsedByDirtyCache))
            {
                SaveSnapshot();
                PruneCache();

                TimeSpan sw = Stopwatch.GetElapsedTime(start);
                long ms = (long)sw.TotalMilliseconds;
                Metrics.PruningTime = ms;
                if (_logger.IsInfo) _logger.Info($"Executed memory prune. Took {ms:0.##} ms. Dirty memory from {memoryUsedByDirtyCache / 1.MiB()}MB to {DirtyMemoryUsedByDirtyCache / 1.MiB()}MB");
            }

            if (_logger.IsDebug) _logger.Debug($"Pruning finished. Unlocked {nameof(TrieStore)}.");
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Pruning failed with exception.", e);
        }
    }

    private void SaveSnapshot()
    {
        if (_pruningStrategy.ShouldPruneDirtyNode(MemoryUsedByDirtyCache))
        {
            if (_logger.IsDebug) _logger.Debug("Elevated pruning starting");

            int count = _commitSetQueue?.Count ?? 0;
            if (count == 0) return;

            using ArrayPoolList<BlockCommitSet> toAddBack = new(count);
            using ArrayPoolList<BlockCommitSet> candidateSets = new(count);
            while (_commitSetQueue!.TryDequeue(out BlockCommitSet frontSet))
            {
                if (frontSet!.BlockNumber >= LatestCommittedBlockNumber - _maxDepth)
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

            Action<TreePath, Hash256?, TrieNode>? persistedNodeRecorder = shouldDeletePersistedNode ? _persistedNodeRecorder : null;

            for (int index = 0; index < candidateSets.Count; index++)
            {
                BlockCommitSet blockCommitSet = candidateSets[index];
                if (_logger.IsDebug) _logger.Debug($"Elevated pruning for candidate {blockCommitSet.BlockNumber}");
                ParallelPersistBlockCommitSet(blockCommitSet, persistedNodeRecorder);
            }

            AnnounceReorgBoundaries();

            if (candidateSets.Count > 0)
            {
                return;
            }

            _commitSetQueue.TryPeek(out BlockCommitSet? uselessFrontSet);
            if (_logger.IsDebug) _logger.Debug($"Found no candidate for elevated pruning (sets: {_commitSetQueue.Count}, earliest: {uselessFrontSet?.BlockNumber}, newest kept: {LatestCommittedBlockNumber}, reorg depth {_maxDepth})");
        }
    }

    private void PersistedNodeRecorder(TreePath treePath, Hash256 address, TrieNode tn)
    {
        if (treePath.Length <= TinyTreePath.MaxNibbleLength)
        {
            int shardIdx = GetNodeShardIdx(treePath, tn.Keccak);

            HashAndTinyPath key = new(address, new TinyTreePath(treePath));

            _persistedHashes[shardIdx].AddOrUpdate(key, _ => tn.Keccak, (_, _) => null);
        }
    }

    private int _lastPrunedShardIdx = 0;

    /// <summary>
    /// This method is responsible for reviewing the nodes that are directly in the cache and
    /// removing ones that are either no longer referenced but not for persisted nodes.
    /// This is done after a `SaveSnapshot`.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    private void PruneCache(bool prunePersisted = false, bool dontRemoveNodes = false, bool forceRemovePersistedNodes = false)
    {
        if (_logger.IsDebug) _logger.Debug($"Pruning nodes {DirtyMemoryUsedByDirtyCache / 1.MB()} MB , last persisted block: {LastPersistedBlockNumber} current: {LatestCommittedBlockNumber}.");
        long start = Stopwatch.GetTimestamp();

        for (int index = 0; index < _dirtyNodes.Length; index++)
        {
            int closureIndex = index;
            TrieStoreDirtyNodesCache dirtyNode = _dirtyNodes[closureIndex];
            _dirtyNodesTasks[closureIndex] = Task.Run(() =>
            {
                ConcurrentDictionary<HashAndTinyPath, Hash256?>? persistedHashes = null;
                if (_persistedHashes.Length > 0)
                {
                    persistedHashes = _persistedHashes[closureIndex];
                }

                INodeStorage nodeStorage = _nodeStorage;
                if (dontRemoveNodes) nodeStorage = null;

                dirtyNode
                    .PruneCache(
                        prunePersisted: prunePersisted,
                        forceRemovePersistedNodes: forceRemovePersistedNodes,
                        persistedHashes: persistedHashes,
                        nodeStorage: nodeStorage);
                persistedHashes?.NoResizeClear();
            });
        }

        Task.WaitAll(_dirtyNodesTasks);

        RecalculateTotalMemoryUsage();

        if (_logger.IsDebug) _logger.Debug($"Finished pruning nodes in {(long)Stopwatch.GetElapsedTime(start).TotalMilliseconds}ms {DirtyMemoryUsedByDirtyCache / 1.MB()} MB, last persisted block: {LastPersistedBlockNumber} current: {LatestCommittedBlockNumber}.");
    }

    /// <summary>
    /// Only prune persisted nodes. This method attempt to pick only some shard for pruning.
    /// </summary>
    private void PrunePersistedNodes()
    {
        try
        {
            long targetPruneMemory = (long)(PersistedMemoryUsedByDirtyCache * _prunePersistedNodePortion);
            targetPruneMemory = Math.Max(targetPruneMemory, _prunePersistedNodeMinimumTarget);

            int shardCountToPrune = (int)((targetPruneMemory / (double)PersistedMemoryUsedByDirtyCache) * _shardedDirtyNodeCount);
            shardCountToPrune = Math.Max(1, Math.Min(shardCountToPrune, _shardedDirtyNodeCount));

            if (_logger.IsWarn) _logger.Debug($"Pruning persisted nodes {PersistedMemoryUsedByDirtyCache / 1.MB()} MB, Pruning {shardCountToPrune} shards starting from shard {_lastPrunedShardIdx}");
            long start = Stopwatch.GetTimestamp();

            using ArrayPoolList<Task> pruneTask = new(shardCountToPrune);

            for (int i = 0; i < shardCountToPrune; i++)
            {
                TrieStoreDirtyNodesCache dirtyNode = _dirtyNodes[_lastPrunedShardIdx];
                pruneTask.Add(Task.Run(() =>
                {
                    dirtyNode.PruneCache(prunePersisted: true);
                }));
                _lastPrunedShardIdx = (_lastPrunedShardIdx + 1) % _shardedDirtyNodeCount;
            }

            Task.WaitAll(pruneTask.AsSpan());

            RecalculateTotalMemoryUsage();

            if (_logger.IsWarn) _logger.Debug($"Finished pruning persisted nodes in {(long)Stopwatch.GetElapsedTime(start).TotalMilliseconds}ms {PersistedMemoryUsedByDirtyCache / 1.MB()} MB, last persisted block: {LastPersistedBlockNumber} current: {LatestCommittedBlockNumber}.");
            Metrics.PersistedNodePruningTime = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Persisted node pruning failed", e);
        }
    }

    private void RecalculateTotalMemoryUsage()
    {
        long memory = 0;
        long dirtyMemory = 0;
        long totalNodes = 0;
        long totalDirtyNodes = 0;
        for (int index = 0; index < _dirtyNodes.Length; index++)
        {
            TrieStoreDirtyNodesCache dirtyNode = _dirtyNodes[index];
            memory += dirtyNode.TotalMemory;
            dirtyMemory += dirtyNode.TotalDirtyMemory;
            totalNodes += dirtyNode.Count;
            totalDirtyNodes += dirtyNode.DirtyCount;
        }

        MemoryUsedByDirtyCache = memory;
        DirtyMemoryUsedByDirtyCache = dirtyMemory;
        _totalCachedNodesCount = totalNodes;
        Metrics.CachedNodesCount = totalNodes;
        _dirtyNodesCount = totalDirtyNodes;
        Metrics.DirtyNodesCount = totalDirtyNodes;
    }

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

    private readonly INodeStorage _nodeStorage;

    private readonly TrieKeyValueStore _publicStore;

    private readonly IPruningStrategy _pruningStrategy;

    private readonly IPersistenceStrategy _persistenceStrategy;

    private readonly ILogger _logger;

    private ConcurrentQueue<BlockCommitSet>? _commitSetQueue;

    private ConcurrentQueue<BlockCommitSet> CommitSetQueue => _commitSetQueue ?? CreateQueueAtomic(ref _commitSetQueue);

    private BlockCommitSet? _lastCommitSet = null;

    private long _memoryUsedByDirtyCache;
    private long _dirtyMemoryUsedByDirtyCache;
    private long _totalCachedNodesCount;
    private long _dirtyNodesCount;

    private int _committedNodesCount;

    private int _persistedNodesCount;

    private long _latestPersistedBlockNumber;

    private BlockCommitter? _currentBlockCommitter = null;

    private long LatestCommittedBlockNumber { get; set; }
    public INodeStorage.KeyScheme Scheme => _nodeStorage.Scheme;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ConcurrentQueue<BlockCommitSet> CreateQueueAtomic(ref ConcurrentQueue<BlockCommitSet> val)
    {
        ConcurrentQueue<BlockCommitSet> instance = new();
        ConcurrentQueue<BlockCommitSet>? prior = Interlocked.CompareExchange(ref val, instance, null);
        return prior ?? instance;
    }

    private void VerifyNewCommitSet(long blockNumber)
    {
        if (_lastCommitSet is not null)
        {
            Debug.Assert(_lastCommitSet.IsSealed, "Not sealed when beginning new block");

            if (_lastCommitSet.BlockNumber != blockNumber - 1 && blockNumber != 0 && _lastCommitSet.BlockNumber != 0)
            {
                if (_logger.IsInfo) _logger.Info($"Non consecutive block commit. This is likely a reorg. Last block commit: {_lastCommitSet.BlockNumber}. New block commit: {blockNumber}.");
            }
        }
    }

    private BlockCommitSet CreateCommitSet(long blockNumber)
    {
        if (_logger.IsDebug) _logger.Debug($"Beginning new {nameof(BlockCommitSet)} - {blockNumber}");

        VerifyNewCommitSet(blockNumber);

        BlockCommitSet commitSet = new(blockNumber);
        CommitSetQueue.Enqueue(commitSet);

        _lastCommitSet = commitSet;

        LatestCommittedBlockNumber = Math.Max(blockNumber, LatestCommittedBlockNumber);
        // Why are we announcing **before** committing next block??
        // Should it be after commit?
        AnnounceReorgBoundaries();
        DequeueOldCommitSets();

        return commitSet;
    }

    /// <summary>
    /// Persists all transient (not yet persisted) starting from <paramref name="commitSet"/> root.
    /// Already persisted nodes are skipped. After this action we are sure that the full state is available
    /// for the block represented by this commit set.
    /// </summary>
    /// <param name="commitSet">A commit set of a block which root is to be persisted.</param>
    /// <param name="persistedNodeRecorder">Special action to be called on each persist. Used to track which node to remove.</param>
    /// <param name="writeFlags"></param>
    private void ParallelPersistBlockCommitSet(
        BlockCommitSet commitSet,
        Action<TreePath, Hash256?, TrieNode>? persistedNodeRecorder = null,
        WriteFlags writeFlags = WriteFlags.None
    )
    {
        INodeStorage.IWriteBatch topLevelWriteBatch = _nodeStorage.StartWriteBatch();
        const int parallelBoundaryPathLength = 2;

        using ArrayPoolList<(TrieNode trieNode, Hash256? address2, TreePath path)> parallelStartNodes = new(_shardedDirtyNodeCount);

        void TopLevelPersist(TrieNode tn, Hash256? address2, TreePath path)
        {
            if (path.Length < parallelBoundaryPathLength)
            {
                persistedNodeRecorder?.Invoke(path, address2, tn);
                PersistNode(address2, path, tn, topLevelWriteBatch, writeFlags);
            }
            else
            {
                parallelStartNodes.Add((tn, address2, path));
            }
        }

        if (_pruningStrategy.PruningEnabled)
        {
            if (_logger.IsInfo) _logger.Info($"Persisting from root {commitSet.Root?.Keccak?.ToShortString()} in block {commitSet.BlockNumber}");
        }
        else
        {
            if (_logger.IsDebug) _logger.Debug($"Persisting from root {commitSet.Root?.Keccak?.ToShortString()} in block {commitSet.BlockNumber}");
        }

        long start = Stopwatch.GetTimestamp();

        // The first CallRecursive stop at two level, yielding 256 node in parallelStartNodes, which is run concurrently
        TreePath path = TreePath.Empty;
        commitSet.Root?.CallRecursively(TopLevelPersist, null, ref path, GetTrieStore(null), true, _logger, maxPathLength: parallelBoundaryPathLength);

        // The amount of change in the subtrees are not balanced at all. So their writes areas buffered here
        // which get disposed in parallel instead of being disposed in `PersistNodeStartingFrom`.
        // This unfortunately is not atomic
        // However, anything that we are trying to persist here should still be in dirty cache.
        // So parallel read should go there first instead of to the database for these dataset,
        // so it should be fine for these to be non atomic.
        Task[] disposeTasks = _disposeTasks;
        Channel<INodeStorage.IWriteBatch> disposeQueue = Channel.CreateBounded<INodeStorage.IWriteBatch>(disposeTasks.Length * 2);
        try
        {
            for (int index = 0; index < disposeTasks.Length; index++)
            {
                disposeTasks[index] = Task.Run(async () =>
                {
                    await foreach (INodeStorage.IWriteBatch disposable in disposeQueue.Reader.ReadAllAsync())
                    {
                        disposable.Dispose();
                    }
                });
            }

            using ArrayPoolList<Task> persistNodeStartingFromTasks = parallelStartNodes.Select(
                entry => Task.Run(() => PersistNodeStartingFrom(entry.trieNode, entry.address2, entry.path, persistedNodeRecorder, writeFlags, disposeQueue)))
                .ToPooledList(parallelStartNodes.Count);

            Task.WaitAll(persistNodeStartingFromTasks.AsSpan());
        }
        finally
        {
            disposeQueue.Writer.Complete();
        }

        Task.WaitAll(disposeTasks);

        // Dispose top level last in case something goes wrong, at least the root won't be stored
        topLevelWriteBatch.Dispose();
        _nodeStorage.Flush(onlyWal: true);

        long elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Metrics.SnapshotPersistenceTime = elapsedMilliseconds;

        if (_logger.IsDebug) _logger.Debug($"Persisted trie from {commitSet.Root} at {commitSet.BlockNumber} in {elapsedMilliseconds}ms (cache memory {MemoryUsedByDirtyCache})");

        LastPersistedBlockNumber = commitSet.BlockNumber;
    }

    private async Task PersistNodeStartingFrom(TrieNode tn, Hash256 address2, TreePath path,
        Action<TreePath, Hash256?, TrieNode>? persistedNodeRecorder,
        WriteFlags writeFlags, Channel<INodeStorage.IWriteBatch> disposeQueue)
    {
        long persistedNodeCount = 0;
        INodeStorage.IWriteBatch writeBatch = _nodeStorage.StartWriteBatch();

        async ValueTask DoPersist(TrieNode node, Hash256? address3, TreePath path2)
        {
            persistedNodeRecorder?.Invoke(path2, address3, node);
            PersistNode(address3, path2, node, writeBatch, writeFlags);

            persistedNodeCount++;
            if (persistedNodeCount % 512 == 0)
            {
                await disposeQueue.Writer.WriteAsync(writeBatch);
                writeBatch = _nodeStorage.StartWriteBatch();
            }
        }

        await tn.CallRecursivelyAsync(DoPersist, address2, ref path, GetTrieStore(address2), _logger);
        await disposeQueue.Writer.WriteAsync(writeBatch);
    }

    private void PersistNode(Hash256? address, in TreePath path, TrieNode currentNode, INodeStorage.IWriteBatch writeBatch, WriteFlags writeFlags = WriteFlags.None)
    {
        ArgumentNullException.ThrowIfNull(currentNode);

        if (currentNode.Keccak is not null)
        {
            // Note that the LastSeen value here can be 'in the future' (greater than block number
            // if we replaced a newly added node with an older copy and updated the LastSeen value.
            // Here we reach it from the old root so it appears to be out of place but it is correct as we need
            // to prevent it from being removed from cache and also want to have it persisted.

            if (_logger.IsTrace) _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode}.");
            writeBatch.Set(address, path, currentNode.Keccak, currentNode.FullRlp, writeFlags);
            currentNode.IsPersisted = true;
            IncrementPersistedNodesCount();
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

    private bool IsNoLongerNeeded(long lastSeen)
    {
        Debug.Assert(lastSeen >= 0, $"Any node that is cache should have {nameof(TrieNode.LastSeen)} set.");
        return lastSeen < LastPersistedBlockNumber
               && lastSeen < LatestCommittedBlockNumber - _maxDepth;
    }

    private void DequeueOldCommitSets()
    {
        if (_commitSetQueue?.IsEmpty ?? true) return;

        while (_commitSetQueue.TryPeek(out BlockCommitSet blockCommitSet))
        {
            if (blockCommitSet.BlockNumber < LatestCommittedBlockNumber - _maxDepth - 1)
            {
                if (_logger.IsDebug) _logger.Debug($"Removing historical ({_commitSetQueue.Count}) {blockCommitSet.BlockNumber} < {LatestCommittedBlockNumber} - {_maxDepth}");
                _commitSetQueue.TryDequeue(out _);
            }
            else
            {
                break;
            }
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
            if (LatestCommittedBlockNumber >= LastPersistedBlockNumber + _maxDepth)
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
                else if (frontSet!.BlockNumber < LatestCommittedBlockNumber - _maxDepth
                         && frontSet!.BlockNumber > candidateSets[0].BlockNumber)
                {
                    candidateSets.Clear();
                    candidateSets.Add(frontSet);
                }
            }

            INodeStorage.IWriteBatch writeBatch = _nodeStorage.StartWriteBatch();
            for (int index = 0; index < candidateSets.Count; index++)
            {
                BlockCommitSet blockCommitSet = candidateSets[index];
                if (_logger.IsDebug) _logger.Debug($"Persisting on disposal {blockCommitSet} (cache memory at {MemoryUsedByDirtyCache})");
                ParallelPersistBlockCommitSet(blockCommitSet);
            }
            writeBatch.Dispose();
            _nodeStorage.Flush(onlyWal: false);

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
        if (_logger.IsInfo) _logger.Info("Full Pruning Persist Cache started.");

        lock (_dirtyNodesLock)
        {
            int commitSetCount = 0;
            long start = Stopwatch.GetTimestamp();
            // We persist all sealed Commitset causing PruneCache to almost completely clear the cache. Any new block that
            // need existing node will have to read back from db causing copy-on-read mechanism to copy the node.
            ConcurrentQueue<BlockCommitSet> commitSetQueue = CommitSetQueue;

            void ClearCommitSetQueue()
            {
                while (commitSetQueue.TryPeek(out BlockCommitSet commitSet) && commitSet.IsSealed)
                {
                    if (!commitSetQueue.TryDequeue(out commitSet)) break;
                    if (!commitSet.IsSealed)
                    {
                        // Oops
                        commitSetQueue.Enqueue(commitSet);
                        break;
                    }

                    commitSetCount++;
                    ParallelPersistBlockCommitSet(commitSet);
                }
            }

            if (_logger.IsInfo) _logger.Info($"Saving all commit set took {Stopwatch.GetElapsedTime(start)} for {commitSetCount} commit sets.");

            start = Stopwatch.GetTimestamp();

            // Double check
            ClearCommitSetQueue();
            if (cancellationToken.IsCancellationRequested) return;

            // All persisted node including recommitted nodes between head and reorg depth must be removed so that
            // it will be re-persisted or at least re-read in order to be cloned.
            // This should clear most nodes. For some reason, not all.
            PruneCache(prunePersisted: true, dontRemoveNodes: true, forceRemovePersistedNodes: true);
            if (cancellationToken.IsCancellationRequested) return;

            int totalPersistedCount = 0;
            for (int index = 0; index < _dirtyNodes.Length; index++)
            {
                TrieStoreDirtyNodesCache dirtyNode = _dirtyNodes[index];
                _dirtyNodesTasks[index] = Task.Run(() =>
                {
                    int persistedCount = dirtyNode.PersistAll(_nodeStorage, cancellationToken);
                    totalPersistedCount += persistedCount;
                });
            }

            Task.WaitAll(_dirtyNodesTasks);

            if (cancellationToken.IsCancellationRequested) return;

            PruneCache(prunePersisted: true, dontRemoveNodes: true, forceRemovePersistedNodes: true);

            long nodesCount = NodesCount();
            if (nodesCount != 0)
            {
                if (_logger.IsWarn) _logger.Warn($"{nodesCount} cache entry remains. {DirtyCachedNodesCount} dirty, total persistec count is {totalPersistedCount}.");
            }

            if (_logger.IsInfo) _logger.Info($"Clear cache took {Stopwatch.GetElapsedTime(start)}.");
        }
    }

    // Used to serve node by hash
    private byte[]? GetByHash(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        Hash256 asHash = new(key);
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

    private class TrieKeyValueStore(TrieStore trieStore) : IReadOnlyKeyValueStore
    {
        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => trieStore.GetByHash(key, flags);
    }

    public bool HasRoot(Hash256 stateRoot)
    {
        if (stateRoot == Keccak.EmptyTreeHash) return true;
        TrieNode node = FindCachedOrUnknown(null, TreePath.Empty, stateRoot, true);
        if (node.NodeType == NodeType.Unknown)
        {
            return TryLoadRlp(null, TreePath.Empty, node.Keccak!) is not null;
        }

        return true;
    }

    private class BlockCommitter(
        TrieStore trieStore,
        BlockCommitSet commitSet
    ) : IBlockCommitter
    {
        internal TrieNode? StateRoot;
        private int _concurrency = trieStore._pruningStrategy.PruningEnabled ? Environment.ProcessorCount : 0;

        public void Dispose()
        {
            trieStore.FinishBlockCommit(commitSet, StateRoot);
        }

        public ICommitter GetTrieCommitter(Hash256? address, TrieNode? root, WriteFlags writeFlags)
        {
            if (address is null) StateRoot = root;
            return trieStore._pruningStrategy.PruningEnabled
                ? new PruningTrieStoreCommitter(this, trieStore, commitSet.BlockNumber, address, root)
                : new NonPruningTrieStoreCommitter(trieStore, address, trieStore._nodeStorage.StartWriteBatch(), writeFlags);
        }

        public bool TryRequestConcurrencyQuota()
        {
            if (Interlocked.Decrement(ref _concurrency) >= 0)
            {
                return true;
            }

            ReturnConcurrencyQuota();
            return false;
        }

        public void ReturnConcurrencyQuota() => Interlocked.Increment(ref _concurrency);
    }

    private class PruningTrieStoreCommitter(
        BlockCommitter blockCommitter,
        TrieStore trieStore,
        long blockNumber,
        Hash256? address,
        TrieNode? root
    ) : ICommitter
    {
        private readonly bool _needToResetRoot = root is not null && root.IsDirty;

        public void Dispose()
        {
            if (_needToResetRoot)
            {
                // During commit it PatriciaTrie, the root may get resolved to an existing node (same keccak).
                // This ensure that the root that we use here is the same.
                // This is only needed for state tree as the root need to be put in the block commit set.
                if (address == null) blockCommitter.StateRoot = trieStore.FindCachedOrUnknown(address, TreePath.Empty, root?.Keccak);
            }
        }

        public void CommitNode(ref TreePath path, NodeCommitInfo nodeCommitInfo) =>
            trieStore.CommitAndInsertToDirtyNodes(blockNumber, address, ref path, nodeCommitInfo);

        public bool TryRequestConcurrentQuota() => blockCommitter.TryRequestConcurrencyQuota();

        public void ReturnConcurrencyQuota() => blockCommitter.ReturnConcurrencyQuota();
    }

    private class NonPruningTrieStoreCommitter(
        TrieStore trieStore,
        Hash256? address,
        INodeStorage.IWriteBatch writeBatch,
        WriteFlags writeFlags = WriteFlags.None
    ) : ICommitter
    {
        public void Dispose()
        {
            writeBatch.Dispose();
        }

        public void CommitNode(ref TreePath path, NodeCommitInfo nodeCommitInfo)
        {
            trieStore.CommitAndPersistNode(address, ref path, nodeCommitInfo, writeFlags: writeFlags, writeBatch: writeBatch);
        }

        public bool TryRequestConcurrentQuota() => false;

        public void ReturnConcurrencyQuota() { }
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
