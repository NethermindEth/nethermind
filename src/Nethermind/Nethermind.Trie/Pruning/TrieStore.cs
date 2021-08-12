//  Copyright (c) 2020 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core;
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
        private class DirtyNodesCache
        {
            private readonly TrieStore _trieStore;

            public DirtyNodesCache(TrieStore trieStore)
            {
                _trieStore = trieStore;
            }

            public void SaveInCache(TrieNode node)
            {
                Debug.Assert(node.Keccak is not null, "Cannot store in cache nodes without resolved key.");
                if (_objectsCache.TryAdd(node.Keccak!, node))
                {
                    _trieStore.MemoryUsedByDirtyCache += node.GetMemorySize(false);
                    Metrics.CachedNodesCount = _objectsCache.Count;
                }
            }

            public TrieNode FindCachedOrUnknown(Keccak hash)
            {
                bool isMissing = !_objectsCache.TryGetValue(hash, out TrieNode trieNode);
                if (isMissing)
                {
                    if (_trieStore._logger.IsTrace) _trieStore._logger.Trace($"Creating new node {trieNode}");
                    trieNode = new TrieNode(NodeType.Unknown, hash);
                    if (trieNode.Keccak is null)
                    {
                        throw new InvalidOperationException($"Adding node with null hash {trieNode}");
                    }

                    SaveInCache(trieNode);
                }
                else
                {
                    Metrics.LoadedFromCacheNodesCount++;
                }

                return trieNode;
            }

            public TrieNode FromCachedRlpOrUnknown(Keccak hash)
            {
                // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                bool isMissing = !_objectsCache.TryGetValue(hash, out TrieNode? trieNode);
                if (isMissing)
                {
                    trieNode = new TrieNode(NodeType.Unknown, hash);
                }
                else
                {
                    if (trieNode!.FullRlp is null)
                    {
                        // // this happens in SyncProgressResolver
                        // throw new InvalidAsynchronousStateException("Read only trie store is trying to read a transient node.");
                        return new TrieNode(NodeType.Unknown, hash);
                    }

                    // we returning a copy to avoid multithreaded access
                    trieNode = new TrieNode(NodeType.Unknown, hash, trieNode.FullRlp);
                    trieNode.ResolveNode(_trieStore);
                    trieNode.Keccak = hash;

                    Metrics.LoadedFromCacheNodesCount++;
                }

                if (_trieStore._logger.IsTrace) _trieStore._logger.Trace($"Creating new node {trieNode}");
                return trieNode;
            }

            public bool IsNodeCached(Keccak hash) => _objectsCache.ContainsKey(hash);

            public ConcurrentDictionary<Keccak, TrieNode> AllNodes => _objectsCache;

            private readonly ConcurrentDictionary<Keccak, TrieNode> _objectsCache = new();

            public int Count => _objectsCache.Count;

            public void Remove(Keccak hash)
            {
                _objectsCache.Remove(hash, out _);
            }

            public void Dump()
            {
                if (_trieStore._logger.IsTrace)
                {
                    _trieStore._logger.Trace($"Trie node dirty cache ({Count})");
                    foreach (KeyValuePair<Keccak, TrieNode> keyValuePair in _objectsCache)
                    {
                        _trieStore._logger.Trace($"  {keyValuePair.Value}");
                    }
                }
            }

            public void Clear()
            {
                _objectsCache.Clear();
                _trieStore.MemoryUsedByDirtyCache = 0;
            }
        }

        private int _isFirst;
        
        private readonly ThreadLocal<IBatch> _currentBatch = new();

        private readonly DirtyNodesCache _dirtyNodes;

        public TrieStore(IKeyValueStoreWithBatching? keyValueStore, ILogManager? logManager)
            : this(keyValueStore, No.Pruning, Pruning.Persist.EveryBlock, logManager)
        {
        }

        public TrieStore(
            IKeyValueStoreWithBatching? keyValueStore,
            IPruningStrategy? pruningStrategy,
            IPersistenceStrategy? persistenceStrategy,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<TrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _pruningStrategy = pruningStrategy ?? throw new ArgumentNullException(nameof(pruningStrategy));
            _persistenceStrategy = persistenceStrategy ?? throw new ArgumentNullException(nameof(persistenceStrategy));
            _dirtyNodes = new DirtyNodesCache(this);
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

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo)
        {
            if (blockNumber < 0) throw new ArgumentOutOfRangeException(nameof(blockNumber));
            EnsureCommitSetExistsForBlock(blockNumber);

            if (_logger.IsTrace) _logger.Trace($"Committing {nodeCommitInfo} at {blockNumber}");
            if (!nodeCommitInfo.IsEmptyBlockMarker)
            {
                TrieNode node = nodeCommitInfo.Node!;

                if (node!.Keccak is null)
                {
                    throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");
                }

                if (CurrentPackage is null)
                {
                    throw new TrieStoreException(
                        $"{nameof(CurrentPackage)} is NULL when committing {node} at {blockNumber}.");
                }

                if (node!.LastSeen.HasValue)
                {
                    throw new TrieStoreException(
                        $"{nameof(TrieNode.LastSeen)} set on {node} committed at {blockNumber}.");
                }

                node = SaveOrReplaceInDirtyNodesCache(nodeCommitInfo, node);
                node.LastSeen = blockNumber;

                if (!_pruningStrategy.PruningEnabled)
                {
                    Persist(node, blockNumber);
                }

                CommittedNodesCount++;
            }
        }

        private TrieNode SaveOrReplaceInDirtyNodesCache(NodeCommitInfo nodeCommitInfo, TrieNode node)
        {
            if (_pruningStrategy.PruningEnabled)
            {
                if (IsNodeCached(node.Keccak))
                {
                    TrieNode cachedNodeCopy = FindCachedOrUnknown(node.Keccak);
                    if (!ReferenceEquals(cachedNodeCopy, node))
                    {
                        if (_logger.IsTrace)
                            _logger.Trace($"Replacing {node} with its cached copy {cachedNodeCopy}.");
                        if (!nodeCommitInfo.IsRoot)
                        {
                            nodeCommitInfo.NodeParent!.ReplaceChildRef(nodeCommitInfo.ChildPositionAtParent,
                                cachedNodeCopy);
                        }

                        node = cachedNodeCopy;
                        Metrics.ReplacedNodesCount++;
                    }
                }
                else
                {
                    _dirtyNodes.SaveInCache(node);
                }
            }

            return node;
        }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root)
        {
            if (blockNumber < 0) throw new ArgumentOutOfRangeException(nameof(blockNumber));
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
                        if (_logger.IsTrace) _logger.Trace(
                            $"Current root (block {blockNumber}): {set.Root}, block {set.BlockNumber}");
                        set.Seal();
                    }

                    bool shouldPersistSnapshot = _persistenceStrategy.ShouldPersist(set.BlockNumber);
                    if (shouldPersistSnapshot)
                    {
                        Persist(set);
                    }
                    else
                    {
                        PruneCurrentSet();
                    }

                    CurrentPackage = null;
                }
            }
            finally
            {
                _currentBatch.Value?.Dispose();
                _currentBatch.Value = null;
            }
        }

        public void HackPersistOnShutdown()
        {
            PersistOnShutdown();
        }

        public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        internal byte[] LoadRlp(Keccak keccak, IKeyValueStore? keyValueStore)
        {
            keyValueStore ??= _keyValueStore;
            byte[]? rlp = _currentBatch.Value?[keccak.Bytes] ?? keyValueStore[keccak.Bytes];

            if (rlp is null)
            {
                throw new TrieException($"Node {keccak} is missing from the DB");
            }

            Metrics.LoadedFromDbNodesCount++;

            return rlp;
        }

        public byte[] LoadRlp(Keccak keccak) => LoadRlp(keccak, null);

        public IReadOnlyTrieStore AsReadOnly(IKeyValueStore? keyValueStore)
        {
            return new ReadOnlyTrieStore(this, keyValueStore);
        }

        public bool IsNodeCached(Keccak hash) => _dirtyNodes.IsNodeCached(hash);

        public TrieNode FindCachedOrUnknown(Keccak? hash)
        {
            return FindCachedOrUnknown(hash, false);
        }

        internal TrieNode FindCachedOrUnknown(Keccak? hash, bool isReadOnly)
        {
            if (hash is null)
            {
                throw new ArgumentNullException(nameof(hash));
            }

            if (!_pruningStrategy.PruningEnabled)
            {
                return new TrieNode(NodeType.Unknown, hash);
            }

            if (isReadOnly)
            {
                return _dirtyNodes.FromCachedRlpOrUnknown(hash);
            }
            else
            {
                return _dirtyNodes.FindCachedOrUnknown(hash);   
            }
        }

        public void Dump() => _dirtyNodes.Dump();

        public void Prune()
        {
            while (true)
            {
                if (!_pruningStrategy.ShouldPrune(MemoryUsedByDirtyCache))
                {
                    break;
                }

                PruneCache();

                if (CanPruneCacheFurther()) continue;
                break;
            }
        }

        private bool CanPruneCacheFurther()
        {
            if (_pruningStrategy.ShouldPrune(MemoryUsedByDirtyCache))
            {
                if (_logger.IsDebug) _logger.Debug("Elevated pruning starting");

                BlockCommitSet? candidateSet = null;
                while (_commitSetQueue.TryPeek(out BlockCommitSet? frontSet))
                {
                    if (frontSet!.BlockNumber >= LatestCommittedBlockNumber - Reorganization.MaxDepth)
                    {
                        break;
                    }

                    if (_commitSetQueue.TryDequeue(out frontSet))
                    {
                        candidateSet = frontSet;
                    }
                    else
                    {
                        break;
                    }
                }

                if (candidateSet is not null)
                {
                    if (_logger.IsDebug) _logger.Debug($"Elevated pruning for candidate {candidateSet.BlockNumber}");
                    Persist(candidateSet);
                    return true;
                }

                _commitSetQueue.TryPeek(out BlockCommitSet? uselessFrontSet);
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Found no candidate for elevated pruning (sets: {_commitSetQueue.Count}, earliest: {uselessFrontSet?.BlockNumber}, newest kept: {LatestCommittedBlockNumber}, reorg depth {Reorganization.MaxDepth})");
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
        private void PruneCache()
        {
            if (_logger.IsDebug)
                _logger.Debug(
                    $"Pruning nodes {MemoryUsedByDirtyCache / 1.MB()}MB , last persisted block: {LastPersistedBlockNumber} current: {LatestCommittedBlockNumber}.");
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<TrieNode> toRemove = new(); // TODO: resettable

            long newMemory = 0;
            foreach ((Keccak key, TrieNode node) in _dirtyNodes.AllNodes)
            {
                if (node.IsPersisted)
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing persisted {node} from memory.");
                    toRemove.Add(node);
                    if (node.Keccak is null)
                    {
                        node.ResolveKey(this, true); // TODO: hack
                        if (node.Keccak != key)
                        {
                            throw new InvalidOperationException($"Persisted {node} {key} != {node.Keccak}");
                        }
                    }

                    Metrics.PrunedPersistedNodesCount++;
                }
                else if (IsNoLongerNeeded(node))
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing {node} from memory (no longer referenced).");
                    toRemove.Add(node);
                    if (node.Keccak is null)
                    {
                        throw new InvalidOperationException($"Removed {node}");
                    }

                    Metrics.PrunedTransientNodesCount++;
                }
                else
                {
                    node.PrunePersistedRecursively(1);
                    newMemory += node.GetMemorySize(false);
                }
            }

            foreach (TrieNode trieNode in toRemove)
            {
                if (trieNode.Keccak is null)
                {
                    throw new InvalidOperationException($"{trieNode} has a null key");
                }

                _dirtyNodes.Remove(trieNode.Keccak!);
            }

            MemoryUsedByDirtyCache = newMemory;
            Metrics.CachedNodesCount = _dirtyNodes.Count;

            stopwatch.Stop();
            Metrics.PruningTime = stopwatch.ElapsedMilliseconds;
            if (_logger.IsDebug)
                _logger.Debug(
                    $"Finished pruning nodes in {stopwatch.ElapsedMilliseconds}ms {MemoryUsedByDirtyCache / 1.MB()}MB, last persisted block: {LastPersistedBlockNumber} current: {LatestCommittedBlockNumber}.");
        }

        /// <summary>
        /// This method is here to support testing.
        /// </summary>
        public void ClearCache() => _dirtyNodes.Clear();

        public void Dispose()
        {
            if (_logger.IsDebug) _logger.Debug("Disposing trie");
            PersistOnShutdown();
        }

        public void DequeueOldCommitSets()
        {
            while (_commitSetQueue.TryPeek(out BlockCommitSet blockCommitSet))
            {
                if (blockCommitSet.BlockNumber < LatestCommittedBlockNumber - Reorganization.MaxDepth - 1)
                {
                    if (_logger.IsDebug)
                        _logger.Debug(
                            $"Removing historical ({_commitSetQueue.Count}) {blockCommitSet.BlockNumber} < {LatestCommittedBlockNumber} - {Reorganization.MaxDepth}");
                    _commitSetQueue.TryDequeue(out _);
                }
                else
                {
                    break;
                }
            }
        }

        #region Private

        private readonly IKeyValueStoreWithBatching _keyValueStore;

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

        private void CreateCommitSet(long blockNumber)
        {
            if (_logger.IsDebug) _logger.Debug($"Beginning new {nameof(BlockCommitSet)} - {blockNumber}");

            // TODO: this throws on reorgs, does it not? let us recreate it in test
            Debug.Assert(CurrentPackage is null || blockNumber == CurrentPackage.BlockNumber + 1,
                "Newly begun block is not a successor of the last one");
            Debug.Assert(IsCurrentListSealed, "Not sealed when beginning new block");

            BlockCommitSet commitSet = new(blockNumber);
            _commitSetQueue.Enqueue(commitSet);
            LatestCommittedBlockNumber = Math.Max(blockNumber, LatestCommittedBlockNumber);
            AnnounceReorgBoundaries();
            DequeueOldCommitSets();
            Prune();

            CurrentPackage = commitSet;
            Debug.Assert(ReferenceEquals(CurrentPackage, commitSet),
                $"Current {nameof(BlockCommitSet)} is not same as the new package just after adding");
        }

        /// <summary>
        /// Persists all transient (not yet persisted) starting from <paramref name="commitSet"/> root.
        /// Already persisted nodes are skipped. After this action we are sure that the full state is available
        /// for the block represented by this commit set.
        /// </summary>
        /// <param name="commitSet">A commit set of a block which root is to be persisted.</param>
        private void Persist(BlockCommitSet commitSet)
        {
            void PersistNode(TrieNode tn) => Persist(tn, commitSet.BlockNumber);

            try
            {
                _currentBatch.Value ??= _keyValueStore.StartBatch();
                if (_logger.IsDebug) _logger.Debug($"Persisting from root {commitSet.Root} in {commitSet.BlockNumber}");

                Stopwatch stopwatch = Stopwatch.StartNew();
                commitSet.Root?.CallRecursively(PersistNode, this, true, _logger);
                stopwatch.Stop();
                Metrics.SnapshotPersistenceTime = stopwatch.ElapsedMilliseconds;

                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Persisted trie from {commitSet.Root} at {commitSet.BlockNumber} in {stopwatch.ElapsedMilliseconds}ms (cache memory {MemoryUsedByDirtyCache})");

                LastPersistedBlockNumber = commitSet.BlockNumber;
            }
            finally
            {
                // For safety we prefer to commit half of the batch rather than not commit at all.
                // Generally hanging nodes are not a problem in the DB but anything missing from the DB is.
                _currentBatch.Value?.Dispose();
                _currentBatch.Value = null;
            }

            PruneCurrentSet();
        }

        private void Persist(TrieNode currentNode, long blockNumber)
        {
            _currentBatch.Value ??= _keyValueStore.StartBatch();
            if (currentNode is null)
            {
                throw new ArgumentNullException(nameof(currentNode));
            }

            if (currentNode.Keccak is not null)
            {
                Debug.Assert(currentNode.LastSeen.HasValue,
                    $"Cannot persist a dangling node (without {(nameof(TrieNode.LastSeen))} value set).");
                // Note that the LastSeen value here can be 'in the future' (greater than block number
                // if we replaced a newly added node with an older copy and updated the LastSeen value.
                // Here we reach it from the old root so it appears to be out of place but it is correct as we need
                // to prevent it from being removed from cache and also want to have it persisted.

                if (_logger.IsTrace)
                    _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode} in snapshot {blockNumber}.");
                _currentBatch.Value[currentNode.Keccak.Bytes] = currentNode.FullRlp;
                currentNode.IsPersisted = true;
                currentNode.LastSeen = blockNumber;
                PersistedNodesCount++;
            }
            else
            {
                Debug.Assert(currentNode.FullRlp is not null && currentNode.FullRlp.Length < 32,
                    "We only expect persistence call without Keccak for the nodes that are kept inside the parent RLP (less than 32 bytes).");
            }
        }

        private bool IsNoLongerNeeded(TrieNode node)
        {
            Debug.Assert(node.LastSeen.HasValue,
                $"Any node that is cache should have {nameof(TrieNode.LastSeen)} set.");
            return node.LastSeen < LastPersistedBlockNumber
                   && node.LastSeen < LatestCommittedBlockNumber - Reorganization.MaxDepth;
        }

        private void EnsureCommitSetExistsForBlock(long blockNumber)
        {
            if (CurrentPackage is null)
            {
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
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Reached first commit - newest {LatestCommittedBlockNumber}, last persisted {LastPersistedBlockNumber}");
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
                if (LatestCommittedBlockNumber >= LastPersistedBlockNumber + Reorganization.MaxDepth)
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

        private bool _lastPersistedReachedReorgBoundary;

        private void PersistOnShutdown()
        {
            // here we try to shorten the number of blocks recalculated when restarting (so we force persist)
            // and we need to speed up the standard announcement procedure so we persists a block
            // from the past (by going max reorg back)

            BlockCommitSet? persistenceCandidate = null;
            bool firstCandidateFound = false;
            while (_commitSetQueue.TryDequeue(out BlockCommitSet? blockCommitSet))
            {
                if (blockCommitSet is not null)
                {
                    if (firstCandidateFound == false)
                    {
                        persistenceCandidate = blockCommitSet;
                        if (_logger.IsDebug) _logger.Debug($"New persistence candidate {persistenceCandidate}");
                        firstCandidateFound = true;
                        continue;
                    }

                    if (blockCommitSet.BlockNumber <= LatestCommittedBlockNumber - Reorganization.MaxDepth)
                    {
                        persistenceCandidate = blockCommitSet;
                        if (_logger.IsDebug) _logger.Debug($"New persistence candidate {persistenceCandidate}");
                    }
                }
                else
                {
                    if (_logger.IsDebug) _logger.Debug("Block commit was null...");
                }
            }

            if (_logger.IsDebug)
                _logger.Debug(
                    $"Persisting on disposal {persistenceCandidate} (cache memory at {MemoryUsedByDirtyCache})");
            if (persistenceCandidate is not null)
            {
                Persist(persistenceCandidate);
                AnnounceReorgBoundaries();
            }
        }

        #endregion
    }
}
