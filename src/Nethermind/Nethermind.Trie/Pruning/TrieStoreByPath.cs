// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpTest.Net.Collections;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.ByPath;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Trie store helps to manage trie commits block by block.
    /// If persistence and pruning are needed they have a chance to execute their behaviour on commits.
    /// </summary>
    public class TrieStoreByPath : ITrieStore
    {
        private static readonly byte[] _rootKeyPath = Array.Empty<byte>();

        private int _isFirst;

        private IBatch? _currentBatch = null;

        private readonly TrieNodePathCache _dirtyNodes;

        private bool _lastPersistedReachedReorgBoundary;
        private Task _pruningTask = Task.CompletedTask;
        private CancellationTokenSource _pruningTaskCancellationTokenSource = new();

        public TrieStoreByPath(IKeyValueStoreWithBatching? keyValueStore, ILogManager? logManager)
            : this(keyValueStore, No.Pruning, Pruning.Persist.EveryBlock, logManager)
        {
        }

        public TrieStoreByPath(
            IKeyValueStoreWithBatching? keyValueStore,
            IPruningStrategy? pruningStrategy,
            IPersistenceStrategy? persistenceStrategy,
            ILogManager? logManager,
            int historyBlockDepth = 0)
        {
            _logger = logManager?.GetClassLogger<TrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _persistenceStrategy = persistenceStrategy ?? throw new ArgumentNullException(nameof(persistenceStrategy));
            _dirtyNodes = new TrieNodePathCache(this, historyBlockDepth, logManager);
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

                _dirtyNodes.AddNode(blockNumber, node);
                node.LastSeen = Math.Max(blockNumber, node.LastSeen ?? 0);

                if (_dirtyNodes.MaxNumberOfBlocks == 0)
                    Persist(node, blockNumber, null);

                CommittedNodesCount++;
            }
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
                        if (_logger.IsTrace) _logger.Trace($"Current root (block {blockNumber}): {set.Root}, block {set.BlockNumber}");
                        set.Seal();
                    }

                    bool shouldPersistSnapshot = _persistenceStrategy.ShouldPersist(set.BlockNumber);
                    if (shouldPersistSnapshot)
                    {
                        Persist(set);
                    }
                    else
                    {
                        _dirtyNodes.Prune();
                    }
                    _dirtyNodes?.SetRootHashForBlock(set.BlockNumber, set.Root?.Keccak);

                    CurrentPackage = null;
                }
            }
            finally
            {
                _currentBatch?.Dispose();
                _currentBatch = null;
            }
        }

        public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        internal byte[] LoadRlp(Keccak keccak, IKeyValueStore? keyValueStore)
        {
            keyValueStore ??= _keyValueStore;
            byte[]? rlp = _currentBatch?[keccak.Bytes] ?? keyValueStore[keccak.Bytes];

            if (rlp is null)
            {
                throw new TrieException($"Node {keccak} is missing from the DB");
            }

            Metrics.LoadedFromDbNodesCount++;

            return rlp;
        }

        internal byte[] LoadRlp(Span<byte> path, IKeyValueStore? keyValueStore, Keccak rootHash = null)
        {
            byte[] keyPath = path.Length < 64 ?
                        Nibbles.ToEncodedStorageBytes(path) :
                        Nibbles.ToBytes(path);

            keyValueStore ??= _keyValueStore;
            byte[]? rlp = _currentBatch?[keyPath] ?? keyValueStore[keyPath];
            if (path.Length < 64 && rlp?.Length == 32)
            {
                byte[]? pointsToPath = _currentBatch?[rlp] ?? keyValueStore[rlp];
                if (pointsToPath is not null)
                    rlp = pointsToPath;
            }

            if (rlp is null)
            {
                throw new TrieException($"Node {keyPath} is missing from the DB");
            }

            Metrics.LoadedFromDbNodesCount++;

            return rlp;
        }

        public byte[] LoadRlp(Keccak keccak) => LoadRlp(keccak, null);
        public byte[] LoadRlp(Span<byte> path, Keccak rootHash) => LoadRlp(path, null, rootHash);

        public bool IsPersisted(Keccak keccak)
        {
            byte[]? rlp = _currentBatch?[keccak.Bytes] ?? _keyValueStore[keccak.Bytes];

            if (rlp is null)
            {
                return false;
            }

            Metrics.LoadedFromDbNodesCount++;

            return true;
        }

        public IReadOnlyTrieStore AsReadOnly(IKeyValueStore? keyValueStore)
        {
            return new ReadOnlyTrieStoreByPath(this, keyValueStore);
        }

        public TrieNode FindCachedOrUnknown(Keccak keccak)
        {
            return new TrieNode(NodeType.Unknown, keccak);
        }

        public TrieNode FindCachedOrUnknown(Keccak keccak, Span<byte> nodePath)
        {
            TrieNode node = _dirtyNodes.GetNode(nodePath.ToArray(), keccak);
            node ??= new TrieNode(NodeType.Unknown, nodePath);
            return node;
        }

        public TrieNode FindCachedOrUnknown(Span<byte> nodePath, Keccak rootHash)
        {
            TrieNode node = _dirtyNodes.GetNode(rootHash, nodePath.ToArray());
            node ??= new TrieNode(NodeType.Unknown, nodePath);
            return node;
        }

        internal TrieNode FindCachedOrUnknown(Keccak keccak, Span<byte> nodePath, bool isReadOnly)
        {
            return _dirtyNodes.GetNode(keccak, nodePath.ToArray());
        }

        //public void Dump() => _dirtyNodes.Dump();

        /// <summary>
        /// This method is here to support testing.
        /// </summary>
        //public void ClearCache() => _dirtyNodes.Clear();

        public void Dispose()
        {
            if (_logger.IsDebug) _logger.Debug("Disposing trie");
            _pruningTaskCancellationTokenSource.Cancel();
            _pruningTask.Wait();
            PersistOnShutdown();
        }

        #region Private

        private readonly IKeyValueStoreWithBatching _keyValueStore;

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

        public TrieNodeResolverCapability Capability => TrieNodeResolverCapability.Path;

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
        /// <param name="commitSet">A commit set of a block which root is to be persisted.</param>
        private void Persist(BlockCommitSet commitSet)
        {
            void PersistNode(TrieNode tn) => Persist(tn, commitSet.BlockNumber, commitSet.Root?.Keccak);

            try
            {
                _currentBatch ??= _keyValueStore.StartBatch();
                if (_logger.IsDebug) _logger.Debug($"Persisting from root {commitSet.Root} in {commitSet.BlockNumber}");

                Stopwatch stopwatch = Stopwatch.StartNew();
                commitSet.Root?.CallRecursively(PersistNode, this, true, _logger);
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
        }

        private void Persist(TrieNode currentNode, long blockNumber, Keccak? rootHash)
        {
            _currentBatch ??= _keyValueStore.StartBatch();
            if (currentNode is null)
            {
                throw new ArgumentNullException(nameof(currentNode));
            }

            if (currentNode.Keccak is not null)
            {
                Debug.Assert(blockNumber == TrieNode.LastSeenNotSet || currentNode.LastSeen != TrieNode.LastSeenNotSet, $"Cannot persist a dangling node (without {(nameof(TrieNode.LastSeen))} value set).");
                // Note that the LastSeen value here can be 'in the future' (greater than block number
                // if we replaced a newly added node with an older copy and updated the LastSeen value.
                // Here we reach it from the old root so it appears to be out of place but it is correct as we need
                // to prevent it from being removed from cache and also want to have it persisted.

                if (_logger.IsTrace) _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode} in snapshot {blockNumber}.");

                SaveNodeDirectly(blockNumber, currentNode, _currentBatch);

                currentNode.IsPersisted = true;
                currentNode.LastSeen = Math.Max(blockNumber, currentNode.LastSeen ?? 0);

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
            Debug.Assert(node.LastSeen != TrieNode.LastSeenNotSet, $"Any node that is cache should have {nameof(TrieNode.LastSeen)} set.");
            return node.LastSeen < LastPersistedBlockNumber
                   && node.LastSeen < LatestCommittedBlockNumber - Reorganization.MaxDepth;
        }

        private void DequeueOldCommitSets()
        {
            while (_commitSetQueue.TryPeek(out BlockCommitSet blockCommitSet))
            {
                if (blockCommitSet.BlockNumber < LatestCommittedBlockNumber - Reorganization.MaxDepth - 1)
                {
                    if (_logger.IsDebug) _logger.Debug($"Removing historical ({_commitSetQueue.Count}) {blockCommitSet.BlockNumber} < {LatestCommittedBlockNumber} - {Reorganization.MaxDepth}");
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
                CreateCommitSet(blockNumber);
            }
        }

        private void AnnounceReorgBoundaries()
        {
            if (LatestCommittedBlockNumber < 1)
            {
                return;
            }

            bool shouldAnnounceReorgBoundary = true;
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

        private void PersistOnShutdown()
        {
            //_dirtyNodes.PersistBlock(LatestCommittedBlockNumber);
        }

        #endregion

        public void PersistCache(IKeyValueStore store, CancellationToken cancellationToken)
        {
            //Task.Run(() =>
            //{
            //    const int million = 1_000_000;
            //    int persistedNodes = 0;
            //    Stopwatch stopwatch = Stopwatch.StartNew();

            //    void PersistNode(TrieNode n)
            //    {
            //        Keccak? hash = n.Keccak;
            //        if (hash?.Bytes is not null)
            //        {
            //            store[hash.Bytes] = n.FullRlp;
            //            int persistedNodesCount = Interlocked.Increment(ref persistedNodes);
            //            if (_logger.IsInfo && persistedNodesCount % million == 0)
            //            {
            //                _logger.Info($"Full Pruning Persist Cache in progress: {stopwatch.Elapsed} {persistedNodesCount / million:N} mln nodes persisted.");
            //            }
            //        }
            //    }

            //    if (_logger.IsInfo) _logger.Info($"Full Pruning Persist Cache started.");
            //    KeyValuePair<byte[], TrieNode>[] nodesCopy = _dirtyNodes.AllNodes.ToArray();
            //    Parallel.For(0, nodesCopy.Length, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount / 2 }, i =>
            //    {
            //        if (!cancellationToken.IsCancellationRequested)
            //        {
            //            nodesCopy[i].Value.CallRecursively(PersistNode, this, false, _logger, false);
            //        }
            //    });

            //    if (_logger.IsInfo) _logger.Info($"Full Pruning Persist Cache finished: {stopwatch.Elapsed} {persistedNodes / (double)million:N} mln nodes persisted.");
            //});
        }

        private byte[] EncodeLeafNode(TrieNode node)
        {
            return Array.Empty<byte>();
        }

        public void SaveNodeDirectly(long blockNumber, TrieNode trieNode)
        {
            SaveNodeDirectly(blockNumber, trieNode, _keyValueStore);
        }

        private void SaveNodeDirectly(long blockNumber, TrieNode trieNode, IKeyValueStore keyValueStore = null)
        {
            keyValueStore ??= _keyValueStore;

            byte[] fullPath = trieNode.FullPath;

            byte[] pathBytes = fullPath.Length < 64 ? Nibbles.ToEncodedStorageBytes(fullPath) : Nibbles.ToBytes(fullPath);
            if (trieNode.IsLeaf && (trieNode.Key.Length < 64 || trieNode.PathToNode.Length == 0))
            {
                //_logger.Info($"Saving node pointer - path to: {trieNode.PathToNode.ToHexString()} full path: {fullPath.ToHexString()}");
                byte[] pathToNodeBytes = Nibbles.ToEncodedStorageBytes(trieNode.PathToNode);
                keyValueStore[pathToNodeBytes] = pathBytes;
            }
            //_logger.Info($"Saving node - full path: {fullPath.ToHexString()} key: {trieNode.Key?.ToHexString()}");
            keyValueStore[pathBytes] = trieNode.FullRlp;
        }

        public bool ExistsInDB(Keccak hash, byte[] pathNibbles)
        {
            byte[] pathBytes = pathNibbles.Length < 64 ?
                Nibbles.ToEncodedStorageBytes(pathNibbles) : Nibbles.ToBytes(pathNibbles);

            byte[] rlp = _currentBatch?[pathBytes] ?? _keyValueStore[pathBytes];
            if (rlp is not null)
            {
                TrieNode node = new(NodeType.Unknown, rlp);
                node.ResolveKey(this, false);
                return node.Keccak == hash;
            }
            return false;
        }

        public byte[]? this[ReadOnlySpan<byte> key]
        {
            //get => _pruningStrategy.PruningEnabled
            //       && _dirtyNodes.AllNodes.TryGetValue(key.ToArray(), out TrieNode? trieNode)
            //       && trieNode is not null
            //       && trieNode.NodeType != NodeType.Unknown
            //       && trieNode.FullRlp is not null
            //    ? trieNode.FullRlp
            //    : _currentBatch?[key] ?? _keyValueStore[key];
            get => _currentBatch?[key] ?? _keyValueStore[key];
        }
    }
}
