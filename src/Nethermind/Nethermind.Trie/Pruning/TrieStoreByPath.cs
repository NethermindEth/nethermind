// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.ByPathState;
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
        private const byte PathMarker = 128;

        private int _isFirst;
        private ConcurrentDictionary<StateColumns, IBatch?> _currentBatches = null;
        private readonly ConcurrentDictionary<StateColumns, IPathTrieNodeCache> _committedNodes = null;
        private readonly IPersistenceStrategy _persistenceStrategy;

        private readonly ConcurrentQueue<byte[]> _destroyPrefixes;

        private bool _lastPersistedReachedReorgBoundary;
        private bool _useCommittedCache = false;

        private readonly TrieKeyValueStore _publicStore;

        public TrieStoreByPath(
        IKeyValueStoreWithBatching stateDb,
        IPersistenceStrategy? persistenceStrategy,
        ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<TrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _stateDb = stateDb as IColumnsDb<StateColumns> ?? throw new ArgumentNullException(nameof(stateDb));
            _persistenceStrategy = persistenceStrategy ?? throw new ArgumentNullException(nameof(persistenceStrategy));
            _useCommittedCache = !(_persistenceStrategy is Archive);
            _committedNodes = new ConcurrentDictionary<StateColumns, IPathTrieNodeCache>();
            _committedNodes.TryAdd(StateColumns.State, new TrieNodePathCache(this, logManager));
            _committedNodes.TryAdd(StateColumns.Storage, new TrieNodePathCache(this, logManager));
            _destroyPrefixes = new ConcurrentQueue<byte[]>();
            _pathStateDb = stateDb as IByPathStateDb;
            _currentBatches = new ConcurrentDictionary<StateColumns, IBatch?>();
            _currentBatches.TryAdd(StateColumns.State, null);
            _currentBatches.TryAdd(StateColumns.Storage, null);
            _publicStore = new TrieKeyValueStore(this);
        }

        public TrieStoreByPath(IKeyValueStoreWithBatching stateDb, ILogManager? logManager) : this(stateDb, Pruning.Persist.EveryBlock, logManager)
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
                Metrics.CachedNodesCount = _committedNodes.Count;
                return _committedNodes.Count;
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
                    _committedNodes[GetProperColumn(nodeCommitInfo.Node.FullPath.Length)].AddNode(blockNumber, node);
                }
                else
                {
                    Persist(nodeCommitInfo.Node, blockNumber, writeFlags);
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
                        Persist(0);
                        AnnounceReorgBoundaries();
                    }
                    else
                    {
                        if (_useCommittedCache && _persistenceStrategy.ShouldPersist(set.BlockNumber, out long persistTarget))
                        {
                            Persist(persistTarget);
                            AnnounceReorgBoundaries();
                        }
                    }
                }
                // set rootHash here to account for root hashes for the storage trees also in every block
                // there needs to be a better way to handle this
                StateColumns column = trieType switch
                {
                    TrieType.State => StateColumns.State,
                    TrieType.Storage => StateColumns.Storage,
                    _ => throw new NotImplementedException()
                };

                if (_useCommittedCache) _committedNodes[column].SetRootHashForBlock(blockNumber, root?.Keccak);

                if (trieType == TrieType.State) CurrentPackage = null;
            }
            finally
            {
                foreach (StateColumns column in Enum.GetValues(typeof(StateColumns)))
                {
                    _currentBatches[column]?.Dispose();
                    _currentBatches[column] = null;
                }
            }
        }

        public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        public byte[]? TryLoadRlp(Span<byte> path, IKeyValueStore? keyValueStore)
        {
            keyValueStore ??= GetProperColumnDb(path.Length);

            StateColumns column = GetProperColumn(path.Length);

            byte[] keyPath = Nibbles.NibblesToByteStorage(path);
            byte[]? rlp = _currentBatches[column]?[keyPath] ?? keyValueStore[keyPath];
            if (rlp is null || rlp[0] != PathMarker)
                return rlp;

            rlp = _currentBatches[column]?[rlp.AsSpan()[1..]] ?? keyValueStore[rlp.AsSpan()[1..]];
            return rlp;
        }

        internal byte[] LoadRlp(Keccak keccak, IKeyValueStore? keyValueStore, ReadFlags flags = ReadFlags.None)
        {
            return Array.Empty<byte>();
        }

        private byte[] LoadRlp(Span<byte> path, IKeyValueStore? keyValueStore, Keccak rootHash = null)
        {
            byte[]? rlp = TryLoadRlp(path, keyValueStore);

            if (rlp is null)
            {
                throw new TrieException($"Node {Nibbles.NibblesToByteStorage(path).ToHexString()} is missing from the DB | path.Length {path.Length} - {Nibbles.ToCompactHexEncoding(path.ToArray()).ToHexString()}");
            }

            Metrics.LoadedFromDbNodesCount++;

            return rlp;
        }

        public byte[] LoadRlp(Keccak keccak, ReadFlags flags = ReadFlags.None) => LoadRlp(keccak, null, flags);
        public byte[] LoadRlp(Span<byte> path, Keccak rootHash) => LoadRlp(path, null, rootHash);

        public bool IsPersisted(Keccak keccak)
        {
            throw new InvalidOperationException("IsPersisted using Keccak has is not supported by path based storage");
        }

        public bool IsPersisted(in ValueKeccak keccak)
        {
            throw new InvalidOperationException("IsPersisted using Keccak has is not supported by path based storage");
        }

        public bool IsPersisted(Keccak keccak, byte[] childPath)
        {
            byte[]? rlp = TryLoadRlp(childPath, GetProperColumnDb(childPath.Length));
            if (rlp is null) return false;

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

        public TrieNode FindCachedOrUnknown(Keccak keccak)
        {
            return new TrieNode(NodeType.Unknown, keccak);
        }

        public TrieNode? FindCachedOrUnknown(Keccak keccak, Span<byte> nodePath, Span<byte> storagePrefix)
        {
            StateColumns column = storagePrefix.Length == 0 ? StateColumns.State : StateColumns.Storage;
            TrieNode node = _committedNodes[column].GetNode(storagePrefix.ToArray().Concat(nodePath.ToArray()).ToArray(), keccak);
            if (node is null)
            {
                return new TrieNode(NodeType.Unknown, path: nodePath, keccak: keccak)
                {
                    StoreNibblePathPrefix = storagePrefix.ToArray()
                };
            }

            return node.FullRlp.IsNull ? null : node;
        }

        public TrieNode? FindCachedOrUnknown(Span<byte> nodePath, byte[] storagePrefix, Keccak? rootHash)
        {
            StateColumns column = storagePrefix.Length == 0 ? StateColumns.State : StateColumns.Storage;
            TrieNode node = storagePrefix.Length == 0
                ? _committedNodes[column].GetNodeFromRoot(rootHash, nodePath)
                : _committedNodes[column].GetNodeFromRoot(rootHash, Bytes.Concat(storagePrefix, nodePath));

            if (node is null)
                return new TrieNode(NodeType.Unknown, nodePath, storagePrefix);

            return node.FullRlp.IsNull ? null : node;
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
        private void Persist(long persistUntilBlock)
        {
            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                foreach (StateColumns column in Enum.GetValues(typeof(StateColumns)))
                {
                    _currentBatches[column] ??= _stateDb.GetColumnDb(column).StartBatch();
                    _committedNodes[column].PersistUntilBlock(persistUntilBlock, _currentBatches[column]);
                }
                
                stopwatch.Stop();
                Metrics.SnapshotPersistenceTime = stopwatch.ElapsedMilliseconds;


                while (_commitSetQueue.TryPeek(out BlockCommitSet frontSet) && frontSet.BlockNumber <= persistUntilBlock)
                {
                    LastPersistedBlockNumber = frontSet.BlockNumber;
                    _commitSetQueue.TryDequeue(out _);
                    if (_logger.IsTrace) _logger.Trace($"Persist target {persistUntilBlock} | persisting {frontSet.BlockNumber} / {frontSet.Root?.Keccak}");
                }
            }
            finally
            {
                // For safety we prefer to commit half of the batch rather than not commit at all.
                // Generally hanging nodes are not a problem in the DB but anything missing from the DB is.
                foreach (StateColumns column in Enum.GetValues(typeof(StateColumns)))
                {
                    _currentBatches[column].Dispose();
                    _currentBatches[column] = null;
                }
            }

            PruneCurrentSet();
        }

        private void Persist(TrieNode currentNode, long blockNumber, WriteFlags writeFlags = WriteFlags.None)
        {
            StateColumns column = GetProperColumn(currentNode);
            _currentBatches[column] ??= _stateDb.GetColumnDb(column).StartBatch();
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

            SaveNodeDirectly(blockNumber, currentNode, _currentBatches[column]);

            currentNode.IsPersisted = true;
            currentNode.LastSeen = Math.Max(blockNumber, currentNode.LastSeen ?? 0);

            PersistedNodesCount++;
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

        public void SaveNodeDirectly(long blockNumber, TrieNode trieNode, IKeyValueStore? keyValueStore = null, bool withDelete = false, WriteFlags writeFlags = WriteFlags.None)
        {
            keyValueStore ??= GetProperColumnDb(trieNode.FullPath.Length);

            byte[] fullPath = trieNode.FullPath ?? throw new ArgumentNullException();
            byte[] pathBytes = Nibbles.NibblesToByteStorage(fullPath);

            if (trieNode.IsLeaf)
            {
                Span<byte> pathToNodeNibbles = stackalloc byte[trieNode.StoreNibblePathPrefix.Length + trieNode.PathToNode!.Length];
                trieNode.StoreNibblePathPrefix.CopyTo(pathToNodeNibbles);
                Span<byte> pathToNodeSlice = pathToNodeNibbles.Slice(trieNode.StoreNibblePathPrefix.Length);
                trieNode.PathToNode.CopyTo(pathToNodeSlice);
                byte[] pathToNodeBytes = Nibbles.NibblesToByteStorage(pathToNodeNibbles);

                if (_logger.IsDebug) _logger.Trace($"Saving node {trieNode.NodeType} path to nibbles {pathToNodeNibbles.ToHexString()}, bytes: {pathToNodeBytes.ToHexString()}, full: {pathBytes.ToHexString()}");

                if (trieNode.FullRlp.IsNull)
                {
                    keyValueStore.Set(pathToNodeBytes, null, writeFlags);
                }
                else
                {
                    if (trieNode.PathToNode.Length < 64)
                    {
                        byte[] newPath = new byte[pathBytes.Length + 1];
                        Array.Copy(pathBytes, 0, newPath, 1, pathBytes.Length);
                        newPath[0] = PathMarker;
                        keyValueStore.Set(pathToNodeBytes, newPath, writeFlags);

                        if (withDelete)
                            RequestDeletionForLeaf(pathToNodeNibbles, trieNode.FullPath);
                    }
                }
            }
            else
            {
                if (_logger.IsDebug) _logger.Trace($"Saving node {trieNode.NodeType} full nibbles {fullPath.ToHexString()} full: {pathBytes.ToHexString()}");
            }
            if (withDelete)
            {
                if (trieNode.IsBranch)
                {
                    RequestDeletionForBranch(trieNode);
                }
                else if (trieNode.IsExtension)
                {
                    RequestDeletionForExtension(fullPath, trieNode.Key);
                }
            }

            keyValueStore.Set(pathBytes, trieNode.FullRlp.ToArray(), writeFlags);
        }

        public bool ExistsInDB(Keccak hash, byte[] pathNibbles)
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
            StateColumns column = GetProperColumn(key.Length);
            return _currentBatches[column]?.Get(key, flags) ?? _stateDb.GetColumnDb(column).Get(key, flags);
        }

        /// <summary>
        /// This method is here to support testing.
        /// </summary>
        public void ClearCache()
        {
            _committedNodes[StateColumns.State].Clear();
            _committedNodes[StateColumns.Storage].Clear();
        }

        public void DeleteByRange(Span<byte> startKey, Span<byte> endKey)
        {
            StateColumns column = GetProperColumn(startKey.Length);
            byte[] startDbKey = Nibbles.NibblesToByteStorage(startKey.ToArray());
            byte[] endDbKey = Nibbles.NibblesToByteStorage(endKey.ToArray());
            _stateDb.GetColumnDb(column).DeleteByRange(startDbKey, endDbKey);
        }

        public void MarkPrefixDeleted(long blockNumber, ReadOnlySpan<byte> keyPrefixNibbles)
        {
            if (_useCommittedCache)
            {
                _committedNodes[StateColumns.Storage].AddRemovedPrefix(blockNumber, keyPrefixNibbles);
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

            Span<byte> fromKey = new (GC.AllocateUninitializedArray<byte>(prefix.Length));
            prefix.CopyTo(fromKey);
            Span<byte> toKey = new(GC.AllocateUninitializedArray<byte>(prefix.Length));
            prefix.CopyTo(toKey);

            return (fromKey.ToArray(), toKey.IncrementNibble().ToArray());
        }

        public static (byte[], byte[]) GetDeleteKeyFromNibbles(Span<byte> nibbleFrom, Span<byte> nibbleTo, int oddityOverride)
        {
            byte[] fromKey = EncodePathWithEnforcedOddity2(nibbleFrom, oddityOverride);
            byte[] toKey = EncodePathWithEnforcedOddity2(nibbleTo, oddityOverride);

            return (fromKey, toKey);
        }

        public static byte[] EncodePathWithEnforcedOddity(Span<byte> pathNibbles, int maxLength, int oddityOverride, byte mask)
        {
            byte[] targetBytes = new byte[maxLength];
            Array.Fill(targetBytes, mask, 1, maxLength - 1);

            if (pathNibbles.Length == 0)
                return targetBytes;

            int oddity = pathNibbles.Length % 2;

            if (oddity == oddityOverride)
            {
                for (int i = 0; i < pathNibbles.Length / 2; i++)
                    targetBytes[i + 1] = Nibbles.ToByte(pathNibbles[2 * i + oddity], pathNibbles[2 * i + 1 + oddity]);

                if (oddity == 1)
                    targetBytes[0] = Nibbles.ToByte(1, pathNibbles[0]);
            }
            else if (oddity == 1 && oddityOverride == 0)
            {
                for (int i = 0; i < pathNibbles.Length / 2; i++)
                    targetBytes[i + 1] = Nibbles.ToByte(pathNibbles[2 * i], pathNibbles[2 * i + 1]);

                targetBytes[pathNibbles.Length / 2 + 1] = Nibbles.ToByte(pathNibbles[^1], (byte)(mask & 0x0f));
            }
            else if (oddity == 0 && oddityOverride == 1)
            {
                for (int i = 0; i < pathNibbles.Length / 2 - 1; i++)
                    targetBytes[i + 1] = Nibbles.ToByte(pathNibbles[2 * i + oddityOverride], pathNibbles[2 * i + 1 + oddityOverride]);

                targetBytes[0] = Nibbles.ToByte(1, pathNibbles[0]);
                targetBytes[pathNibbles.Length / 2] = Nibbles.ToByte(pathNibbles[^1], (byte)(mask & 0x0f));
            }
            return targetBytes;
        }

        public static byte[] EncodePathWithEnforcedOddity2(Span<byte> pathNibbles, int oddityOverride)
        {
            byte[] targetBytes = null;

            int oddity = pathNibbles.Length % 2;

            if (oddity == oddityOverride)
            {
                return Nibbles.NibblesToByteStorage(pathNibbles);
            }
            else if (oddity == 1 && oddityOverride == 0)
            {
                targetBytes = new byte[pathNibbles.Length / 2 + 2];
                for (int i = 0; i < pathNibbles.Length / 2; i++)
                    targetBytes[i + 1] = Nibbles.ToByte(pathNibbles[2 * i], pathNibbles[2 * i + 1]);

                targetBytes[pathNibbles.Length / 2 + 1] = Nibbles.ToByte(pathNibbles[^1], 0);
            }
            else if (oddity == 0 && oddityOverride == 1)
            {
                targetBytes = new byte[pathNibbles.Length / 2 + 1];

                for (int i = 0; i < pathNibbles.Length / 2 - 1; i++)
                    targetBytes[i + 1] = Nibbles.ToByte(pathNibbles[2 * i + oddityOverride], pathNibbles[2 * i + 1 + oddityOverride]);

                targetBytes[0] = Nibbles.ToByte(1, pathNibbles.Length > 0 ? pathNibbles[0] : (byte)0);
                targetBytes[pathNibbles.Length / 2] = Nibbles.ToByte(pathNibbles[^1], 0);
            }
            return targetBytes;
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
                (from, to) = GetDeleteKeyFromNibbles(fromNibblesKey, fullPathNibbles, 0);
                _pathStateDb?.EnqueueDeleteRange(column, from, to);

                (from, to) = GetDeleteKeyFromNibbles(fromNibblesKey, fullPathNibbles, 1);
                _pathStateDb?.EnqueueDeleteRange(column, from, to);
            }

            if (keySlice.IndexOfAnyExcept((byte)0xf) >= 0)
            {
                Span<byte> fullPathIncremented = stackalloc byte[fullPathNibbles.Length];
                fullPathNibbles.CopyTo(fullPathIncremented);
                Span<byte> endNibbles = fromNibblesKey.Slice(0, pathToNodeNibbles.Length).IncrementNibble(true);
                (from, to) = GetDeleteKeyFromNibbles(fullPathIncremented.IncrementNibble(), endNibbles, 0);
                _pathStateDb?.EnqueueDeleteRange(column, from, to);

                (from, to) = GetDeleteKeyFromNibbles(fullPathIncremented, endNibbles, 1);
                _pathStateDb?.EnqueueDeleteRange(column, from, to);
            }
        }

        public void RequestDeletionForBranch(TrieNode branchNode)
        {
            void GenerateRangesAndRequest(Span<byte> childPathFrom, Span<byte> childPathTo, byte? from, byte? to, byte toMask)
            {
                int fullKeyLength = childPathFrom.Length >= 66 ? 66 : 33;
                byte[][] keyRanges = new byte[4][];

                if (childPathFrom.Length > 0)
                {
                    childPathFrom[^1] = from.Value;
                    keyRanges[0] = EncodePathWithEnforcedOddity2(childPathFrom, 0);
                    keyRanges[2] = EncodePathWithEnforcedOddity2(childPathFrom, 1);
                }
                else
                {
                    keyRanges[0] = EncodePathWithEnforcedOddity(childPathFrom, fullKeyLength, 0, 0x00);
                    keyRanges[2] = EncodePathWithEnforcedOddity(childPathFrom, fullKeyLength, 1, 0x00);
                }

                if (childPathTo.Length > 0)
                {
                    if (to is not null)
                        childPathTo[^1] = to.Value;
                    keyRanges[1] = EncodePathWithEnforcedOddity2(childPathTo, 0);
                    keyRanges[3] = EncodePathWithEnforcedOddity2(childPathTo, 1);
                }
                else
                {
                    keyRanges[1] = EncodePathWithEnforcedOddity(childPathTo, fullKeyLength, 0, 0xff);
                    keyRanges[3] = EncodePathWithEnforcedOddity(childPathTo, fullKeyLength, 1, 0xff);
                }

                for (int i = 0; i < 4; i+=2)
                {
                    _pathStateDb?.EnqueueDeleteRange(fullKeyLength == 66 ? StateColumns.Storage : StateColumns.State, keyRanges[i], keyRanges[i + 1]);
                }
            }

            Span<byte> childPathFrom = stackalloc byte[branchNode.FullPath.Length + 1];
            Span<byte> childPathTo = stackalloc byte[branchNode.FullPath.Length + 1];
            branchNode.FullPath.CopyTo(childPathFrom);
            branchNode.FullPath.CopyTo(childPathTo);
            byte? ind1 = null;
            for (byte i = 0; i < 16; i++)
            {
                if (branchNode.IsChildNull(i))
                {
                    ind1 ??= i;
                    continue;
                }
                else
                {
                    if (ind1 is not null)
                    {
                        GenerateRangesAndRequest(childPathFrom, childPathTo, ind1, i, 0x00);
                        ind1 = null;
                    }
                }
            }

            if (ind1 is not null)
            {
                Span<byte> nextSiblingPath = childPathTo.Slice(0, branchNode.FullPath.Length).IncrementNibble(true);
                GenerateRangesAndRequest(childPathFrom, nextSiblingPath, ind1, null, 0x00);
                ind1 = null;
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
                (from, to) = GetDeleteKeyFromNibbles(fromNibblesKey, fullPathAndKey, 0);
                _pathStateDb?.EnqueueDeleteRange(column, from, to);

                (from, to) = GetDeleteKeyFromNibbles(fromNibblesKey, fullPathAndKey, 1);
                _pathStateDb?.EnqueueDeleteRange(column, from, to);
            }

            if (key.IndexOfAnyExcept((byte)0xf) >= 0)
            {
                //start path
                fullPathAndKey.IncrementNibble();

                Span<byte> endNibbles = fromNibblesKey[..fullPathNibbles.Length].IncrementNibble(true);

                (from, to) = GetDeleteKeyFromNibbles(fullPathAndKey, endNibbles, 0);
                _pathStateDb?.EnqueueDeleteRange(column, from, to);

                (from, to) = GetDeleteKeyFromNibbles(fullPathAndKey, endNibbles, 1);
                _pathStateDb?.EnqueueDeleteRange(column, from, to);
            }
        }

        public bool CanAccessByPath()
        {
            return _pathStateDb is null ? true :
                _pathStateDb.CanAccessByPath(Db.StateColumns.State) && _pathStateDb.CanAccessByPath(Db.StateColumns.Storage);
        }

        private IKeyValueStoreWithBatching GetProperColumnDb(int pathLength)
        {
            return pathLength >= 66 ?
                _stateDb.GetColumnDb(StateColumns.Storage) :
                _stateDb.GetColumnDb(StateColumns.State);
        }

        private StateColumns GetProperColumn(int pathLength)
        {
            return pathLength >= 66 ? StateColumns.Storage : StateColumns.State;
        }

        private StateColumns GetProperColumn(TrieNode trieNode)
        {
            return trieNode.StoreNibblePathPrefix?.Length == 0 ? StateColumns.State : StateColumns.Storage;
        }

        public IKeyValueStore AsKeyValueStore() => _publicStore;

        private class TrieKeyValueStore : IKeyValueStore
        {
            private readonly TrieStoreByPath _trieStore;

            public TrieKeyValueStore(TrieStoreByPath trieStore)
            {
                _trieStore = trieStore;
            }

            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => _trieStore.Get(key, flags);

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                => _trieStore._stateDb.Set(key, value, flags);
        }
    }
}
