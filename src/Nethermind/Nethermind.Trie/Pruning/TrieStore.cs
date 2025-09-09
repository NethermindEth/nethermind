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
    private readonly int _maxBufferedCommitCount;
    private readonly int _maxDepth;
    private readonly double _prunePersistedNodePortion;
    private readonly long _prunePersistedNodeMinimumTarget;

    private int _isFirst;

    private readonly TrieStoreDirtyNodesCache[] _dirtyNodes = [];
    private readonly Task[] _dirtyNodesTasks = [];
    private readonly ConcurrentDictionary<HashAndTinyPath, Hash256?>[] _persistedHashes = [];
    private readonly Action<TreePath, Hash256?, TrieNode> _persistedNodeRecorder;
    private readonly Action<TreePath, Hash256?, TrieNode> _persistedNodeRecorderNoop;
    private readonly Task[] _disposeTasks = new Task[RuntimeInformation.PhysicalCoreCount];

    // Is created when _scopeLock was acquired but _dirtyNodesLock was not, meaning _dirtyNodes is being used,
    // likely by memory pruning. Read and commit will get redirected to _commitBuffer in this case.
    private CommitBuffer? _commitBuffer = null;

    // Small optimization to not re-create CommitBuffer
    private CommitBuffer? _commitBufferUnused = null;

    private bool IsInCommitBufferMode => _commitBuffer is not null;

    // Only one scope can be active at the same time. Any mutation to trieStore as part of block processing need to
    // acquire _scopeLock.
    private readonly Lock _scopeLock = new Lock();

    // Protect _dirtyNodes from mutation. Used during memory pruning or WorldState scope.
    private readonly Lock _pruningLock = new Lock();

    private readonly bool _deleteOldNodes = false;
    private readonly bool _pastKeyTrackingEnabled = false;

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
        _persistedNodeRecorderNoop = PersistedNodeRecorderNoop;
        _maxDepth = pruningConfig.PruningBoundary;
        _prunePersistedNodePortion = pruningConfig.PrunePersistedNodePortion;
        _prunePersistedNodeMinimumTarget = pruningConfig.PrunePersistedNodeMinimumTarget;
        _maxBufferedCommitCount = pruningConfig.MaxBufferedCommitCount;

        _shardBit = pruningConfig.DirtyNodeShardBit;
        _shardedDirtyNodeCount = 1 << _shardBit;
        _dirtyNodes = new TrieStoreDirtyNodesCache[_shardedDirtyNodeCount];
        _dirtyNodesTasks = new Task[_shardedDirtyNodeCount];
        _persistedHashes = new ConcurrentDictionary<HashAndTinyPath, Hash256?>[_shardedDirtyNodeCount];
        for (int i = 0; i < _shardedDirtyNodeCount; i++)
        {
            _dirtyNodes[i] = new TrieStoreDirtyNodesCache(this, !_nodeStorage.RequirePath, _logger);
            _persistedHashes[i] = new ConcurrentDictionary<HashAndTinyPath, Hash256>();
        }

        _deleteOldNodes = _pruningStrategy.DeleteObsoleteKeys;
        _pastKeyTrackingEnabled = pruningConfig.TrackPastKeys && nodeStorage.RequirePath;
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

    private TrieNode CommitAndInsertToDirtyNodes(long blockNumber, Hash256? address, ref TreePath path, TrieNode node)
    {
        if (_logger.IsTrace) Trace(blockNumber, in node);
        if (!node.IsBoundaryProofNode)
        {
            if (node.Keccak is null)
            {
                ThrowUnknownHash(node);
            }

            if (IsInCommitBufferMode)
                node = _commitBuffer.SaveOrReplaceInDirtyNodesCache(address, ref path, node, blockNumber);
            else
                node = SaveOrReplaceInDirtyNodesCache(address, ref path, node, blockNumber);

            IncrementCommittedNodesCount();
        }

        return node;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(long blockNumber, in TrieNode node)
        {
            _logger.Trace($"Committing {node} at {blockNumber}");
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnknownHash(TrieNode node) => throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");
    }

    private int GetNodeShardIdx(in TreePath path, Hash256 hash)
    {
        // When enabled, the shard have dictionaries for tracking past path hash also.
        // So the same path need to be in the same shard for the remove logic to work.
        uint hashCode = (uint)(_pastKeyTrackingEnabled
            ? path.GetHashCode()
            : hash.GetHashCode());

        return (int)(hashCode % _shardedDirtyNodeCount);
    }

    private TrieStoreDirtyNodesCache GetDirtyNodeShard(in TrieStoreDirtyNodesCache.Key key) => _dirtyNodes[GetNodeShardIdx(key.Path, key.Keccak)];

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

    private bool DirtyNodesIsNodeCached(TrieStoreDirtyNodesCache.Key key) =>
        GetDirtyNodeShard(key).IsNodeCached(key);

    private TrieNode DirtyNodesFromCachedRlpOrUnknown(TrieStoreDirtyNodesCache.Key key) =>
        GetDirtyNodeShard(key).FromCachedRlpOrUnknown(key);

    private TrieNode DirtyNodesFindCachedOrUnknown(TrieStoreDirtyNodesCache.Key key) =>
        GetDirtyNodeShard(key).FindCachedOrUnknown(key);

    private TrieNode SaveOrReplaceInDirtyNodesCache(
        Hash256? address,
        ref TreePath path,
        TrieNode node,
        long blockNumber)
    {
        TrieStoreDirtyNodesCache shard = _dirtyNodes[GetNodeShardIdx(path, node.Keccak)];
        return SaveOrReplaceInDirtyNodesCache(shard, address, ref path, node, blockNumber);
    }

    private TrieNode SaveOrReplaceInDirtyNodesCache(
        TrieStoreDirtyNodesCache shard,
        Hash256? address,
        ref TreePath path,
        TrieNode node,
        long blockNumber
    )
    {
        TrieStoreDirtyNodesCache.Key key = new(address, path, node.Keccak);
        TrieNode cachedNodeCopy = shard.GetOrAdd(in key, new TrieStoreDirtyNodesCache.NodeRecord(node, blockNumber)).Node;
        if (!ReferenceEquals(cachedNodeCopy, node))
        {
            Metrics.ReplacedNodesCount++;
        }
        else
        {
            shard.IncrementMemory(node);
        }

        return cachedNodeCopy;
    }

    public IDisposable BeginScope(BlockHeader? baseBlock)
    {
        _scopeLock.Enter();
        if (_pruningLock.TryEnter())
        {
            // When in non commit buffer mode, FindCachedOrUnknown can also modify the dirty cache which has
            // a notable performance benefit. So we try to clear the buffer before.
            FlushCommitBufferNoLock();

            return new Reactive.AnonymousDisposable(() =>
            {
                _pruningLock.Exit();
                _scopeLock.Exit();
            });
        }

        // _dirtyNodesLock was not acquired, likely due to memory pruning.
        // Will continue with commit buffer.
        if (_commitBuffer is null) _commitBuffer = _commitBufferUnused ?? new CommitBuffer(this);
        if (_commitBuffer.CommitCount >= _maxBufferedCommitCount)
        {
            // Prevent commit buffer from becoming too large.
            // This only happen if dirty cache size is very large and during forward sync.
            if (_logger.IsDebug) _logger.Debug("Commit buffer too large. Flushing first.");

            // Blocks until memory pruning is finished

            while (!_pruningLock.TryEnter(TimeSpan.FromSeconds(10)))
            {
                if (_logger.IsInfo) _logger.Info("Commit buffer full. Waiting for state to be unlocked.");
            }

            FlushCommitBufferNoLock();

            return new Reactive.AnonymousDisposable(() =>
            {
                _pruningLock.Exit();
                _scopeLock.Exit();
            });
        }

        return new Reactive.AnonymousDisposable(() =>
        {
            _scopeLock.Exit();

            // Try exit and flush async
            Task.Factory.StartNew(TryExitCommitBufferMode);
        });
    }

    private void TryExitCommitBufferMode()
    {
        if (!IsInCommitBufferMode) return;

        if (_scopeLock.TryEnter())
        {
            try
            {
                if (_pruningLock.TryEnter())
                {
                    try
                    {
                        FlushCommitBufferNoLock();
                    }
                    finally
                    {
                        _pruningLock.Exit();
                    }
                }
            }
            finally
            {
                _scopeLock.Exit();
            }
        }
    }

    private void FlushCommitBufferNoLock()
    {
        if (!IsInCommitBufferMode) return;
        _commitBuffer.FlushToDirtyNodes();
        _commitBufferUnused = _commitBuffer;
        _commitBuffer = null;
    }

    ICommitter IScopableTrieStore.BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags)
    {
        if (_currentBlockCommitter is null) throw new InvalidOperationException($"With pruning triestore, {nameof(BeginBlockCommit)} must be called.");
        return _currentBlockCommitter.GetTrieCommitter(address, root, writeFlags);
    }

    public IBlockCommitter BeginBlockCommit(long blockNumber)
    {
        if (_currentBlockCommitter is not null) throw new InvalidOperationException("Cannot start a new block commit when an existing one is still not closed");

        if (_logger.IsDebug) _logger.Debug($"Beginning new {nameof(BlockCommitSet)} - {blockNumber}");
        VerifyNewCommitSet(blockNumber);

        BlockCommitSet commitSet = new BlockCommitSet(blockNumber);

        _currentBlockCommitter = new BlockCommitter(this, commitSet);
        return _currentBlockCommitter;
    }

    private void FinishBlockCommit(BlockCommitSet set, TrieNode? root)
    {
        if (_logger.IsTrace) _logger.Trace($"Enqueued blocks {_commitSetQueue?.Count ?? 0}");
        // Note: root is null when the state trie is empty. It will therefore make the block commit set not sealed.
        set.Seal(root);
        set.Prune();

        _currentBlockCommitter = null;
        _lastCommitSet = set;

        // Commit buffer mode would use the
        if (IsInCommitBufferMode)
        {
            _commitBuffer.EnqueueCommitSet(set);
        }
        else
        {
            PushToMainCommitSetQueue(set);
            Prune();
        }
    }

    private void PushToMainCommitSetQueue(BlockCommitSet set)
    {
        _commitSetQueue.Enqueue(set);
        LatestCommittedBlockNumber = Math.Max(set.BlockNumber, LatestCommittedBlockNumber);
        AnnounceReorgBoundaries();
    }

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        byte[]? rlp = _nodeStorage.Get(address, path, keccak, readFlags);

        if (rlp is not null)
        {
            Metrics.LoadedFromDbNodesCount++;
        }

        return rlp;
    }

    public byte[] LoadRlp(Hash256? address, in TreePath path, Hash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        byte[]? rlp = TryLoadRlp(address, path, keccak, readFlags);
        if (rlp is null)
        {
            ThrowMissingNode(address, path, keccak);
        }

        return rlp;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowMissingNode(Hash256? address, in TreePath path, Hash256 keccak)
        {
            throw new MissingTrieNodeException($"Node A:{address} P:{path} H:{keccak} is missing from the DB", address, path, keccak);
        }
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

    public IReadOnlyTrieStore AsReadOnly() => new ReadOnlyTrieStore(this);

    public bool IsNodeCached(Hash256? address, in TreePath path, Hash256? hash) => DirtyNodesIsNodeCached(new TrieStoreDirtyNodesCache.Key(address, path, hash));

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256? hash) =>
        FindCachedOrUnknown(address, path, hash, false);

    internal TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256? hash, bool isReadOnly)
    {
        ArgumentNullException.ThrowIfNull(hash);

        TrieStoreDirtyNodesCache.Key key = new TrieStoreDirtyNodesCache.Key(address, path, hash);
        return _commitBuffer is { } commitBuffer
            ? commitBuffer.FindCachedOrUnknown(key, isReadOnly)
            : FindCachedOrUnknown(key, isReadOnly);
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

    private TrieStoreState CaptureCurrentState()
    {
        return new TrieStoreState(PersistedMemoryUsedByDirtyCache, DirtyMemoryUsedByDirtyCache,
            LatestCommittedBlockNumber, LastPersistedBlockNumber);
    }

    public void Prune()
    {
        var state = CaptureCurrentState();
        if ((_pruningStrategy.ShouldPruneDirtyNode(state) || _pruningStrategy.ShouldPrunePersistedNode(state)) && _pruningTask.IsCompleted)
        {
            _pruningTask = Task.Run(() =>
            {
                using (var _ = _pruningLock.EnterScope())
                {
                    if (_pruningStrategy.ShouldPruneDirtyNode(CaptureCurrentState()))
                    {
                        PersistAndPruneDirtyCache();
                    }

                    if (_pruningStrategy.ShouldPrunePersistedNode(CaptureCurrentState()))
                    {
                        PrunePersistedNodes();
                    }
                }

                TryExitCommitBufferMode();
            });
        }
    }

    private void PersistAndPruneDirtyCache()
    {
        try
        {
            long start = Stopwatch.GetTimestamp();
            if (_logger.IsDebug) _logger.Debug($"Locked {nameof(TrieStore)} for pruning.");

            long memoryUsedByDirtyCache = DirtyMemoryUsedByDirtyCache;
            SaveSnapshot();

            // Full pruning may set delete obsolete keys to false
            PruneCache(dontRemoveNodes: !_pruningStrategy.DeleteObsoleteKeys);

            TimeSpan sw = Stopwatch.GetElapsedTime(start);
            long ms = (long)sw.TotalMilliseconds;
            Metrics.PruningTime = ms;
            if (_logger.IsInfo) _logger.Info($"Executed memory prune. Took {ms:0.##} ms. Dirty memory from {memoryUsedByDirtyCache / 1.MiB()}MB to {DirtyMemoryUsedByDirtyCache / 1.MiB()}MB");

            if (_logger.IsDebug) _logger.Debug($"Pruning finished. Unlocked {nameof(TrieStore)}.");
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Pruning failed with exception.", e);
        }
    }

    private void SaveSnapshot()
    {
        if (_logger.IsDebug) _logger.Debug("Elevated pruning starting");

        int count = _commitSetQueue?.Count ?? 0;
        if (count == 0) return;

        using ArrayPoolList<BlockCommitSet> candidateSets = DetermineCommitSetToPersistInSnapshot(count);

        bool shouldTrackPastKey =
            // Its disabled
            _pastKeyTrackingEnabled &&
            // Full pruning need to visit all node, so can't delete anything.
            (_deleteOldNodes
                // If more than one candidate set, its a reorg, we can't remove node as persisted node may not be canonical
                ? candidateSets.Count == 1
                // For archice node, it is safe to remove canon key from cache as it will just get re-loaded.
                : true);

        if (shouldTrackPastKey)
        {
            for (int i = 0; i < _shardedDirtyNodeCount; i++)
            {
                if (!_persistedHashes[i].IsEmpty)
                {
                    _logger.Error($"Shard {i} is not empty and contain {_persistedHashes[i].Count} item");
                }
            }
        }

        Action<TreePath, Hash256?, TrieNode> persistedNodeRecorder = shouldTrackPastKey ? _persistedNodeRecorder : _persistedNodeRecorderNoop;

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

    private ArrayPoolList<BlockCommitSet> DetermineCommitSetToPersistInSnapshot(int count)
    {
        ArrayPoolList<BlockCommitSet>? candidateSets = null;
        try
        {

            long lastBlockBeforeRorgBoundary = 0;
            foreach (BlockCommitSet blockCommitSet in _commitSetQueue)
            {
                if (!blockCommitSet.IsSealed) continue;
                if (blockCommitSet.BlockNumber <= LatestCommittedBlockNumber - _maxDepth && blockCommitSet.BlockNumber > lastBlockBeforeRorgBoundary)
                {
                    lastBlockBeforeRorgBoundary = blockCommitSet.BlockNumber;
                }
            }

            using ArrayPoolList<BlockCommitSet> toAddBack = new(count);
            candidateSets = new(count);
            while (_commitSetQueue.TryDequeue(out BlockCommitSet frontSet))
            {
                if (frontSet.BlockNumber == lastBlockBeforeRorgBoundary || (_persistenceStrategy.ShouldPersist(frontSet.BlockNumber) && frontSet.BlockNumber < lastBlockBeforeRorgBoundary))
                {
                    candidateSets.Add(frontSet);
                }
                else if (frontSet.BlockNumber >= LatestCommittedBlockNumber - _maxDepth)
                {
                    toAddBack.Add(frontSet);
                }
            }

            for (int index = 0; index < toAddBack.Count; index++)
            {
                _commitSetQueue.Enqueue(toAddBack[index]);
            }

            return candidateSets;
        }
        catch
        {
            candidateSets?.Dispose();
            throw;
        }
    }

    private void PersistedNodeRecorder(TreePath treePath, Hash256 address, TrieNode tn)
    {
        if (treePath.Length <= TinyTreePath.MaxNibbleLength)
        {
            int shardIdx = GetNodeShardIdx(treePath, tn.Keccak);

            HashAndTinyPath key = new(address, new TinyTreePath(treePath));

            if (_deleteOldNodes)
            {
                _persistedHashes[shardIdx].AddOrUpdate(
                    key,
                    static (_, newHash) => newHash,
                    static (_, hash, _) => null,
                    tn.Keccak);
            }
            else
            {
                _persistedHashes[shardIdx].AddOrUpdate(
                    key,
                    static (_, newHash) => newHash,
                    // When not deleting old nodes, key tracking is used to prune in memory cache. It is
                    // safe to accidentally remove key by taking non-canon block as canon as it will just load
                    // from disk again.
                    static (_, hash, _) => hash,
                    tn.Keccak);
            }
        }
    }

    private void PersistedNodeRecorderNoop(TreePath treePath, Hash256 address, TrieNode tn)
    {
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

    public void Dispose()
    {
        if (_logger.IsDebug) _logger.Debug("Disposing trie");
        _pruningTaskCancellationTokenSource.Cancel();
        _pruningTask.Wait();
        FlushNonBlockingBuffer();
        PersistOnShutdown();
    }

    private void FlushNonBlockingBuffer()
    {
        using var _ = _scopeLock.EnterScope();
        if (_commitBuffer is null) return;

        using var _2 = _pruningLock.EnterScope();

        FlushCommitBufferNoLock();
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

    private ConcurrentQueue<BlockCommitSet> _commitSetQueue = new ConcurrentQueue<BlockCommitSet>();

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
        Action<TreePath, Hash256?, TrieNode> persistedNodeRecorder,
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
                persistedNodeRecorder.Invoke(path, address2, tn);
                PersistNode(address2, path, tn, topLevelWriteBatch, writeFlags);
            }
            else
            {
                parallelStartNodes.Add((tn, address2, path));
            }
        }

        if (_logger.IsDebug) _logger.Debug($"Persisting from root {commitSet.Root?.Keccak?.ToShortString()} in block {commitSet.BlockNumber}");

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
        Action<TreePath, Hash256?, TrieNode> persistedNodeRecorder,
        WriteFlags writeFlags, Channel<INodeStorage.IWriteBatch> disposeQueue)
    {
        long persistedNodeCount = 0;
        INodeStorage.IWriteBatch writeBatch = _nodeStorage.StartWriteBatch();

        async ValueTask DoPersist(TrieNode node, Hash256? address3, TreePath path2)
        {
            persistedNodeRecorder.Invoke(path2, address3, node);
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
            if (_logger.IsTrace) _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode}.");
            writeBatch.Set(address, path, currentNode.Keccak, currentNode.FullRlp.Span, writeFlags);
            currentNode.IsPersisted = true;
            IncrementPersistedNodesCount();
        }
        else
        {
            Debug.Assert(currentNode.FullRlp.IsNotNull && currentNode.FullRlp.Length < 32,
                "We only expect persistence call without Keccak for the nodes that are kept inside the parent RLP (less than 32 bytes).");
        }
    }

    public bool IsNoLongerNeeded(long lastCommit)
    {
        return lastCommit < LastPersistedBlockNumber
               && lastCommit < LatestCommittedBlockNumber - _maxDepth;
    }

    private void AnnounceReorgBoundaries()
    {
        if (LatestCommittedBlockNumber < 1)
        {
            return;
        }

        bool shouldAnnounceReorgBoundary = false;
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
        if (_commitSetQueue?.IsEmpty ?? true) return;

        using ArrayPoolList<BlockCommitSet> candidateSets = DetermineCommitSetToPersistInSnapshot(_commitSetQueue.Count);
        if (candidateSets.Count == 0 && _commitSetQueue.TryDequeue(out BlockCommitSet anyCommmitSet))
        {
            // No commitset to persist, likely as not enough block was processed to reached prune boundary
            // This happens when node is shutdown right after sync.
            // we need to persist at least something or in case of fresh sync or the best persisted state will not be set
            // at all. This come at a risk that this commitset is not canon though.
            candidateSets.Add(anyCommmitSet);
        }

        INodeStorage.IWriteBatch writeBatch = _nodeStorage.StartWriteBatch();
        for (int index = 0; index < candidateSets.Count; index++)
        {
            BlockCommitSet blockCommitSet = candidateSets[index];
            if (_logger.IsDebug) _logger.Debug($"Persisting on disposal {blockCommitSet} (cache memory at {MemoryUsedByDirtyCache})");
            ParallelPersistBlockCommitSet(blockCommitSet, _persistedNodeRecorderNoop);
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

    public void PersistCache(CancellationToken cancellationToken)
    {
        if (_logger.IsInfo) _logger.Info("Full Pruning Persist Cache started.");
        using var _ = _pruningLock.EnterScope();

        long start = Stopwatch.GetTimestamp();
        int commitSetCount = 0;
        // We persist all sealed Commitset causing PruneCache to almost completely clear the cache. Any new block that
        // need existing node will have to read back from db causing copy-on-read mechanism to copy the node.
        ConcurrentQueue<BlockCommitSet> commitSetQueue = _commitSetQueue;

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
                ParallelPersistBlockCommitSet(commitSet, _persistedNodeRecorderNoop);
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

    // Used to serve node by hash
    private byte[]? GetByHash(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        Hash256 asHash = new(key);
        return DirtyNodesTryGetValue(new TrieStoreDirtyNodesCache.Key(null, TreePath.Empty, asHash), out TrieNode? trieNode)
               && trieNode is not null
               && trieNode.NodeType != NodeType.Unknown
               && trieNode.FullRlp.IsNotNull
            ? trieNode.FullRlp.ToArray()
            : _nodeStorage.Get(null, TreePath.Empty, asHash, flags);
    }

    public IReadOnlyKeyValueStore TrieNodeRlpStore => _publicStore;

    public StableLockScope PrepareStableState(CancellationToken cancellationToken)
    {
        var scopeLockScope = _scopeLock.EnterScope();
        var pruneLockScope = _pruningLock.EnterScope();

        try
        {
            FlushCommitBufferNoLock();
            PersistCache(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            pruneLockScope.Dispose();
            scopeLockScope.Dispose();
            throw;
        }

        return new StableLockScope()
        {
            scopeLockScope = scopeLockScope,
            pruneLockScope = pruneLockScope,
        };
    }

    public ref struct StableLockScope : IDisposable
    {
        public Lock.Scope scopeLockScope;
        public Lock.Scope pruneLockScope;

        public void Dispose()
        {
            pruneLockScope.Dispose();
            scopeLockScope.Dispose();
        }
    }

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
        private int _concurrency = Environment.ProcessorCount;

        public void Dispose()
        {
            trieStore.FinishBlockCommit(commitSet, StateRoot);
        }

        public ICommitter GetTrieCommitter(Hash256? address, TrieNode? root, WriteFlags writeFlags)
        {
            if (address is null) StateRoot = root;
            return new PruningTrieStoreCommitter(this, trieStore, commitSet.BlockNumber, address, root);
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
                if (address == null) blockCommitter.StateRoot = ((IScopableTrieStore)trieStore).FindCachedOrUnknown(address, TreePath.Empty, root?.Keccak);
            }
        }

        public TrieNode CommitNode(ref TreePath path, TrieNode node) =>
            trieStore.CommitAndInsertToDirtyNodes(blockNumber, address, ref path, node);

        public bool TryRequestConcurrentQuota() => blockCommitter.TryRequestConcurrencyQuota();

        public void ReturnConcurrencyQuota() => blockCommitter.ReturnConcurrencyQuota();
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

    private class CommitBuffer
    {
        private readonly ConcurrentQueue<BlockCommitSet> _commitSetQueueBuffer = new();
        private readonly TrieStoreDirtyNodesCache[] _dirtyNodesBuffer;
        private readonly TrieStore _trieStore;
        private readonly ILogger _logger;

        public int CommitCount => _commitSetQueueBuffer.Count;

        public CommitBuffer(TrieStore trieStore)
        {
            _trieStore = trieStore;
            _logger = trieStore._logger;
            _dirtyNodesBuffer = new TrieStoreDirtyNodesCache[trieStore._dirtyNodes.Length];
            for (int i = 0; i < trieStore._shardedDirtyNodeCount; i++)
            {
                _dirtyNodesBuffer[i] = new TrieStoreDirtyNodesCache(trieStore, !trieStore._nodeStorage.RequirePath, trieStore._logger);
            }
        }

        public void EnqueueCommitSet(BlockCommitSet set)
        {
            _commitSetQueueBuffer.Enqueue(set);
        }

        public void FlushToDirtyNodes()
        {
            if (_logger.IsDebug) _logger.Debug("Flushing commit buffer");
            long startTime = Stopwatch.GetTimestamp();
            Parallel.For(0, _trieStore._shardedDirtyNodeCount, (i) =>
            {
                _dirtyNodesBuffer[i].CopyTo(_trieStore._dirtyNodes[i]);
            });

            int count = 0;
            while (_commitSetQueueBuffer.TryDequeue(out BlockCommitSet commitSet))
            {
                _trieStore._commitSetQueue.Enqueue(commitSet);
                _trieStore.PushToMainCommitSetQueue(commitSet);
                count++;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTime);
            if (_logger.IsDebug) _logger.Debug($"Flushed {count} commit buffers in {elapsed.Milliseconds}ms");
        }

        private TrieStoreDirtyNodesCache GetDirtyNodeShard(in TreePath path, Hash256 keccak) => _dirtyNodesBuffer[_trieStore.GetNodeShardIdx(path, keccak)];

        public TrieNode SaveOrReplaceInDirtyNodesCache(Hash256? address, ref TreePath path, in TrieNode node, long blockNumber)
        {
            // Change the shard to the one from commit buffer.
            TrieStoreDirtyNodesCache shard = GetDirtyNodeShard(path, node.Keccak);
            return _trieStore.SaveOrReplaceInDirtyNodesCache(shard, address, ref path, node, blockNumber);
        }

        public TrieNode FindCachedOrUnknown(TrieStoreDirtyNodesCache.Key key, bool isReadOnly)
        {
            int shardIdx = _trieStore.GetNodeShardIdx(key.Path, key.Keccak);
            TrieStoreDirtyNodesCache bufferShard = _dirtyNodesBuffer[shardIdx];
            TrieStoreDirtyNodesCache mainShard = _trieStore._dirtyNodes[shardIdx];

            var hasInBuffer = bufferShard.TryGetValue(key, out TrieNode bufferNode);
            if (!hasInBuffer && mainShard.TryGetValue(key, out TrieNode mainNode))
            {
                // Need to migrate the shard
                bufferShard.GetOrAdd(key, new TrieStoreDirtyNodesCache.NodeRecord(mainNode, -1));
                if (!isReadOnly) return mainNode;
            }

            if (!isReadOnly && hasInBuffer)
            {
                return bufferNode;
            }

            return isReadOnly ? bufferShard.FromCachedRlpOrUnknown(key) : bufferShard.FindCachedOrUnknown(key);
        }
    }
}
