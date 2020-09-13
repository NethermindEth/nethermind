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
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
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

            MemorySize =
                MemorySizes.SmallObjectOverhead +
                5 * MemorySizes.RefSize -
                MemorySizes.SmallObjectFreeDataSize +
                40 /* linked list */;
        }

        public void Commit(TrieType trieType, long blockNumber, NodeCommitInfo nodeCommitInfo)
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
                TrieNode trieNode = nodeCommitInfo.Node;
                _commitCount++;

                if (trieNode!.Keccak == null)
                {
                    throw new InvalidOperationException(
                        $"Hash of the node {trieNode} should be known at the time of committing.");
                }

                Debug.Assert(CurrentPackage != null, "Current package is null when enqueing a trie node.");

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
                            nodeCommitInfo.NodeParent!.ReplaceChildRef(nodeCommitInfo.ChildPositionAtParent, cachedReplacement);
                        }
                    }
                }
                else
                {
                    long previousPackageMemory = CurrentPackage.MemorySize;
                    if(_logger.IsTrace) _logger.Trace($"Enqueuing {trieNode}");
                    CurrentPackage.Enqueue(trieNode);
                    _trieNodeCache.Set(trieNode.Keccak, trieNode);
                    AddToMemory(CurrentPackage.MemorySize - previousPackageMemory);
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

        public void Unwind()
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

        public event EventHandler<BlockNumberEventArgs>? Stored;

        public void Flush()
        {
            if (_logger.IsDebug)
                _logger.Debug($"Flushing trie cache - in memory {_trieNodeCache.Count} " +
                              $"| commit count {_commitCount} " +
                              $"| drop count {DroppedNodesCount} " +
                              $"| carry count {_carriedCount} " +
                              $"| save count {PersistedNodesCount}");

            CurrentPackage?.Seal();
            while (TryPruningOldBlock())
            {
            }

            if (_logger.IsDebug)
                _logger.Debug($"Flushed trie cache - in memory {_trieNodeCache.Count} " +
                              $"| commit count {_commitCount} " +
                              $"| drop count {DroppedNodesCount} " +
                              $"| carry count {_carriedCount} " +
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

        public long MemorySize { get; private set; }

        #region Private

        private const int LinkedListNodeMemorySize = 48;

        private readonly ITrieNodeCache _trieNodeCache;

        private readonly IKeyValueStore _keyValueStore;
        
        private readonly IPruningStrategy _pruningStrategy;
        
        private readonly IPersistenceStrategy _snapshotStrategy;

        private readonly ILogger _logger;

        private int _commitCount;

        private int _carriedCount;

        private LinkedList<BlockCommitPackage> _blockCommitsQueue = new LinkedList<BlockCommitPackage>();

        private Queue<TrieNode> _carryQueue = new Queue<TrieNode>();

        private BlockCommitPackage? CurrentPackage => _blockCommitsQueue.Last?.Value;

        private bool IsCurrentPackageSealed => CurrentPackage == null || CurrentPackage.IsSealed;

        public int PersistedNodesCount { get; private set; }

        public int DroppedNodesCount { get; private set; }

        private void BeginNewPackage(long blockNumber)
        {
            CurrentPackage?.LogContent(_logger);

            if (_logger.IsDebug)
                _logger.Debug($"Beginning new {nameof(BlockCommitPackage)} - {blockNumber} | memory {MemorySize}");

            Debug.Assert(CurrentPackage == null || CurrentPackage.BlockNumber == blockNumber - 1,
                "Newly begun block is not a successor of the last one");

            Debug.Assert(IsCurrentPackageSealed, "Not sealed when beginning new block");

            BlockCommitPackage newPackage = new BlockCommitPackage(blockNumber);
            _blockCommitsQueue.AddLast(newPackage);
            NewestKeptBlockNumber = Math.Max(blockNumber, NewestKeptBlockNumber);

            while(_pruningStrategy.ShouldPrune(OldestKeptBlockNumber, NewestKeptBlockNumber, MemorySize))
            {
                TryPruningOldBlock();
            }

            long newMemory = newPackage.MemorySize + LinkedListNodeMemorySize;
            AddToMemory(newMemory);

            Debug.Assert(CurrentPackage == newPackage,
                "Current package is not equal the new package just after adding");
        }

        private long OldestKeptBlockNumber => _blockCommitsQueue.First!.Value.BlockNumber;

        private long NewestKeptBlockNumber { get; set; }

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

                
                //package.Root?.IncrementRefsRecursively(_logger, _trieNodeCache);

                package.Seal();
                _trieNodeCache.Dump();
            }
        }

        private void AddToMemory(long newMemory)
        {
            MemorySize += newMemory;
        }

        internal bool TryPruningOldBlock()
        {
            BlockCommitPackage blockCommit = _blockCommitsQueue.First?.Value;
            bool canPrune = blockCommit?.IsSealed ?? false;
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
                _logger.Debug(
                    $"Start pruning {nameof(BlockCommitPackage)} - {commitPackage.BlockNumber} | memory {MemorySize}");

            Debug.Assert(commitPackage != null && commitPackage.IsSealed,
                $"Invalid {nameof(commitPackage)} - {commitPackage} received for pruning.");

            long memoryToDrop = commitPackage.MemorySize + LinkedListNodeMemorySize;

            // Queue<TrieNode> localCarryQueue = _carryQueue;
            // _carryQueue = new Queue<TrieNode>();
            
            bool shouldPersistSnapshot = _snapshotStrategy.ShouldPersistSnapshot(commitPackage.BlockNumber);

            // really I should just save from root...
            //while (localCarryQueue.TryDequeue(out TrieNode currentNode) ||
            //       commitPackage.TryDequeue(out currentNode))
            //{
            //    Debug.Assert(
            //        currentNode.Keccak != null,
            //        $"Committed node {currentNode} has a NULL key");
            //    Debug.Assert(
            //        currentNode.FullRlp != null,
            //        $"Committed node {currentNode} has a NULL {nameof(currentNode.FullRlp)}");

            //    if (currentNode.Refs == 0)
            //    {
            //        if (_logger.IsTrace)
            //            _logger.Trace($"Dropping (refs 0) {nameof(TrieNode)} {currentNode}.");

            //        DropNode(currentNode);
            //    }
            //    else
            //    {
            //        if (shouldPersistSnapshot)
            //        {
            //            if (!currentNode.IsPersisted)
            //            {
            //                if (_logger.IsTrace)
            //                    _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode}.");
            //                Persist(currentNode);
            //                _trieNodeCache.Remove(currentNode.Keccak);
            //                currentNode.IsPersisted = true;
            //            }
            //            else
            //            {
            //                if (_logger.IsTrace)
            //                    _logger.Trace($"Ignoring persisted {nameof(TrieNode)} {currentNode}.");
            //            }
            //        }
            //        else
            //        {
            //            // there was an incorrect drop of ref <= 1 here but it was wrong
            //            // imagine referenced by block 12 but not by block 11 and

            //            if (!currentNode.IsPersisted)
            //            {
            //                if (_logger.IsTrace)
            //                    _logger.Trace($"Carrying (refs > 1) and not P {nameof(TrieNode)} {currentNode}.");

            //                _carryQueue.Enqueue(currentNode);
            //                _carriedCount++;
            //            }
            //            else
            //            {
            //                if (_logger.IsTrace)
            //                    _logger.Trace($"Ignoring persisted {nameof(TrieNode)} {currentNode}.");
            //            }
            //        }
            //    }
            //}

            //DecrementRefs(commitPackage);
            if (shouldPersistSnapshot)
            {
                commitPackage.Root?.PersistRecursively(_logger, _trieNodeCache, Persist);
                _trieNodeCache.Prune();
            }
            
            // _trieNodeCache.Prune();

            MemorySize -= memoryToDrop;
            if (_logger.IsDebug)
                _logger.Debug(
                    $"End pruning {nameof(BlockCommitPackage)} - {commitPackage.BlockNumber} | memory {MemorySize}");

            _trieNodeCache.Dump();
            if (shouldPersistSnapshot)
            {
                Stored?.Invoke(this, new BlockNumberEventArgs(commitPackage.BlockNumber));
            }
        }

        private void Persist(TrieNode currentNode)
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
            
            if (_logger.IsTrace)
                _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode}.");
            _keyValueStore[currentNode.Keccak.Bytes] = currentNode.FullRlp;
            currentNode.IsPersisted = true;
            PersistedNodesCount++;
        }

        // private void DropNode(TrieNode trieNode)
        // {
        //     if (!trieNode.IsPersisted)
        //     {
        //         DroppedNodesCount++;
        //         if (_logger.IsTrace)
        //             _logger.Trace($"Pruning in store: {nameof(TrieNode)} {trieNode}. ({DroppedNodesCount / ((decimal) DroppedNodesCount + PersistedNodesCount):P2})");
        //     }
        //
        //     _trieNodeCache.Remove(trieNode.Keccak!);
        // }

        //private void DecrementRefs(BlockCommitPackage package)
        //{
        //    if (!package.IsSealed)
        //    {
        //        throw new InvalidOperationException(
        //            $"Current {nameof(BlockCommitPackage)} has to be sealed before the refs can be decremented.");
        //    }

        //    TrieNode? root = package.Root;
        //    if (_logger.IsTrace)
        //        _logger.Trace(
        //            $"Decrementing refs from block {package.BlockNumber} root {root?.ToString() ?? "NULL"}");

        //    if (root != null)
        //    {
        //        if (root.Refs == 0)
        //        {
        //            throw new InvalidDataException($"Found root node {root} with 0 refs.");
        //        }

        //        root.DecrementRefsRecursively(_logger, _trieNodeCache);

        //        //_trieNodeCache.Prune();
        //        //_trieNodeCache.Dump();
        //    }
        //}

        #endregion
    }
}
