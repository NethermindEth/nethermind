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
            : this(keyValueStore, No.Pruning, Full.Archive, logManager) { }

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
                Metrics.CachedNodesCount = _nodeCache.Count;
                return _nodeCache.Count;
            }
        }

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo)
        {
            if (blockNumber < 0) throw new ArgumentOutOfRangeException(nameof(blockNumber));
            EnsureCommitListExistsForBlock(blockNumber);

            if (_logger.IsTrace) _logger.Trace($"Committing {nodeCommitInfo} at {blockNumber}");
            if (!nodeCommitInfo.IsEmptyBlockMarker)
            {
                TrieNode node = nodeCommitInfo.Node!;
                if (node!.Keccak == null)
                {
                    throw new PruningException($"The hash of {node} should be known at the time of committing.");
                }
                
                if (CurrentPackage == null)
                {
                    throw new PruningException($"{nameof(CurrentPackage)} is NULL when committing {node} at {blockNumber}.");
                }
                
                if (node!.LastSeen.HasValue)
                {
                    throw new PruningException($"{nameof(TrieNode.LastSeen)} not set on {node} committed at {blockNumber}.");
                }

                if (IsNodeCached(node.Keccak))
                {
                    TrieNode cachedNodeCopy = FindCachedOrUnknown(node.Keccak);
                    if (!ReferenceEquals(cachedNodeCopy, node))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Replacing {node} with its cached copy {cachedNodeCopy}.");
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
                    SaveInCache(node);
                }

                node.LastSeen = blockNumber;
                CommittedNodesCount++;
            }
        }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root)
        {
            if (blockNumber < 0) throw new ArgumentOutOfRangeException(nameof(blockNumber));
            EnsureCommitListExistsForBlock(blockNumber);
            
            if (trieType == TrieType.State) // storage tries happen before state commits
            {
                if (_logger.IsTrace) _logger.Trace($"Enqueued packages {_blockCommitsQueue.Count}");
                BlockCommitList list = CurrentPackage;
                if (list != null)
                {
                    list.Root = root;
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"Current root (block {blockNumber}): {list.Root}, block {list.BlockNumber}");
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"Incrementing refs from block {blockNumber} root {list.Root?.ToString() ?? "NULL"} ");

                    list.Seal();

                    TryRemovingOldBlock();
                }
                
                CurrentPackage = null;
            }
        }

        public event EventHandler<BlockNumberEventArgs>? SnapshotTaken;

        public void Flush()
        {
            if (_logger.IsDebug)
                _logger.Debug($"Flushing trie cache - in memory {_nodeCache.Count} " +
                              $"| commit count {CommittedNodesCount} " +
                              $"| save count {PersistedNodesCount}");

            CurrentPackage?.Seal();
            while (TryRemovingOldBlock())
            {
            }

            if (_logger.IsDebug)
                _logger.Debug($"Flushed trie cache - in memory {_nodeCache.Count} " +
                              $"| commit count {CommittedNodesCount} " +
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

        public bool IsNodeCached(Keccak hash) => _nodeCache.ContainsKey(hash);

        public TrieNode FindCachedOrUnknown(Keccak hash)
        {
            bool isMissing = !_nodeCache.TryGetValue(hash, out TrieNode trieNode);
            if (isMissing)
            {
                trieNode = new TrieNode(NodeType.Unknown, hash);
                if (_logger.IsTrace) _logger.Trace($"Creating new node {trieNode}");
                _nodeCache.TryAdd(trieNode.Keccak!, trieNode);
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
                _logger.Trace($"Trie node cache ({_nodeCache.Count})");
                // return;
                foreach (KeyValuePair<Keccak, TrieNode> keyValuePair in _nodeCache)
                {
                    _logger.Trace($"  {keyValuePair.Value}");
                }
            }
        }

        public void Prune(long snapshotId)
        {
            List<Keccak> toRemove = new List<Keccak>(); // TODO: resettable
            foreach ((Keccak key, TrieNode value) in _nodeCache)
            {
                if (value.IsPersisted)
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing persisted {value} from memory.");
                    toRemove.Add(key);
                    Metrics.PrunedPersistedNodesCount++;
                }
                else if (HasBeenRemoved(value, snapshotId))
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing {value} from memory (no longer referenced).");
                    toRemove.Add(key);
                    Metrics.PrunedTransientNodesCount++;
                }
            }

            foreach (Keccak keccak in toRemove)
            {
                _nodeCache.Remove(keccak);
            }

            Metrics.CachedNodesCount = _nodeCache.Count;
        }

        public void ClearCache()
        {
            _nodeCache.Clear();
        }

        #region Private

        private readonly IKeyValueStore _keyValueStore;

        private Dictionary<Keccak, TrieNode> _nodeCache = new Dictionary<Keccak, TrieNode>();

        private readonly IPruningStrategy _pruningStrategy;

        private readonly IPersistenceStrategy _snapshotStrategy;

        private readonly ILogger _logger;

        private LinkedList<BlockCommitList> _blockCommitsQueue = new LinkedList<BlockCommitList>();

        private int _committedNodesCount;

        private int _persistedNodesCount;

        private BlockCommitList? CurrentPackage { get; set; }

        private bool IsCurrentPackageSealed => CurrentPackage == null || CurrentPackage.IsSealed;

        private long OldestKeptBlockNumber => _blockCommitsQueue.First!.Value.BlockNumber;

        private long NewestKeptBlockNumber { get; set; }

        private void CreateCommitList(long blockNumber)
        {
            if (_logger.IsDebug)
                _logger.Debug($"Beginning new {nameof(BlockCommitList)} - {blockNumber}");

            Debug.Assert(CurrentPackage == null || blockNumber == CurrentPackage.BlockNumber + 1,
                "Newly begun block is not a successor of the last one");

            Debug.Assert(IsCurrentPackageSealed, "Not sealed when beginning new block");

            BlockCommitList newList = new BlockCommitList(blockNumber);
            _blockCommitsQueue.AddLast(newList);
            NewestKeptBlockNumber = Math.Max(blockNumber, NewestKeptBlockNumber);

            // TODO: memory should be taken from the cache now
            while (_pruningStrategy.ShouldPrune(OldestKeptBlockNumber, NewestKeptBlockNumber, 0))
            {
                TryRemovingOldBlock();
            }

            CurrentPackage = newList;
            Debug.Assert(CurrentPackage == newList,
                "Current package is not equal the new package just after adding");
        }

        internal bool TryRemovingOldBlock()
        {
            BlockCommitList? blockCommit = _blockCommitsQueue.First?.Value;
            bool hasAnySealedBlockInQueue = blockCommit != null && blockCommit.IsSealed;
            if (hasAnySealedBlockInQueue)
            {
                RemoveOldBlock(blockCommit);
            }

            return hasAnySealedBlockInQueue;
        }

        private void SaveInCache(TrieNode node)
        {
            Debug.Assert(node.Keccak != null, "Cannot store in cache nodes without resolved key.");
            _nodeCache[node.Keccak!] = node;
            Metrics.CachedNodesCount = _nodeCache.Count;
        }

        private void RemoveOldBlock(BlockCommitList commitList)
        {
            _blockCommitsQueue.RemoveFirst();

            if (_logger.IsDebug)
                _logger.Debug($"Start pruning {nameof(BlockCommitList)} - {commitList.BlockNumber}");

            Debug.Assert(commitList != null && commitList.IsSealed,
                $"Invalid {nameof(commitList)} - {commitList} received for pruning.");

            bool shouldPersistSnapshot = _snapshotStrategy.ShouldPersistSnapshot(commitList.BlockNumber);
            if (shouldPersistSnapshot)
            {
                if (_logger.IsDebug) _logger.Debug($"Persisting from root {commitList.Root} in {commitList.BlockNumber}");

                Stopwatch stopwatch = Stopwatch.StartNew();
                commitList.Root?.PersistRecursively(tn => Persist(tn, commitList.BlockNumber), this, _logger);
                stopwatch.Stop();
                Metrics.SnapshotPersistenceTime = stopwatch.ElapsedMilliseconds;

                stopwatch.Restart();
                Prune(commitList.BlockNumber); // for now can only prune on snapshot?
                stopwatch.Stop();
                Metrics.PruningTime = stopwatch.ElapsedMilliseconds;
            }

            if (_logger.IsDebug)
                _logger.Debug($"End pruning {nameof(BlockCommitList)} - {commitList.BlockNumber}");

            Dump();
            if (shouldPersistSnapshot)
            {
                if (_logger.IsDebug) _logger.Debug($"Snapshot taken {commitList.Root} in {commitList.BlockNumber}");
                SnapshotTaken?.Invoke(this, new BlockNumberEventArgs(commitList.BlockNumber));
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

        private static bool HasBeenRemoved(TrieNode node, long snapshotId)
        {
            Debug.Assert(node.LastSeen.HasValue, $"Any node that is cache should have {nameof(TrieNode.LastSeen)} set.");
            return node.LastSeen < snapshotId;
        }
        
        private void EnsureCommitListExistsForBlock(long blockNumber)
        {
            if (CurrentPackage is null)
            {
                CreateCommitList(blockNumber);
            }
        }

        #endregion
    }
}