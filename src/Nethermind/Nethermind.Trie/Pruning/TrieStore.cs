//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Currently the responsibility of this class is to decide how many blocks should be kept as active roots,
    /// manage the reorgs, snapshotting, and some of the cache pruning decision making.
    /// (does it sound like single responsibility to you?) 
    /// </summary>
    public class TrieStore : ITrieStore
    {
        public TrieStore(IKeyValueStore? keyValueStore, ILogManager? logManager) 
            :this(keyValueStore, No.Pruning, Full.Archive, logManager) { }
        
        public TrieStore(
            IKeyValueStore? keyValueStore,
            IPruningStrategy? pruningStrategy,
            IPersistenceStrategy? snapshotStrategy,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<TrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _pruningStrategy = pruningStrategy ?? throw new ArgumentNullException(nameof(pruningStrategy));
            _snapshotStrategy = snapshotStrategy ?? throw new ArgumentNullException(nameof(snapshotStrategy));
        }

        public int CommittedNodeCount
        {
            get => _committedNodeCount;
            private set
            {
                Metrics.CommittedNodesCount = value;
                _committedNodeCount = value;
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
                Metrics.CachedNodesCount = _trieNodeCache.Count;
                return _trieNodeCache.Count;
            }
        }

        public void CommitOneNode(long blockNumber, NodeCommitInfo nodeCommitInfo)
        {
            if (blockNumber < 0)
                throw new ArgumentOutOfRangeException(nameof(blockNumber));

            bool shouldBeginNewPackage = CurrentPackage == null || CurrentPackage.BlockNumber != blockNumber;
            if (shouldBeginNewPackage)
            {
                BeginNewPackage(blockNumber);
            }

            if (_logger.IsTrace) _logger.Trace($"Committing {blockNumber} {nodeCommitInfo}");

            if (!nodeCommitInfo.IsEmptyBlockMarker)
            {
                Debug.Assert(CurrentPackage != null, "Current package is null when enqueing a trie node.");
                Debug.Assert(!nodeCommitInfo.Node!.LastSeen.HasValue, "Committing a block.");

                TrieNode trieNode = nodeCommitInfo.Node;
                if (trieNode!.Keccak == null)
                {
                    throw new InvalidOperationException(
                        $"Hash of the node {trieNode} should be known at the time of committing.");
                }

                if (IsInMemory(trieNode.Keccak))
                {
                    TrieNode cachedReplacement = FindCachedOrUnknown(trieNode.Keccak);
                    if (!ReferenceEquals(cachedReplacement, trieNode))
                    {
                        if (_logger.IsTrace)
                            _logger.Trace(
                                $"Replacing a {nameof(trieNode)} object {trieNode} with its cached representation {cachedReplacement}.");
                        if (!nodeCommitInfo.IsRoot)
                        {
                            nodeCommitInfo.NodeParent!.ReplaceChildRef(nodeCommitInfo.ChildPositionAtParent, cachedReplacement);
                        }

                        trieNode = cachedReplacement;
                        Metrics.ReplacedNodesCount++;
                    }
                }
                else
                {
                    SaveInCache(trieNode);
                }

                trieNode.LastSeen = blockNumber;
                CommittedNodeCount++;
            }
        }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root)
        {
            bool shouldBeginNewPackage = CurrentPackage == null || CurrentPackage.BlockNumber != blockNumber;
            if (shouldBeginNewPackage)
            {
                BeginNewPackage(blockNumber);
            }

            if (trieType == TrieType.State) // storage tries happen before state commits
            {
                if (_logger.IsTrace) _logger.Trace($"Enqueued packages {_blockCommitsQueue.Count}");
                BlockCommitPackage package = CurrentPackage;
                if (package != null)
                {
                    package.Root = root;
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"Current root (block {blockNumber}): {package.Root}, block {package.BlockNumber}");
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"Incrementing refs from block {blockNumber} root {package.Root?.ToString() ?? "NULL"} ");

                    package.Seal();

                    TryRemovingOldBlock();
                }
            }
        }

        public void UndoOneBlock()
        {
            if (!_blockCommitsQueue.Any())
            {
                throw new InvalidOperationException(
                    $"Trying to unwind a {nameof(BlockCommitPackage)} when the queue is empty.");
            }

            if (_logger.IsDebug) _logger.Debug($"Unwinding {CurrentPackage}");

            _blockCommitsQueue.RemoveLast();
        }

        public event EventHandler<BlockNumberEventArgs>? SnapshotTaken;

        public void Flush()
        {
            if (_logger.IsDebug)
                _logger.Debug($"Flushing trie cache - in memory {_trieNodeCache.Count} " +
                              $"| commit count {CommittedNodeCount} " +
                              $"| save count {PersistedNodesCount}");

            CurrentPackage?.Seal();
            while (TryRemovingOldBlock())
            {
            }

            if (_logger.IsDebug)
                _logger.Debug($"Flushed trie cache - in memory {_trieNodeCache.Count} " +
                              $"| commit count {CommittedNodeCount} " +
                              $"| save count {PersistedNodesCount}");
        }

        public byte[]? LoadRlp(Keccak keccak, bool allowCaching)
        {
            byte[] rlp = null;
            if (allowCaching)
            {
                // TODO: static NodeCache in PatriciaTrie stays for now to simplify the PR
                rlp = PatriciaTree.NodeCache.Get(keccak);
            }

            if (rlp is null)
            {
                rlp = _keyValueStore[keccak.Bytes];
                if (rlp == null)
                {
                    throw new TrieException($"Node {keccak} is missing from the DB");
                }

                Metrics.LoadedFromDbNodesCount++;
                PatriciaTree.NodeCache.Set(keccak, rlp);
            }
            else
            {
                Metrics.LoadedFromRlpCacheNodesCount++;
            }

            return rlp;
        }

        public bool IsInMemory(Keccak hash) => _trieNodeCache.ContainsKey(hash);

        public TrieNode FindCachedOrUnknown(Keccak hash)
        {
            bool isMissing = !_trieNodeCache.TryGetValue(hash, out TrieNode trieNode);
            if (isMissing)
            {
                trieNode = new TrieNode(NodeType.Unknown, hash);
                if (_logger.IsTrace) _logger.Trace($"Creating new node {trieNode}");
                _trieNodeCache.TryAdd(trieNode.Keccak!, trieNode);
            }
            else
            {
                Metrics.LoadedFromCacheNodesCount++;
            }

            return trieNode;
        }

        public void Dump()
        {
            if (_logger.IsTrace)
            {
                _logger.Trace($"Trie node cache ({_trieNodeCache.Count})");
                // return;
                foreach (KeyValuePair<Keccak, TrieNode> keyValuePair in _trieNodeCache)
                {
                    _logger.Trace($"  {keyValuePair.Value}");
                }
            }
        }

        public void Prune(long snapshotId)
        {
            List<Keccak> toRemove = new List<Keccak>(); // TODO: resettable
            foreach ((Keccak key, TrieNode value) in _trieNodeCache)
            {
                if (value.IsPersisted)
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing persisted {value} from memory.");
                    toRemove.Add(key);
                }
                else if (HasBeenRemoved(value, snapshotId))
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing {value} from memory (no longer referenced).");
                    toRemove.Add(key);
                }
            }

            foreach (Keccak keccak in toRemove)
            {
                _trieNodeCache.Remove(keccak);
                Metrics.PrunedNodesCount++;
            }
            
            Metrics.CachedNodesCount = _trieNodeCache.Count;
        }

        public void ClearCache()
        {
            _trieNodeCache.Clear();
        }

        #region Private

        private readonly IKeyValueStore _keyValueStore;

        private Dictionary<Keccak, TrieNode> _trieNodeCache = new Dictionary<Keccak, TrieNode>();

        private readonly IPruningStrategy _pruningStrategy;

        private readonly IPersistenceStrategy _snapshotStrategy;

        private readonly ILogger _logger;

        private LinkedList<BlockCommitPackage> _blockCommitsQueue = new LinkedList<BlockCommitPackage>();

        private int _committedNodeCount;

        private int _persistedNodesCount;

        private BlockCommitPackage? CurrentPackage => _blockCommitsQueue.Last?.Value;

        private bool IsCurrentPackageSealed => CurrentPackage == null || CurrentPackage.IsSealed;

        private long OldestKeptBlockNumber => _blockCommitsQueue.First!.Value.BlockNumber;

        private long NewestKeptBlockNumber { get; set; }

        private void BeginNewPackage(long blockNumber)
        {
            if (_logger.IsDebug)
                _logger.Debug($"Beginning new {nameof(BlockCommitPackage)} - {blockNumber}");

            Debug.Assert(CurrentPackage == null || blockNumber == CurrentPackage.BlockNumber + 1,
                "Newly begun block is not a successor of the last one");

            Debug.Assert(IsCurrentPackageSealed, "Not sealed when beginning new block");

            BlockCommitPackage newPackage = new BlockCommitPackage(blockNumber);
            _blockCommitsQueue.AddLast(newPackage);
            NewestKeptBlockNumber = Math.Max(blockNumber, NewestKeptBlockNumber);
            
            // TODO: memory should be taken from the cache now
            while (_pruningStrategy.ShouldPrune(OldestKeptBlockNumber, NewestKeptBlockNumber, 0))
            {
                TryRemovingOldBlock();
            }

            Debug.Assert(CurrentPackage == newPackage,
                "Current package is not equal the new package just after adding");
        }

        internal bool TryRemovingOldBlock()
        {
            BlockCommitPackage? blockCommit = _blockCommitsQueue.First?.Value;
            bool hasAnySealedBlockInQueue = blockCommit != null && blockCommit.IsSealed;
            if (hasAnySealedBlockInQueue)
            {
                RemoveOldBlock(blockCommit);
            }

            return hasAnySealedBlockInQueue;
        }

        private void SaveInCache(TrieNode trieNode)
        {
            Debug.Assert(trieNode.Keccak != null, "Cannot store in cache nodes without resolved key.");
            _trieNodeCache[trieNode.Keccak!] = trieNode;
            Metrics.CachedNodesCount = _trieNodeCache.Count;
        }

        private void RemoveOldBlock(BlockCommitPackage commitPackage)
        {
            _blockCommitsQueue.RemoveFirst();
            
            if (_logger.IsDebug)
                _logger.Debug($"Start pruning {nameof(BlockCommitPackage)} - {commitPackage.BlockNumber}");

            Debug.Assert(commitPackage != null && commitPackage.IsSealed,
                $"Invalid {nameof(commitPackage)} - {commitPackage} received for pruning.");

            bool shouldPersistSnapshot = _snapshotStrategy.ShouldPersistSnapshot(commitPackage.BlockNumber);
            if (shouldPersistSnapshot)
            {
                if(_logger.IsDebug) _logger.Debug($"Persisting from root {commitPackage.Root} in {commitPackage.BlockNumber}");
                
                Stopwatch stopwatch = Stopwatch.StartNew();
                commitPackage.Root?.PersistRecursively(tn => Persist(tn, commitPackage.BlockNumber), this, _logger);
                stopwatch.Stop();
                Metrics.SnapshotPersistenceTime = stopwatch.ElapsedMilliseconds;
                
                stopwatch.Restart();
                Prune(commitPackage.BlockNumber); // for now can only prune on snapshot?
                stopwatch.Stop();
                Metrics.PruningTime = stopwatch.ElapsedMilliseconds;
            }

            if (_logger.IsDebug)
                _logger.Debug($"End pruning {nameof(BlockCommitPackage)} - {commitPackage.BlockNumber}");

            Dump();
            if (shouldPersistSnapshot)
            {
                if(_logger.IsDebug) _logger.Debug($"Snapshot taken {commitPackage.Root} in {commitPackage.BlockNumber}");
                SnapshotTaken?.Invoke(this, new BlockNumberEventArgs(commitPackage.BlockNumber));
            }
        }

        private void Persist(TrieNode currentNode, long snapshotId)
        {
            if (currentNode == null)
            {
                throw new ArgumentNullException(nameof(currentNode));
            }

            if (currentNode.Keccak != null)
            {
                Debug.Assert(currentNode.LastSeen.HasValue, $"Cannot persist a dangling node (without {(nameof(TrieNode.LastSeen))} value set).");
                // Note that the LastSeen value here can be 'in the future' (greater than snapshotId
                // if we replaced a newly added node with an older copy and updated the LastSeen value.
                // Here we reach it from the old root so it appears to be out of place but it is correct as we need
                // to prevent it from being removed from cache and also want to have it persisted.

                if (_logger.IsTrace) _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode} in snapshot {snapshotId}.");
                _keyValueStore[currentNode.Keccak.Bytes] = currentNode.FullRlp;
                currentNode.IsPersisted = true;
                currentNode.LastSeen = snapshotId;

                PersistedNodesCount++;
            }
            else
            {
                Debug.Assert(currentNode.FullRlp != null && currentNode.FullRlp.Length < 32,
                    "We only expect persistence call without Keccak for the nodes that are kept inside the parent RLP (less than 32 bytes).");
            }
        }

        private static bool HasBeenRemoved(TrieNode trieNode, long snapshotId)
        {
            Debug.Assert(trieNode.LastSeen.HasValue, $"Any node that is cache should have {nameof(TrieNode.LastSeen)} set.");
            return trieNode.LastSeen < snapshotId;
        }

        #endregion
    }
}