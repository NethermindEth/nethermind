// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.ByPathState;
using Nethermind.Logging;
using Nethermind.Trie.ByPath;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Trie store helps to manage trie commits block by block.
    /// If persistence and pruning are needed they have a chance to execute their behaviour on commits.
    /// </summary>
    public class TrieStoreByPath : ITrieStore
    {
        private const byte PathMarker = 128;

        private int _isFirst;
        private IColumnsWriteBatch<StateColumns>? _columnsBatch = null;
        private readonly ConcurrentDictionary<StateColumns, IWriteBatch?> _currentWriteBatches = null;
        private readonly IPathDataCache _committedNodes = null;
        private readonly IByPathPersistenceStrategy _persistenceStrategy;

        private bool _lastPersistedReachedReorgBoundary;
        private readonly bool _useCommittedCache = false;

        private readonly TrieKeyValueStore _publicStore;

        public TrieStoreByPath(
            IColumnsDb<StateColumns> stateDb,
            IByPathPersistenceStrategy? persistenceStrategy,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<TrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _persistenceStrategy = persistenceStrategy ?? throw new ArgumentNullException(nameof(persistenceStrategy));
            _useCommittedCache = _persistenceStrategy is not ByPathArchive;
            _committedNodes = new PathDataCache(this, logManager);
            _pathStateDb = stateDb as IByPathStateDb;
            _currentWriteBatches = new ConcurrentDictionary<StateColumns, IWriteBatch?>();
            _publicStore = new TrieKeyValueStore(this);
        }

        public TrieStoreByPath(IColumnsDb<StateColumns> stateDb, ILogManager? logManager) : this(stateDb, ByPathPersist.EveryBlock, logManager)
        {
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
                //Metrics.CachedNodesCount = _committedNodes.Count;
                //return _committedNodes.Count;
                return 0;
            }
        }

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
        {
            if (blockNumber < 0) throw new ArgumentOutOfRangeException(nameof(blockNumber));
            EnsureCommitSetExistsForBlock(blockNumber);

            if (_logger.IsTrace) _logger.Trace($"Committing {nodeCommitInfo} at {blockNumber}");
            if (!nodeCommitInfo.IsEmptyBlockMarker && !nodeCommitInfo.Node!.IsBoundaryProofNode)
            {
                TrieNode node = nodeCommitInfo.Node!;

                if (CurrentPackage is null)
                {
                    throw new TrieStoreException($"{nameof(CurrentPackage)} is NULL when committing {node} at {blockNumber}.");
                }

                if (node!.LastSeen.HasValue)
                {
                    throw new TrieStoreException($"{nameof(TrieNode.LastSeen)} set on {node} committed at {blockNumber}.");
                }

                if (_useCommittedCache)
                {
                    _committedNodes.AddNodeData(blockNumber, node);
                }
                else
                {
                    PersistNode(nodeCommitInfo.Node, blockNumber, writeFlags);
                }
                node.LastSeen = Math.Max(blockNumber, node.LastSeen ?? 0);

                CommittedNodesCount++;
            }
        }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
        {
            if (blockNumber < 0) throw new ArgumentOutOfRangeException(nameof(blockNumber));
            EnsureCommitSetExistsForBlock(blockNumber);

            try
            {
                if (trieType == TrieType.State) // storage tries happen before state commits
                {
                    if (_useCommittedCache)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Setting root hash {root?.Keccak} for block {blockNumber}");
                        _committedNodes.CloseContext(blockNumber, root?.Keccak ?? Keccak.EmptyTreeHash);
                    }

                    if (_logger.IsTrace) _logger.Trace($"Enqueued blocks {_commitSetQueue.Count}");
                    BlockCommitSet set = CurrentPackage;
                    if (set is not null)
                    {
                        set.Root = root;
                        if (_logger.IsTrace) _logger.Trace($"Current root (block {blockNumber}): {set.Root}, block {set.BlockNumber}");
                        set.Seal();
                    }
                    if (blockNumber == 0) // special case for genesis
                    {
                        Persist(0, null);
                        AnnounceReorgBoundaries();
                    }
                    else
                    {
                        if (_useCommittedCache)
                        {
                            (long blockNumber, Hash256 stateRoot)? persist = _persistenceStrategy.GetBlockToPersist(set.BlockNumber, set.Root?.Keccak ?? Keccak.EmptyTreeHash);
                            if (persist is not null)
                            {
                                Persist(persist.Value.blockNumber, persist.Value.stateRoot);
                                AnnounceReorgBoundaries();
                            }
                            else
                            {
                                //we are catching up with head and not receiving FCU? Try to persist to avoid indefinite memory growth
                                int minCached = 128;
                                int maxNoPersisted = 3 * minCached;
                                if (_commitSetQueue.Count > maxNoPersisted)
                                {
                                    BlockCommitSet commitSet = null;
                                    while (_commitSetQueue.Count > minCached)
                                        _commitSetQueue.TryDequeue(out commitSet);

                                    Persist(commitSet.BlockNumber, commitSet.Root?.Keccak);
                                    AnnounceReorgBoundaries();
                                }
                            }
                        }
                    }

                    CurrentPackage = null;
                }

                DisposeWriteBatches();
            }
            finally
            {
            }
        }

        public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        public byte[]? TryLoadRlp(Span<byte> path, IKeyValueStore? keyValueStore)
        {
            StateColumns column = GetProperColumn(path.Length);
            keyValueStore ??= _stateDb.GetColumnDb(column);

            byte[] keyPath = Nibbles.NibblesToByteStorage(path);
            byte[]? rlp = keyValueStore.Get(keyPath);
            if (rlp is null || rlp[0] != PathMarker)
                return rlp;

            return keyValueStore.Get(rlp.AsSpan()[1..]);
        }

        internal byte[] LoadRlp(Hash256 keccak, IKeyValueStore? keyValueStore, ReadFlags flags = ReadFlags.None)
        {
            return Array.Empty<byte>();
        }
        public virtual byte[]? TryLoadRlp(Hash256 keccak, ReadFlags readFlags = ReadFlags.None) => LoadRlp(keccak, null, readFlags);

        private byte[] LoadRlp(Span<byte> path, IKeyValueStore? keyValueStore, Hash256 rootHash = null)
        {
            byte[]? rlp = TryLoadRlp(path, keyValueStore);

            if (rlp is null)
            {
                throw new TrieException($"Node {Nibbles.NibblesToByteStorage(path).ToHexString()} is missing from the DB | path.Length {path.Length} - {path.ToHexString()}");
            }

            Metrics.LoadedFromDbNodesCount++;

            return rlp;
        }

        public byte[] LoadRlp(Hash256 keccak, ReadFlags flags = ReadFlags.None) => LoadRlp(keccak, null, flags);
        public byte[] LoadRlp(Span<byte> path, Hash256 rootHash) => LoadRlp(path, null, rootHash);

        public bool IsPersisted(Hash256 keccak)
        {
            throw new InvalidOperationException("IsPersisted using Hash256 has is not supported by path based storage");
        }

        public bool IsPersisted(in ValueHash256 keccak)
        {
            throw new InvalidOperationException("IsPersisted using Hash256 has is not supported by path based storage");
        }

        public bool IsPersisted(Hash256 keccak, byte[] childPath)
        {
            byte[]? rlp = TryLoadRlp(childPath, null);
            if (rlp is null)
                return keccak == Keccak.EmptyTreeHash;

            TrieNode node = new TrieNode(NodeType.Unknown, rlp: rlp);
            node.ResolveNode(this);
            node.ResolveKey(this, false);
            if (node.Keccak == keccak) return true;
            Metrics.LoadedFromDbNodesCount++;

            return false;
        }

        public IReadOnlyTrieStore AsReadOnly(IKeyValueStore? keyValueStore)
        {
            return new ReadOnlyTrieStoreByPath(this, keyValueStore);
        }

        public TrieNode FindCachedOrUnknown(Hash256 keccak)
        {
            return new TrieNode(NodeType.Unknown, keccak);
        }

        public TrieNode? FindCachedOrUnknown(Hash256 keccak, Span<byte> nodePath, Span<byte> storagePrefix)
        {
            if (!_useCommittedCache)
            {
                return new(NodeType.Unknown, path: nodePath, keccak: keccak)
                {
                    StoreNibblePathPrefix = storagePrefix.ToArray()
                };
            }
            Span<byte> targetPath = Bytes.Concat(storagePrefix, nodePath);
            NodeData? nodeData = _committedNodes.GetNodeData(targetPath, keccak);
            if (nodeData is null)
            {
                TrieNode unknownNode = new(NodeType.Unknown, path: nodePath, keccak: keccak)
                {
                    StoreNibblePathPrefix = storagePrefix.ToArray()
                };
                return unknownNode;
            }

            if (nodeData.RLP is null)
                return null;

            TrieNode node = new(NodeType.Unknown, nodePath.ToArray(), nodeData.Keccak, nodeData.RLP);
            node.StoreNibblePathPrefix = storagePrefix.ToArray();
            node.ResolveNode(this);
            node.ResolveKey(this, nodePath.Length == 0);
            return node;
        }

        public TrieNode? FindCachedOrUnknown(Span<byte> nodePath, byte[] storagePrefix, Hash256? rootHash)
        {
            if (!_useCommittedCache)
                return new TrieNode(NodeType.Unknown, nodePath, storagePrefix);

            Span<byte> targetPath = Bytes.Concat(storagePrefix, nodePath);
            NodeData? nodeData = _committedNodes.GetNodeDataAtRoot(rootHash, targetPath);

            if (nodeData is null)
                return new TrieNode(NodeType.Unknown, nodePath, storagePrefix);

            if (nodeData.RLP is null)
                return null;

            TrieNode node = new(NodeType.Unknown, nodePath.ToArray(), nodeData.Keccak, nodeData.RLP);
            node.StoreNibblePathPrefix = storagePrefix.ToArray();
            node.ResolveNode(this);
            node.ResolveKey(this, nodePath.Length == 0);
            return node;
        }

        public void Dispose()
        {
            if (_logger.IsDebug) _logger.Debug("Disposing trie");
            PersistOnShutdown();
        }

        #region Private

        protected readonly IColumnsDb<StateColumns> _stateDb;
        private readonly IByPathStateDb _pathStateDb;

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

        public bool UseCommittedCache => _useCommittedCache;

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
        /// Persists all transient (not yet persisted) blocks un till <paramref name="persistUntilBlock"/> block number.
        /// After this action we are sure that the full state is available for the block <paramref name="persistUntilBlock"/>.
        /// </summary>
        /// <param name="persistUntilBlock">Persist all the block changes until this block number</param>
        private void Persist(long targetBlockNumber, Hash256? targetBlockHash)
        {
            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                if (targetBlockHash is null && targetBlockNumber > 0)
                    throw new ArgumentException("Need to pass target state root to persist other than genesis");

                BlockCommitSet frontSet = null;
                while (_commitSetQueue.TryPeek(out BlockCommitSet peekSet) && peekSet.BlockNumber <= targetBlockNumber)
                {
                    _commitSetQueue.TryDequeue(out frontSet);
                }

                if (_logger.IsTrace) _logger.Trace($"Persist target {targetBlockNumber} | persisting {targetBlockNumber} / {targetBlockHash}");

                PrepareWriteBatches();
                _committedNodes.PersistUntilBlock(targetBlockNumber, targetBlockHash ?? frontSet.Root?.Keccak, _columnsBatch);

                LastPersistedBlockNumber = targetBlockNumber;

                stopwatch.Stop();
                Metrics.SnapshotPersistenceTime = stopwatch.ElapsedMilliseconds;

                DisposeWriteBatches();
            }
            finally
            {
            }

            PruneCurrentSet();
        }

        private void PersistNode(TrieNode currentNode, long blockNumber, WriteFlags writeFlags = WriteFlags.None)
        {
            StateColumns column = GetProperColumn(currentNode.FullPath.Length);
            PrepareWriteBatches();

            if (currentNode is null)
            {
                throw new ArgumentNullException(nameof(currentNode));
            }
            Debug.Assert(blockNumber == TrieNode.LastSeenNotSet || currentNode.LastSeen != TrieNode.LastSeenNotSet, $"Cannot persist a dangling node (without {(nameof(TrieNode.LastSeen))} value set).");
            // Note that the LastSeen value here can be 'in the future' (greater than block number
            // if we replaced a newly added node with an older copy and updated the LastSeen value.
            // Here we reach it from the old root so it appears to be out of place but it is correct as we need
            // to prevent it from being removed from cache and also want to have it persisted.

            if (_logger.IsTrace) _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode} in snapshot {blockNumber}.");

            PersistNode(currentNode, _currentWriteBatches[column]);

            currentNode.IsPersisted = true;
            currentNode.LastSeen = Math.Max(blockNumber, currentNode.LastSeen ?? 0);

            PersistedNodesCount++;
        }

        public void PersistNode(TrieNode trieNode, IWriteBatch? batch = null, bool withDelete = false, WriteFlags writeFlags = WriteFlags.None)
        {
            StateColumns column = GetProperColumn(trieNode.FullPath.Length);

            bool shouldDispose = batch is null;
            IColumnsWriteBatch<StateColumns> masterBatch = null;
            if (batch is null)
            {
                masterBatch = _stateDb.StartWriteBatch();
                batch = masterBatch.GetColumnBatch(column);
            }

            byte[] fullPath = trieNode.FullPath ?? throw new ArgumentNullException();
            byte[] pathBytes = Nibbles.NibblesToByteStorage(fullPath);

            if (trieNode.IsLeaf)
            {
                Span<byte> pathToNodeNibbles = stackalloc byte[trieNode.StoreNibblePathPrefix.Length + trieNode.PathToNode!.Length];
                trieNode.StoreNibblePathPrefix.CopyTo(pathToNodeNibbles);
                Span<byte> pathToNodeSlice = pathToNodeNibbles.Slice(trieNode.StoreNibblePathPrefix.Length);
                trieNode.PathToNode.CopyTo(pathToNodeSlice);
                byte[] pathToNodeBytes = Nibbles.NibblesToByteStorage(pathToNodeNibbles);

                if (_logger.IsTrace) _logger.Trace($"Saving node {trieNode.NodeType} path to nibbles {pathToNodeNibbles.ToHexString()}, bytes: {pathToNodeBytes.ToHexString()}, full: {pathBytes.ToHexString()}");

                if (trieNode.FullRlp.IsNull)
                {
                    batch.Set(pathToNodeBytes, null, writeFlags);
                }
                else
                {
                    if (trieNode.PathToNode.Length < 64)
                    {
                        byte[] newPath = new byte[pathBytes.Length + 1];
                        Array.Copy(pathBytes, 0, newPath, 1, pathBytes.Length);
                        newPath[0] = PathMarker;

                        batch.Set(pathToNodeBytes, newPath, writeFlags);

                        if (withDelete)
                            RequestDeletionForLeaf(pathToNodeNibbles, trieNode.FullPath);
                    }
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Saving node {trieNode.NodeType} full nibbles {fullPath.ToHexString()} full: {pathBytes.ToHexString()}");
            }
            if (withDelete)
            {
                if (trieNode.IsBranch)
                    RequestDeletionForBranch(trieNode);
                else if (trieNode.IsExtension)
                    RequestDeletionForExtension(fullPath, trieNode.Key);
            }

            batch.Set(pathBytes, trieNode.FullRlp.ToArray(), writeFlags);

            if (shouldDispose)
                masterBatch.Dispose();
        }

        public void PersistNodeData(Span<byte> fullPath, int pathToNodeLength, byte[]? rlpData, IWriteBatch? batch = null, WriteFlags writeFlags = WriteFlags.None)
        {
            StateColumns column = GetProperColumn(fullPath.Length);

            bool shouldDispose = batch is null;
            IColumnsWriteBatch<StateColumns> masterBatch = null;
            if (batch is null)
            {
                masterBatch = _stateDb.StartWriteBatch();
                batch = masterBatch.GetColumnBatch(column);
            }

            int pathToNodeIndexWithPrefix = fullPath.Length >= 66 ? pathToNodeLength + 66 : pathToNodeLength;

            byte[] pathBytes = Nibbles.NibblesToByteStorage(fullPath);
            Span<byte> pathToNodeNibbles = pathToNodeLength >= 0 ? fullPath[..pathToNodeIndexWithPrefix] : Array.Empty<byte>();

            if (_logger.IsTrace)
                _logger.Trace($"Persisting node path to nibbles {pathToNodeNibbles.ToHexString()}, full path bytes: {pathBytes.ToHexString()}, rlp: {rlpData?.ToHexString()}");

            if (pathToNodeLength >= 0)
            {
                byte[] pathToNodeBytes = Nibbles.NibblesToByteStorage(pathToNodeNibbles);

                if (rlpData is null)
                {
                    batch.Set(pathToNodeBytes, null, writeFlags);
                }
                else
                {
                    if (pathToNodeLength < 64)
                    {
                        Span<byte> newPath = stackalloc byte[pathBytes.Length + 1];
                        newPath[0] = PathMarker;
                        pathBytes.CopyTo(newPath.Slice(1));

                        batch.Set(pathToNodeBytes, newPath.ToArray(), writeFlags);
                    }
                }
            }

            batch.Set(pathBytes, rlpData, writeFlags);

            if (shouldDispose)
                masterBatch.Dispose();
        }

        private void DequeueOldCommitSets()
        {
            while (_commitSetQueue.TryPeek(out BlockCommitSet blockCommitSet))
            {
                if (blockCommitSet.BlockNumber < LastPersistedBlockNumber)
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
            // TODO: this can be used to persist the cache if we want
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

        #endregion

        public bool ExistsInDB(Hash256 hash, byte[] pathNibbles)
        {
            byte[]? rlp = TryLoadRlp(pathNibbles.AsSpan(), GetProperColumnDb(pathNibbles.Length));
            if (rlp is not null)
            {
                TrieNode node = new(NodeType.Unknown, rlp);
                node.ResolveNode(this);
                node.ResolveKey(this, false);
                return node.Keccak == hash;
            }
            return false;
        }
        public byte[]? this[ReadOnlySpan<byte> key]
        {
            get => Get(key);
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return _stateDb.GetColumnDb(GetProperColumn(key.Length)).Get(key, flags);
        }

        /// <summary>
        /// This method is here to support testing.
        /// </summary>
        public void ClearCache()
        {
            //_committedNodes[StateColumns.State].Clear();
            //_committedNodes[StateColumns.Storage].Clear();
        }

        public void DeleteByRange(Span<byte> startKey, Span<byte> endKey, IWriteBatch writeBatch = null)
        {
            StateColumns column = GetProperColumn(startKey.Length);
            byte[] startDbKey = Nibbles.NibblesToByteStorage(startKey.ToArray());
            byte[] endDbKey = Nibbles.NibblesToByteStorage(endKey.ToArray());

            if (writeBatch is not null)
                writeBatch.DeleteByRange(startDbKey, endDbKey);
            else
                _stateDb.GetColumnDb(column).DeleteByRange(startDbKey, endDbKey);
        }

        public void MarkPrefixDeleted(long blockNumber, ReadOnlySpan<byte> keyPrefixNibbles)
        {
            if (_useCommittedCache)
            {
                _committedNodes.AddRemovedPrefix(blockNumber, keyPrefixNibbles);
            }
            else
            {
                (byte[] startKey, byte[] endKey) = GetDeleteKeyFromNibblePrefix(keyPrefixNibbles);
                byte[] startDbKey = Nibbles.NibblesToByteStorage(startKey.ToArray());
                byte[] endDbKey = Nibbles.NibblesToByteStorage(endKey.ToArray());
                _stateDb.GetColumnDb(StateColumns.Storage).DeleteByRange(startDbKey, endDbKey);
            }
        }

        public static (byte[], byte[]) GetDeleteKeyFromNibblePrefix(ReadOnlySpan<byte> prefix)
        {
            if (prefix.Length % 2 != 0)
                throw new ArgumentException("Only even length prefixes supported");

            Span<byte> fromKey = new(GC.AllocateUninitializedArray<byte>(prefix.Length));
            prefix.CopyTo(fromKey);
            Span<byte> toKey = new(GC.AllocateUninitializedArray<byte>(prefix.Length));
            prefix.CopyTo(toKey);

            return (fromKey.ToArray(), toKey.IncrementNibble().ToArray());
        }

        public static (byte[], byte[]) GetDeleteKeyFromNibbles(Span<byte> nibbleFrom, Span<byte> nibbleTo)
        {
            byte[] fromKey = Nibbles.NibblesToByteStorage(nibbleFrom);
            byte[] toKey = Nibbles.NibblesToByteStorage(nibbleTo);

            return (fromKey, toKey);
        }

        public void RequestDeletionForLeaf(Span<byte> pathToNodeNibbles, Span<byte> fullPathNibbles)
        {
            byte[] from, to;
            StateColumns column = fullPathNibbles.Length >= 66 ? StateColumns.Storage : StateColumns.State;

            if (pathToNodeNibbles.Length == 0)
                return;

            //edge cases
            Span<byte> keySlice = fullPathNibbles.Slice(pathToNodeNibbles.Length);

            Span<byte> fromNibblesKey = stackalloc byte[pathToNodeNibbles.Length + 1];
            pathToNodeNibbles.CopyTo(fromNibblesKey);

            if (!keySlice.IsZero())
            {
                (from, to) = GetDeleteKeyFromNibbles(fromNibblesKey, fullPathNibbles);
                _pathStateDb?.EnqueueDeleteRange(column, from, to);
                if (_logger.IsTrace) _logger.Trace($"Leaf deletion for {pathToNodeNibbles.ToHexString()} | {fullPathNibbles.ToHexString()} | {from.ToHexString()} - {to.ToHexString()}");
            }

            if (keySlice.IndexOfAnyExcept((byte)0xf) >= 0)
            {
                Span<byte> fullPathIncremented = stackalloc byte[fullPathNibbles.Length];
                fullPathNibbles.CopyTo(fullPathIncremented);
                Span<byte> endNibbles = fromNibblesKey.Slice(0, pathToNodeNibbles.Length).IncrementNibble(true);
                (from, to) = GetDeleteKeyFromNibbles(fullPathIncremented.IncrementNibble(), endNibbles);
                if (_logger.IsTrace) _logger.Trace($"Leaf deletion for {pathToNodeNibbles.ToHexString()} | {fullPathNibbles.ToHexString()} | {from.ToHexString()} - {to.ToHexString()}");
                _pathStateDb?.EnqueueDeleteRange(column, from, to);
            }
        }

        public void RequestDeletionForBranch(TrieNode branchNode)
        {
            void GenerateRangesAndRequest(Span<byte> childPathFrom, Span<byte> childPathTo, byte? from, byte? to)
            {
                byte[] fromKey, toKey;

                childPathFrom[^1] = from.Value;
                fromKey = Nibbles.NibblesToByteStorage(childPathFrom);

                if (to is not null)
                    childPathTo[^1] = to.Value;
                toKey = Nibbles.NibblesToByteStorage(childPathTo);

                if (_logger.IsTrace) _logger.Trace($"Branch deletion for {branchNode.FullPath.ToHexString()} | {fromKey.ToHexString()} - {toKey.ToHexString()}");
                _pathStateDb?.EnqueueDeleteRange(GetProperColumn(childPathFrom.Length), fromKey, toKey);
            }

            Span<byte> childPathFrom = stackalloc byte[branchNode.FullPath.Length + 1];
            Span<byte> childPathTo = stackalloc byte[branchNode.FullPath.Length + 1];
            branchNode.FullPath.CopyTo(childPathFrom);
            branchNode.FullPath.CopyTo(childPathTo);
            byte? rangeStart = null;
            for (byte i = 0; i < 16; i++)
            {
                if (branchNode.IsChildNull(i))
                {
                    rangeStart ??= i;
                    continue;
                }
                else
                {
                    if (rangeStart is not null)
                    {
                        GenerateRangesAndRequest(childPathFrom, childPathTo, rangeStart, i);
                        rangeStart = null;
                    }
                }
            }

            if (rangeStart is not null)
            {
                //special case for root
                if (branchNode.FullPath.Length == 0)
                {
                    int maxLen = branchNode.StoreNibblePathPrefix.Length == 0 ? 33 : 66;
                    Span<byte> maxPath = stackalloc byte[maxLen];
                    maxPath.Fill(0xff);
                    GenerateRangesAndRequest(childPathFrom, maxPath, rangeStart, null);
                }
                else
                {
                    Span<byte> nextSiblingPath = childPathTo.Slice(0, branchNode.FullPath.Length).IncrementNibble(true);
                    GenerateRangesAndRequest(childPathFrom, nextSiblingPath, rangeStart, null);
                }
            }
        }

        public void RequestDeletionForExtension(Span<byte> fullPathNibbles, Span<byte> key)
        {
            byte[] from, to;
            StateColumns column = fullPathNibbles.Length >= 66 ? StateColumns.Storage : StateColumns.State;

            if (fullPathNibbles.Length == 0)
                return;

            Span<byte> fromNibblesKey = stackalloc byte[fullPathNibbles.Length + 1];
            fullPathNibbles.CopyTo(fromNibblesKey);

            Span<byte> fullPathAndKey = stackalloc byte[fullPathNibbles.Length + key.Length];
            fullPathNibbles.CopyTo(fullPathAndKey);
            key.CopyTo(fullPathAndKey[fullPathNibbles.Length..]);

            if (!key.IsZero())
            {
                (from, to) = GetDeleteKeyFromNibbles(fromNibblesKey, fullPathAndKey);
                if (_logger.IsTrace) _logger.Trace($"Extension deletion for {fullPathAndKey.ToHexString()} | {from.ToHexString()} - {to.ToHexString()}");
                _pathStateDb?.EnqueueDeleteRange(column, from, to);
            }

            if (key.IndexOfAnyExcept((byte)0xf) >= 0)
            {
                //start path
                fullPathAndKey.IncrementNibble();

                Span<byte> endNibbles = fromNibblesKey[..fullPathNibbles.Length].IncrementNibble(true);

                (from, to) = GetDeleteKeyFromNibbles(fullPathAndKey, endNibbles);
                if (_logger.IsTrace) _logger.Trace($"Extension deletion for {fullPathAndKey.ToHexString()} | {from.ToHexString()} - {to.ToHexString()}");
                _pathStateDb?.EnqueueDeleteRange(column, from, to);
            }
        }

        public bool CanAccessByPath()
        {
            return _pathStateDb is null ? true :
                _pathStateDb.CanAccessByPath(Db.StateColumns.State) && _pathStateDb.CanAccessByPath(Db.StateColumns.Storage);
        }

        public bool ShouldResetObjectsOnRootChange()
        {
            return !UseCommittedCache;
        }

        private IKeyValueStoreWithBatching GetProperColumnDb(int pathLength)
        {
            return pathLength >= 66 ? _stateDb.GetColumnDb(StateColumns.Storage) : _stateDb.GetColumnDb(StateColumns.State);
        }

        private StateColumns GetProperColumn(int pathLength)
        {
            return pathLength >= 66 ? StateColumns.Storage : StateColumns.State;
        }

        private void PrepareWriteBatches()
        {
            if (_columnsBatch is null)
            {
                _columnsBatch = _stateDb.StartWriteBatch();
                foreach (StateColumns column in Enum.GetValues(typeof(StateColumns)))
                    _currentWriteBatches[column] = _columnsBatch.GetColumnBatch(column);
            }
        }

        private void DisposeWriteBatches()
        {
            if (_columnsBatch is not null)
            {
                _columnsBatch.Dispose();
                foreach (StateColumns column in Enum.GetValues(typeof(StateColumns)))
                    _currentWriteBatches[column] = null;
                _columnsBatch = null;
            }
        }

        public void OpenContext(long blockNumber, Hash256 keccak)
        {
            _committedNodes.OpenContext(blockNumber, keccak);
        }

        public IReadOnlyKeyValueStore TrieNodeRlpStore => _publicStore;

        public void Set(in ValueHash256 hash, byte[] rlp) { }

        private class TrieKeyValueStore : IReadOnlyKeyValueStore
        {
            private readonly TrieStoreByPath _trieStore;

            public TrieKeyValueStore(TrieStoreByPath trieStore)
            {
                _trieStore = trieStore;
            }

            //public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => _trieStore.GetByHash(key, flags);
            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => Array.Empty<byte>();
        }
    }
}
