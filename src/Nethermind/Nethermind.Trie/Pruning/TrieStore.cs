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
        public TrieStore(
            ITrieNodeCache? trieNodeCache,
            IKeyValueStore? keyValueStore,
            IPruningStrategy? pruningStrategy,
            IPersistenceStrategy? snapshotStrategy,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<TrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _trieNodeCache = trieNodeCache ?? throw new ArgumentNullException(nameof(trieNodeCache));
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _pruningStrategy = pruningStrategy ?? throw new ArgumentNullException(nameof(pruningStrategy));
            _snapshotStrategy = snapshotStrategy ?? throw new ArgumentNullException(nameof(snapshotStrategy));
        }
        
        public int CommittedNodeCount { get; private set; }
        
        public int PersistedNodesCount { get; private set; }

        public void CommitOneNode(long blockNumber, NodeCommitInfo nodeCommitInfo)
        {
            if (blockNumber < 0)
                throw new ArgumentOutOfRangeException(nameof(blockNumber));

            bool shouldBeginNewPackage = CurrentPackage == null || CurrentPackage.BlockNumber != blockNumber;
            if (shouldBeginNewPackage)
            {
                BeginNewPackage(blockNumber);
            }

            if (_logger.IsTrace)
                _logger.Trace($"Committing {blockNumber} {nodeCommitInfo}");

            if (!nodeCommitInfo.IsEmptyBlockMarker)
            {
                Debug.Assert(CurrentPackage != null, "Current package is null when enqueing a trie node.");
                Debug.Assert(!nodeCommitInfo.Node.LastSeen.HasValue, "Committing a block.");
                
                TrieNode trieNode = nodeCommitInfo.Node;
                if (trieNode!.Keccak == null)
                {
                    throw new InvalidOperationException(
                        $"Hash of the node {trieNode} should be known at the time of committing.");
                }

                CommittedNodeCount++;

                if (_trieNodeCache.IsInMemory(trieNode.Keccak))
                {
                    TrieNode cachedReplacement = _trieNodeCache.GetOrCreateUnknown(trieNode.Keccak);
                    if (!ReferenceEquals(cachedReplacement, trieNode))
                    {
                        if (_logger.IsTrace)
                            _logger.Trace(
                                $"Replacing a {nameof(trieNode)} object {trieNode} with its cached representation {cachedReplacement}.");
                        if (!nodeCommitInfo.IsRoot)
                        {
                            cachedReplacement.LastSeen = blockNumber; // TODO: this line is not tested yet and if missing it may lead to lost storage
                            nodeCommitInfo.NodeParent!.ReplaceChildRef(nodeCommitInfo.ChildPositionAtParent, cachedReplacement);
                        }
                    }
                }
                else
                {
                    trieNode!.LastSeen = blockNumber;
                    _trieNodeCache.Set(trieNode.Keccak, trieNode);
                }
            }
        }

        public bool IsInMemory(Keccak keccak)
        {
            return _trieNodeCache.IsInMemory(keccak);
        }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root)
        {
            bool shouldBeginNewPackage = CurrentPackage == null || CurrentPackage.BlockNumber != blockNumber;
            if (shouldBeginNewPackage)
            {
                BeginNewPackage(blockNumber);
            }

            if (trieType == TrieType.Storage)
            {
                // do nothing, we will extract roots from accounts
            }
            else
            {
                FinishBlockCommitOnState(blockNumber, root);
            }
        }

        public void UndoOneBlock()
        {
            if (!_blockCommitsQueue.Any())
            {
                throw new InvalidOperationException(
                    $"Trying to unwind a {nameof(BlockCommitPackage)} when the queue is empty.");
            }

            if (_logger.IsDebug)
                _logger.Debug($"Unwinding {CurrentPackage}");

            //DecrementRefs(CurrentPackage!);
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
            while (TryPruningOldBlock())
            {
            }

            if (_logger.IsDebug)
                _logger.Debug($"Flushed trie cache - in memory {_trieNodeCache.Count} " +
                              $"| commit count {CommittedNodeCount} " +
                              $"| save count {PersistedNodesCount}");
        }

        public TrieNode? FindCachedOrNull(Keccak hash)
        {
            return _trieNodeCache.GetOrNull(hash);
        }

        public TrieNode? FindCachedOrUnknown(Keccak hash)
        {
            return _trieNodeCache.GetOrCreateUnknown(hash);
        }

        public byte[]? LoadRlp(Keccak keccak, bool allowCaching)
        {
            if (!allowCaching)
            {
                return _keyValueStore[keccak.Bytes];
            }

            // TODO: to not modify too many other parts of the solution I leave the cache as a static field of
            // the PatriciaTree class for now
            byte[] cachedRlp = PatriciaTree.NodeCache.Get(keccak);
            if (cachedRlp == null)
            {
                byte[] dbValue = _keyValueStore[keccak.Bytes];
                if (dbValue == null)
                {
                    throw new TrieException($"Node {keccak} is missing from the DB");
                }

                PatriciaTree.NodeCache.Set(keccak, dbValue);
                return dbValue;
            }

            return cachedRlp;
        }

        #region Private

        private readonly ITrieNodeCache _trieNodeCache;

        private readonly IKeyValueStore _keyValueStore;

        private readonly IPruningStrategy _pruningStrategy;

        private readonly IPersistenceStrategy _snapshotStrategy;

        private readonly ILogger _logger;

        private LinkedList<BlockCommitPackage> _blockCommitsQueue = new LinkedList<BlockCommitPackage>();

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
                TryPruningOldBlock();
            }

            Debug.Assert(CurrentPackage == newPackage,
                "Current package is not equal the new package just after adding");
        }
        
        private void FinishBlockCommitOnState(long blockNumber, TrieNode? root)
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
            }
        }

        internal bool TryPruningOldBlock()
        {
            BlockCommitPackage? blockCommit = _blockCommitsQueue.First?.Value;
            bool canPrune = blockCommit != null && blockCommit.IsSealed; 
            if (canPrune)
            {
                Prune(blockCommit);
                _blockCommitsQueue.RemoveFirst();
            }

            return canPrune;
        }

        private void Prune(BlockCommitPackage commitPackage)
        {
            if (_logger.IsDebug)
                _logger.Debug($"Start pruning {nameof(BlockCommitPackage)} - {commitPackage.BlockNumber}");

            Debug.Assert(commitPackage != null && commitPackage.IsSealed,
                $"Invalid {nameof(commitPackage)} - {commitPackage} received for pruning.");
            
            bool shouldPersistSnapshot = _snapshotStrategy.ShouldPersistSnapshot(commitPackage.BlockNumber);
            if (shouldPersistSnapshot)
            {
                commitPackage.Root?.PersistRecursively(tn => Persist(tn, commitPackage.BlockNumber), this, _logger);
                
                // the pruning responsibility can be divided by this and the cache now
                _trieNodeCache.Prune(commitPackage.BlockNumber);
            }
            
            if (_logger.IsDebug)
                _logger.Debug($"End pruning {nameof(BlockCommitPackage)} - {commitPackage.BlockNumber}");

            _trieNodeCache.Dump();
            if (shouldPersistSnapshot)
            {
                SnapshotTaken?.Invoke(this, new BlockNumberEventArgs(commitPackage.BlockNumber));
            }
        }

        private void Persist(TrieNode currentNode, long snapshotId)
        {
            if (currentNode == null)
            {
                throw new ArgumentNullException(nameof(currentNode));
            }

            if (currentNode.Keccak == null)
            {
                throw new InvalidOperationException(
                    $"An attempt to {nameof(Persist)} a node without a resolved {nameof(TrieNode.Keccak)}");
            }
            
            Debug.Assert(currentNode.LastSeen.HasValue, $"Cannot persist a dangling node (without {(nameof(TrieNode.LastSeen))} value set).");
            // Debug.Assert(currentNode.LastSeen <= snapshotId, $"Cannot persist nodes from the future ({(nameof(TrieNode.LastSeen))} {currentNode.LastSeen} > {nameof(snapshotId)} {snapshotId}).");

            if (_logger.IsTrace) _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode}.");
            
            _keyValueStore[currentNode.Keccak.Bytes] = currentNode.FullRlp;
            currentNode.IsPersisted = true;
            currentNode.LastSeen = snapshotId;

            PersistedNodesCount++;
        }

        #endregion
    }
}