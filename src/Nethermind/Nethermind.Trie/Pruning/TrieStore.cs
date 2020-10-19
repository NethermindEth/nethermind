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
        private int _isFirst;
        
        public TrieStore(IKeyValueStore? keyValueStore, ILogManager? logManager)
            : this(keyValueStore, No.Pruning, Full.Archive, logManager)
        {
        }

        public TrieStore(
            IKeyValueStore? keyValueStore,
            IPruningStrategy? pruningStrategy,
            IPersistenceStrategy? persistenceStrategy,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<TrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _pruningStrategy = pruningStrategy ?? throw new ArgumentNullException(nameof(pruningStrategy));
            _persistenceStrategy = persistenceStrategy ?? throw new ArgumentNullException(nameof(persistenceStrategy));
        }

        public long LastPersistedBlockNumber
        {
            get => _lastPersistedBlockNumber;
            private set
            {
                if (value != _lastPersistedBlockNumber)
                {
                    Metrics.LastPersistedBlockNumber = value;
                    _lastPersistedBlockNumber = value;
                    _lastPersistedReachedReorgBoundary = false;
                }
            }
        }

        public long MemoryUsedByCache
        {
            get => _memoryUsedByCache;
            private set
            {
                Metrics.MemoryUsedByCache = value;
                _memoryUsedByCache = value;
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
                Metrics.CachedNodesCount = _nodeCache.Count;
                return _nodeCache.Count;
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
                    throw new PruningException($"{nameof(TrieNode.LastSeen)} set on {node} committed at {blockNumber}.");
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
            EnsureCommitSetExistsForBlock(blockNumber);

            if (trieType == TrieType.State) // storage tries happen before state commits
            {
                if (_logger.IsTrace) _logger.Trace($"Enqueued blocks {_commitSetQueue.Count}");
                BlockCommitSet set = CurrentPackage;
                if (set != null)
                {
                    set.Root = root;
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"Current root (block {blockNumber}): {set.Root}, block {set.BlockNumber}");
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"Incrementing refs from block {blockNumber} root {set.Root?.ToString() ?? "NULL"} ");

                    set.Seal();
                }

                bool shouldPersistSnapshot = _persistenceStrategy.ShouldPersist(set.BlockNumber);
                if (shouldPersistSnapshot)
                {
                    Persist(set);
                }

                CurrentPackage = null;
            }
        }

        public void HackPersistOnShutdown()
        {
            PersistOnShutdown();
        }

        public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

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
                if (trieNode.Keccak == null)
                {
                    throw new InvalidOperationException($"Adding node with null hash {trieNode}");
                }

                MemoryUsedByCache += trieNode.GetMemorySize(false);
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
                foreach (KeyValuePair<Keccak, TrieNode> keyValuePair in _nodeCache)
                {
                    _logger.Trace($"  {keyValuePair.Value}");
                }
            }
        }

        public void Prune()
        {
            while (true)
            {
                if (!_pruningStrategy.ShouldPrune(MemoryUsedByCache))
                {
                    break;
                }

                if (_logger.IsWarn) _logger.Warn($"Pruning nodes {MemoryUsedByCache / 1.MB()}MB , last persisted block: {LastPersistedBlockNumber} current: {NewestKeptBlockNumber}.");

                Stopwatch stopwatch = Stopwatch.StartNew();
                List<TrieNode> toRemove = new List<TrieNode>(); // TODO: resettable

                long newMemory = 0;
                foreach ((Keccak key, TrieNode node) in _nodeCache)
                {
                    if (node.IsPersisted)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Removing persisted {node} from memory.");
                        toRemove.Add(node);
                        if (node.Keccak == null)
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
                        if (node.Keccak == null)
                        {
                            throw new InvalidOperationException($"Removed {node}");
                        }

                        Metrics.PrunedTransientNodesCount++;
                    }
                    else
                    {
                        newMemory += node.GetMemorySize(false);
                    }
                }

                foreach (TrieNode trieNode in toRemove)
                {
                    if (trieNode.Keccak == null)
                    {
                        throw new InvalidOperationException($"{trieNode} has a null key");
                    }

                    _nodeCache.Remove(trieNode.Keccak!);
                }

                MemoryUsedByCache = newMemory;
                Metrics.CachedNodesCount = _nodeCache.Count;

                stopwatch.Stop();
                Metrics.PruningTime = stopwatch.ElapsedMilliseconds;

                if (_logger.IsWarn) _logger.Warn($"Finished pruning nodes in {stopwatch.ElapsedMilliseconds}ms {MemoryUsedByCache / 1.MB()}MB, last persisted block: {LastPersistedBlockNumber} current: {NewestKeptBlockNumber}.");

                if (_pruningStrategy.ShouldPrune(MemoryUsedByCache))
                {
                    if (_logger.IsDebug) _logger.Debug("Elevated pruning starting");

                    BlockCommitSet? candidateSet = null;
                    while (_commitSetQueue.TryPeek(out BlockCommitSet? frontSet))
                    {
                        if (frontSet!.BlockNumber >= NewestKeptBlockNumber - Reorganization.MaxDepth)
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

                    if (candidateSet != null)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Elevated pruning for candidate {candidateSet.BlockNumber}");
                        Persist(candidateSet);
                        continue;
                    }

                    if (_logger.IsWarn) _logger.Warn($"Found no candidate for elevated pruning ({_commitSetQueue.Count})");
                }

                break;
            }
        }

        public void ClearCache()
        {
            _nodeCache.Clear();
            MemoryUsedByCache = 0;
        }
        
        public void Dispose()
        {
            _logger.Warn("Disposing trie");
            PersistOnShutdown();
        }

        public void RemoveHistorical()
        {
            while (_commitSetQueue.TryPeek(out BlockCommitSet blockCommitSet))
            {
                // safety
                if (_commitSetQueue.Count <= Reorganization.MaxDepth + 3)
                {
                    break;
                }
                
                if (blockCommitSet.BlockNumber < NewestKeptBlockNumber - Reorganization.MaxDepth)
                {
                    // if (_logger.IsWarn) _logger.Warn($"Removing historical ({_commitSetQueue.Count}) {blockCommitSet.BlockNumber} < {NewestKeptBlockNumber} - 512");    
                    _commitSetQueue.TryDequeue(out _);
                }
                else
                {
                    break;
                }
            }
        }

        #region Private

        private readonly IKeyValueStore _keyValueStore;

        private Dictionary<Keccak, TrieNode> _nodeCache = new Dictionary<Keccak, TrieNode>();

        private readonly IPruningStrategy _pruningStrategy;

        private readonly IPersistenceStrategy _persistenceStrategy;

        private readonly ILogger _logger;

        private ConcurrentQueue<BlockCommitSet> _commitSetQueue = new ConcurrentQueue<BlockCommitSet>();

        private long _memoryUsedByCache;

        private int _committedNodesCount;

        private int _persistedNodesCount;

        private long _lastPersistedBlockNumber;

        private BlockCommitSet? CurrentPackage { get; set; }

        private bool IsCurrentListSealed => CurrentPackage == null || CurrentPackage.IsSealed;

        private long NewestKeptBlockNumber { get; set; }

        private void CreateCommitSet(long blockNumber)
        {
            if (_logger.IsDebug) _logger.Debug($"Beginning new {nameof(BlockCommitSet)} - {blockNumber}");

            // TODO: this throws on reorgs, does it not? let us recreate it in test
            Debug.Assert(CurrentPackage == null || blockNumber == CurrentPackage.BlockNumber + 1,
                "Newly begun block is not a successor of the last one");
            Debug.Assert(IsCurrentListSealed, "Not sealed when beginning new block");

            BlockCommitSet commitSet = new BlockCommitSet(blockNumber);
            _commitSetQueue.Enqueue(commitSet);
            NewestKeptBlockNumber = Math.Max(blockNumber, NewestKeptBlockNumber);
            AnnounceReorgBoundaries();
            RemoveHistorical();
            Prune();

            CurrentPackage = commitSet;
            Debug.Assert(ReferenceEquals(CurrentPackage, commitSet),
                $"Current {nameof(BlockCommitSet)} is not same as the new package just after adding");
        }

        private void SaveInCache(TrieNode node)
        {
            Debug.Assert(node.Keccak != null, "Cannot store in cache nodes without resolved key.");
            _nodeCache[node.Keccak!] = node;
            MemoryUsedByCache += node.GetMemorySize(false);
            Metrics.CachedNodesCount = _nodeCache.Count;
        }

        private void Persist(BlockCommitSet commitSet)
        {
            if (_logger.IsDebug) _logger.Debug($"Persisting from root {commitSet.Root} in {commitSet.BlockNumber}");

            Stopwatch stopwatch = Stopwatch.StartNew();
            commitSet.Root?.PersistRecursively(tn => Persist(tn, commitSet.BlockNumber), this, _logger);
            stopwatch.Stop();
            Metrics.SnapshotPersistenceTime = stopwatch.ElapsedMilliseconds;

            if (_logger.IsWarn) _logger.Warn(
                $"Persisted trie from {commitSet.Root} at {commitSet.BlockNumber} in {stopwatch.ElapsedMilliseconds}ms (cache memory {MemoryUsedByCache})");

            LastPersistedBlockNumber = commitSet.BlockNumber;
        }

        private void Persist(TrieNode currentNode, long blockNumber)
        {
            if (currentNode == null)
            {
                throw new ArgumentNullException(nameof(currentNode));
            }

            if (currentNode.Keccak != null)
            {
                Debug.Assert(currentNode.LastSeen.HasValue, $"Cannot persist a dangling node (without {(nameof(TrieNode.LastSeen))} value set).");
                // Note that the LastSeen value here can be 'in the future' (greater than block number
                // if we replaced a newly added node with an older copy and updated the LastSeen value.
                // Here we reach it from the old root so it appears to be out of place but it is correct as we need
                // to prevent it from being removed from cache and also want to have it persisted.

                if (_logger.IsTrace) _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode} in snapshot {blockNumber}.");
                _keyValueStore[currentNode.Keccak.Bytes] = currentNode.FullRlp;
                currentNode.IsPersisted = true;
                currentNode.LastSeen = blockNumber;

                PersistedNodesCount++;
            }
            else
            {
                Debug.Assert(currentNode.FullRlp != null && currentNode.FullRlp.Length < 32,
                    "We only expect persistence call without Keccak for the nodes that are kept inside the parent RLP (less than 32 bytes).");
            }
        }

        private bool IsNoLongerNeeded(TrieNode node)
        {
            Debug.Assert(node.LastSeen.HasValue, $"Any node that is cache should have {nameof(TrieNode.LastSeen)} set.");
            return node.LastSeen < LastPersistedBlockNumber
                   && node.LastSeen < NewestKeptBlockNumber - Reorganization.MaxDepth;
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
            if (NewestKeptBlockNumber < 1)
            {
                return;
            }
            
            bool announceReorgBoundary = false;
            bool isFirstCommit = Interlocked.Exchange(ref _isFirst, 1) == 0;
            if (isFirstCommit)
            {
                _logger.Warn(
                    $"Reached first commit - newest {NewestKeptBlockNumber}, last persisted {LastPersistedBlockNumber}");
                // this is important when transitioning from fast sync
                // imagine that we transition at block 1200000
                // and then we close the app at 1200010
                // in such case we would try to continue at Head - 1200010
                // because head is loaded if there is no persistence checkpoint
                // so we need to force the persistence checkpoint
                long baseBlock = Math.Max(0, NewestKeptBlockNumber - 1);
                LastPersistedBlockNumber = baseBlock;
                announceReorgBoundary = true;
            }
            else if (!_lastPersistedReachedReorgBoundary)
            {
                // even after we persist a block we do not really remember it as a safe checkpoint
                // until max reorgs blocks after
                if (NewestKeptBlockNumber >= LastPersistedBlockNumber + Reorganization.MaxDepth)
                {
                    announceReorgBoundary = true;
                }
            }

            if (announceReorgBoundary)
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
            while (_commitSetQueue.TryDequeue(out BlockCommitSet? blockCommitSet))
            {
                if (blockCommitSet != null)
                {
                    if (blockCommitSet.BlockNumber <= NewestKeptBlockNumber - Reorganization.MaxDepth)
                    {
                        persistenceCandidate = blockCommitSet;
                        _logger.Warn($"New persistence candidate {persistenceCandidate}");
                    }
                }
                else
                {
                    _logger.Warn($"Block commit was null...");
                }
            }

            _logger.Warn($"Persisting on disposal {persistenceCandidate} (cache memory at {MemoryUsedByCache})");
            if (persistenceCandidate != null)
            {
                Persist(persistenceCandidate);
                AnnounceReorgBoundaries();
            }
        }

        #endregion
    }
}