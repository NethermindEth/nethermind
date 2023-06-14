// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.ByPath;
using Newtonsoft.Json.Linq;

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
        private static int _deletionCount = 0;
        private static long _deletionsTotalus = 0;
        private BlockingCollection<byte[]> _emptyPrefixes;

        private IBatch? _currentBatch = null;

        private readonly IPathTrieNodeCache _committedNodes;
        private readonly ConcurrentQueue<byte[]> _destroyPrefixes;

        private bool _lastPersistedReachedReorgBoundary;
        private readonly Task _pruningTask = Task.CompletedTask;
        private readonly CancellationTokenSource _pruningTaskCancellationTokenSource = new();

        private ByPathStateDbPrunner _dbPrunner;

        public TrieStoreByPath(IKeyValueStoreWithBatching? keyValueStore, ILogManager? logManager)
            : this(keyValueStore, No.Pruning, Pruning.Persist.EveryBlock, logManager)
        {
        }

        public TrieStoreByPath(IKeyValueStoreWithBatching? keyValueStore, int historyBlockDepth, ILogManager? logManager)
                : this(keyValueStore, No.Pruning, Pruning.Persist.EveryBlock, logManager, historyBlockDepth)
        {
        }

        public TrieStoreByPath(
            IKeyValueStoreWithBatching? keyValueStore,
            IPruningStrategy? pruningStrategy,
            IPersistenceStrategy? persistenceStrategy,
            ILogManager? logManager,
            int historyBlockDepth = 128,
            ByPathStateDbPrunner dbPrunner = null)
        {
            _logger = logManager?.GetClassLogger<TrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _persistenceStrategy = persistenceStrategy ?? throw new ArgumentNullException(nameof(persistenceStrategy));
            _committedNodes = new TrieNodeBlockCache(this, historyBlockDepth, logManager);
            _destroyPrefixes = new ConcurrentQueue<byte[]>();
            _emptyPrefixes = new BlockingCollection<byte[]>();
            _dbPrunner = dbPrunner ?? new ByPathStateDbPrunner(_keyValueStore);
            //_pruningTask = BuildCleaningTask();
            //_pruningTask.Start();
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

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo)
        {
            if (blockNumber < 0) throw new ArgumentOutOfRangeException(nameof(blockNumber));
            EnsureCommitSetExistsForBlock(blockNumber);

            if (_logger.IsTrace) _logger.Trace($"Committing {nodeCommitInfo} at {blockNumber}");
            if (!nodeCommitInfo.IsEmptyBlockMarker && !nodeCommitInfo.Node!.IsBoundaryProofNode)
            //if (!nodeCommitInfo.IsEmptyBlockMarker)
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

                _committedNodes.AddNode(blockNumber, node);
                node.LastSeen = Math.Max(blockNumber, node.LastSeen ?? 0);

                if (_committedNodes.MaxNumberOfBlocks == 0)
                    Persist(node, blockNumber);

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
                    if (blockNumber == 0) // special case for genesis
                    {
                        Persist(0);
                        AnnounceReorgBoundaries();
                    }
                    else
                    {
                        long persistTarget = Math.Max(LastPersistedBlockNumber, set.BlockNumber - _committedNodes.MaxNumberOfBlocks);
                        if (persistTarget > LastPersistedBlockNumber)
                        {
                            Persist(persistTarget);
                            AnnounceReorgBoundaries();
                        }
                    }
                }
                if (trieType == TrieType.Storage)
                {
                    while (_destroyPrefixes.TryDequeue(out var prefix))
                    {
                        _committedNodes.AddRemovedPrefix(blockNumber, prefix);
                    }
                }
                // set rootHash here to account for root hashes for the storage trees also in every block
                // there needs to be a better way to handle this
                _committedNodes.SetRootHashForBlock(blockNumber, root?.Keccak);

                if (trieType == TrieType.State) CurrentPackage = null;
            }
            finally
            {
                _currentBatch?.Dispose();
                _currentBatch = null;
            }
        }

        public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        public byte[]? TryLoadRlp(Span<byte> path, IKeyValueStore? keyValueStore)
        {
            //if (IsPathCleaned(path))
            //    return null;

            keyValueStore ??= _keyValueStore;
            byte[] keyPath = Nibbles.NibblesToByteStorage(path);
            byte[]? rlp = _currentBatch?[keyPath] ?? keyValueStore[keyPath];
            if (rlp is null || rlp[0] != PathMarker) return rlp;

            rlp = _currentBatch?[rlp.AsSpan()[1..]] ?? keyValueStore[rlp.AsSpan()[1..]];
            return rlp;
        }

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

        private byte[] LoadRlp(Span<byte> path, IKeyValueStore? keyValueStore, Keccak rootHash = null)
        {
            byte[] keyPath = Nibbles.NibblesToByteStorage(path);
            byte[]? rlp = TryLoadRlp(path, keyValueStore);

            if (rlp is null)
            {
                throw new TrieException($"Node {keyPath.ToHexString()} is missing from the DB | path.Length {path.Length} - {Nibbles.ToCompactHexEncoding(path.ToArray()).ToHexString()}");
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

        public bool IsPersisted(Keccak keccak, byte[] childPath)
        {
            byte[]? rlp = TryLoadRlp(childPath, _keyValueStore);
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
            TrieNode node = _committedNodes.GetNode(storagePrefix.ToArray().Concat(nodePath.ToArray()).ToArray(), keccak);
            if (node is null)
            {
                return new TrieNode(NodeType.Unknown, path: nodePath)
                {
                    StoreNibblePathPrefix = storagePrefix.ToArray()
                };
            }

            return node.FullRlp is null ? null : node;
        }

        public TrieNode? FindCachedOrUnknown(Span<byte> nodePath, Span<byte> storagePrefix, Keccak rootHash)
        {
            TrieNode node = _committedNodes.GetNode(rootHash, storagePrefix.ToArray().Concat(nodePath.ToArray()).ToArray());
            if (node is null)
            {
                return new TrieNode(NodeType.Unknown, path: nodePath)
                {
                    StoreNibblePathPrefix = storagePrefix.ToArray()
                };
            }
            return node.FullRlp is null ? null : node;
        }

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
        /// Persists all transient (not yet persisted) blocks un till <paramref name="persistUntilBlock"/> block number.
        /// After this action we are sure that the full state is available for the block <paramref name="persistUntilBlock"/>.
        /// </summary>
        /// <param name="persistUntilBlock">Persist all the block changes until this block number</param>
        private void Persist(long persistUntilBlock)
        {
            try
            {
                _currentBatch ??= _keyValueStore.StartBatch();

                Stopwatch stopwatch = Stopwatch.StartNew();
                _committedNodes.PersistUntilBlock(persistUntilBlock, _currentBatch);
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
                _currentBatch?.Dispose();
                _currentBatch = null;
            }
        }

        private void Persist(TrieNode currentNode, long blockNumber)
        {
            _currentBatch ??= _keyValueStore.StartBatch();
            if (currentNode is null)
            {
                throw new ArgumentNullException(nameof(currentNode));
            }

            if (currentNode.Keccak is not null || currentNode.FullRlp is null)
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

        #endregion

        public void SaveNodeDirectly(long blockNumber, TrieNode trieNode, IKeyValueStore? keyValueStore = null, bool withDelete = false)
        {
            keyValueStore ??= _keyValueStore;

            byte[] fullPath = trieNode.FullPath ?? throw new ArgumentNullException();
            byte[] pathBytes = Nibbles.NibblesToByteStorage(fullPath);

            if (trieNode.IsLeaf)
            {
                Span<byte> pathToNodeNibbles = stackalloc byte[trieNode.StoreNibblePathPrefix.Length + trieNode.PathToNode!.Length];
                trieNode.StoreNibblePathPrefix.CopyTo(pathToNodeNibbles);
                Span<byte> pathToNodeSlice = pathToNodeNibbles.Slice(trieNode.StoreNibblePathPrefix.Length);
                trieNode.PathToNode.CopyTo(pathToNodeSlice);
                byte[] pathToNodeBytes = Nibbles.NibblesToByteStorage(pathToNodeNibbles);

                _logger.Info($"Saving node {trieNode.NodeType} path to nibbles {pathToNodeNibbles.ToHexString()}, bytes: {pathToNodeBytes.ToHexString()}, full: {pathBytes.ToHexString()}");

                if (trieNode.FullRlp == null)
                {
                    keyValueStore[pathToNodeBytes] = null;
                }
                else
                {
                    if (trieNode.PathToNode.Length < 64)
                    {
                        byte[] newPath = new byte[pathBytes.Length + 1];
                        Array.Copy(pathBytes, 0, newPath, 1, pathBytes.Length);
                        newPath[0] = PathMarker;
                        if (withDelete)
                        {
                            RequestDeletionForLeaf(pathToNodeNibbles, trieNode.FullPath);
                            //_emptyPrefixes.Add(pathToNodeNibbles.ToArray());
                            //DeleteSubtreeWithRangeDelete(pathToNodeNibbles, keyValueStore);
                            //_dbPrunner.EnqueueRange()
                            //DeleteTreeVisitor deleteVistor = new(_keyValueStore, this, new DeleteTreeVisitorContext()
                            //{
                            //    StoreNibblePrefix = trieNode.StoreNibblePathPrefix
                            //});
                            //deleteVistor.DeleteSubTree(trieNode.PathToNode);
                        }
                        //_logger.Info($"Save path {trieNode.PathToNode.ToHexString()}");
                        keyValueStore[pathToNodeBytes] = newPath;
                    }
                }
            }
            else
            {
                _logger.Info($"Saving node {trieNode.NodeType} full: {pathBytes.ToHexString()}");
            }
            if (withDelete)
            {
                if (trieNode.IsBranch)
                {
                    RequestDeletionForBranch(trieNode);
                }
                else if (trieNode.IsExtension)
                {
                    _logger.Info($"Missed extension handling {pathBytes.ToHexString()}");
                }
            }

            //_logger.Info($"Save path {trieNode.FullPath.ToHexString()}");
            keyValueStore[pathBytes] = trieNode.FullRlp;
        }

        public bool ExistsInDB(Keccak hash, byte[] pathNibbles)
        {
            byte[]? rlp = TryLoadRlp(pathNibbles.AsSpan(), _keyValueStore);
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
            get => _currentBatch?[key] ?? _keyValueStore[key];
        }
        /// <summary>
        /// This method is here to support testing.
        /// </summary>
        public void ClearCache()
        { }

        public void DeleteByRange(Span<byte> startKey, Span<byte> endKey)
        {
            _keyValueStore.DeleteByRange(startKey, endKey);
        }

        public void MarkPrefixDeleted(ReadOnlySpan<byte> keyPrefixNibbles)
        {
            bool addPrefixAsDeleted = _committedNodes.IsPathCached(keyPrefixNibbles);
            if (!addPrefixAsDeleted)
            {
                byte[] keyPath = Nibbles.NibblesToByteStorage(keyPrefixNibbles.ToArray());
                byte[]? rlp = _currentBatch?[keyPath] ?? _keyValueStore[keyPath];
                addPrefixAsDeleted &= rlp != null;
            }

            if (addPrefixAsDeleted)
                _destroyPrefixes.Enqueue(keyPrefixNibbles.ToArray());
        }

        public void DeleteSubtreeWithRangeDelete(Span<byte> pathNibbles, IKeyValueStore? keyValueStore)
        {
            Stopwatch sw = Stopwatch.StartNew();

            if (_logger.IsInfo) _logger.Info($"Deleting all paths prefixed {pathNibbles.ToHexString()}");

            if (pathNibbles.Length == 66 || pathNibbles.Length == 0)
            {
                _keyValueStore[Nibbles.NibblesToByteStorage(pathNibbles)] = null;
                return;
            }

            int fullKeyLength = pathNibbles.Length >= 66 ? 66 : 33;

            (byte[] from, byte[] to) = GetDeleteKeyFromNibblePrefix(pathNibbles, fullKeyLength, 0);
            if (_logger.IsInfo) _logger.Info($"Deleting from {from.ToHexString()} to {to.ToHexString()}");
            _keyValueStore.DeleteByRange(from, to);

            (from, to) = GetDeleteKeyFromNibblePrefix(pathNibbles, fullKeyLength, 1);
            if (_logger.IsInfo) _logger.Info($"Deleting from {from.ToHexString()} to {to.ToHexString()}");
            _keyValueStore.DeleteByRange(from, to);

            sw.Stop();
            Interlocked.Add(ref _deletionsTotalus, sw.ElapsedMicroseconds());
            _deletionCount++;
            if (_deletionCount % 1000 == 0)
            {
                if (_logger.IsInfo) _logger.Info($"Deletion took {_deletionsTotalus / 1000.0f} ms");
            }
        }

        public void DeleteSubtree(Span<byte> pathNibbles, Span<byte> storeNibblePrefix, bool deleteSelf, IKeyValueStore? keyValueStore)
        {
            keyValueStore ??= _keyValueStore;

            byte[] rlp = TryLoadRlp(pathNibbles, keyValueStore);
            if (rlp is not null)
            {
                TrieNode oldNode = new(NodeType.Unknown, pathNibbles[storeNibblePrefix.Length..].ToArray(), null, rlp)
                {
                    StoreNibblePathPrefix = storeNibblePrefix.ToArray()
                };
                oldNode.ResolveNode(this);

                _logger.Info($"Deleting old node: path {Nibbles.ToCompactHexEncoding(pathNibbles.ToArray()).ToHexString()} | node {oldNode}");

                if (oldNode.IsBranch)
                {
                    Span<byte> childPath = stackalloc byte[pathNibbles.Length + 1];
                    pathNibbles.CopyTo(childPath);
                    for (byte i = 0; i < 16; i++)
                    {
                        if (!oldNode.IsChildNull(i))
                        {
                            _logger.Info($"Deleting child {i} of node {oldNode}");
                            childPath[^1] = i;
                            DeleteSubtree(childPath, storeNibblePrefix, true, keyValueStore);
                        }
                    }
                }
                if (oldNode.IsLeaf)
                {
                    keyValueStore[Nibbles.NibblesToByteStorage(oldNode.FullPath)] = null;
                }
                if (oldNode.IsExtension)
                {
                    Span<byte> childPath = stackalloc byte[pathNibbles.Length + oldNode.Key.Length];
                    pathNibbles.CopyTo(childPath);
                    oldNode.Key.CopyTo(childPath[pathNibbles.Length..]);
                    DeleteSubtree(childPath, storeNibblePrefix, true, keyValueStore);
                }

                if (deleteSelf)
                {
                    keyValueStore[Nibbles.NibblesToByteStorage(pathNibbles)] = null;
                }
            }
        }

        public static (byte[], byte[]) GetDeleteKeyFromNibblePrefix(Span<byte> prefix, int maxLength, int oddityOverride)
        {
            byte[] fromKey = new byte[maxLength];
            byte[] toKey = new byte[maxLength];
            Array.Fill(toKey, (byte)0xff, 1, maxLength - 1);

            int oddity = prefix.Length % 2;

            if (oddity == oddityOverride)
            {
                for (int i = 0; i < prefix.Length / 2; i++)
                {
                    toKey[i + 1] = fromKey[i + 1] = Nibbles.ToByte(prefix[2 * i + oddity], prefix[2 * i + 1 + oddity]);
                }
                if (oddity == 1)
                {
                    toKey[0] = fromKey[0] = Nibbles.ToByte(1, prefix[0]);
                }
            }
            else if (oddity == 1 && oddityOverride == 0)
            {
                for (int i = 0; i < prefix.Length / 2; i++)
                {
                    toKey[i + 1] = fromKey[i + 1] = Nibbles.ToByte(prefix[2 * i], prefix[2 * i + 1]);
                }
                fromKey[prefix.Length / 2 + 1] = Nibbles.ToByte(prefix[^1], 0);
                toKey[prefix.Length / 2 + 1] = Nibbles.ToByte(prefix[^1], 0xf);
                //toKey[0] = fromKey[0] = 0x00;
            }
            else if (oddity == 0 && oddityOverride == 1)
            {
                for (int i = 0; i < prefix.Length / 2 - 1; i++)
                {
                    toKey[i + 1] = fromKey[i + 1] = Nibbles.ToByte(prefix[2 * i + oddityOverride], prefix[2 * i + 1 + oddityOverride]);
                }
                toKey[0] = fromKey[0] = Nibbles.ToByte(1, prefix[0]);

                fromKey[prefix.Length / 2] = Nibbles.ToByte(prefix[^1], 0);
                toKey[prefix.Length / 2] = Nibbles.ToByte(prefix[^1], 0xf);
            }
            return (fromKey, toKey);
        }

        public static (byte[], byte[]) GetDeleteKeyFromNibbles(Span<byte> nibbleFrom, Span<byte> nibbleTo, int maxLength, int oddityOverride)
        {
            byte[] fromKey = EncodePathWithEnforcedOddity(nibbleFrom, maxLength, oddityOverride, 0x00);
            byte[] toKey = EncodePathWithEnforcedOddity(nibbleTo, maxLength, oddityOverride, 0xff);

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

        public void RequestDeletionForLeaf(Span<byte> pathToNodeNibbles, Span<byte> fullPathNibbles)
        {
            int fullKeyLength = fullPathNibbles.Length >= 66 ? 66 : 33;

            (byte[] from, byte[] to) = GetDeleteKeyFromNibbles(pathToNodeNibbles, fullPathNibbles, fullKeyLength, 0);
            _logger.Info($"From: {from.ToHexString()} To: {to.ToHexString()}");
            _dbPrunner.EnqueueRange(from, to);

            (from, to) = GetDeleteKeyFromNibbles(pathToNodeNibbles, fullPathNibbles, fullKeyLength, 1);
            _logger.Info($"From: {from.ToHexString()} To: {to.ToHexString()}");
            _dbPrunner.EnqueueRange(from, to);

            byte[] modified = fullPathNibbles.ToArray();
            (from, to) = GetDeleteKeyFromNibbles(modified.IncrementNibble(), pathToNodeNibbles, fullKeyLength, 0);
            _logger.Info($"From: {from.ToHexString()} To: {to.ToHexString()}");
            _dbPrunner.EnqueueRange(from, to);

            (from, to) = GetDeleteKeyFromNibbles(modified, pathToNodeNibbles, fullKeyLength, 1);
            _logger.Info($"From: {from.ToHexString()} To: {to.ToHexString()}");
            _dbPrunner.EnqueueRange(from, to);
        }

        public void RequestDeletionForBranch(TrieNode branchNode)
        {
            void GenerateRangesAndRequest(Span<byte> childPathFrom, Span<byte> childPathTo, byte? from, byte? to)
            {
                int fullKeyLength = childPathFrom.Length >= 66 ? 66 : 33;

                childPathFrom[^1] = from.Value;
                childPathTo[^1] = to ?? from.Value;
                (byte[] fromKey, byte[] toKey) = GetDeleteKeyFromNibbles(childPathFrom, childPathTo, fullKeyLength, 0);
                _logger.Info($"From: {fromKey.ToHexString()} To: {toKey.ToHexString()}");
                _dbPrunner.EnqueueRange(fromKey, toKey);

                (fromKey, toKey) = GetDeleteKeyFromNibbles(childPathFrom, childPathTo, fullKeyLength, 1);
                _logger.Info($"From: {fromKey.ToHexString()} To: {toKey.ToHexString()}");
                _dbPrunner.EnqueueRange(fromKey, toKey);
            }

            Span<byte> childPathFrom = stackalloc byte[branchNode.FullPath.Length + 1];
            Span<byte> childPathTo = stackalloc byte[branchNode.FullPath.Length + 1];
            branchNode.FullPath.CopyTo(childPathFrom);
            branchNode.FullPath.CopyTo(childPathTo);
            byte? ind1 = null, ind2 = null;
            for (byte i = 0; i < 16; i++)
            {
                if (branchNode.IsChildNull(i))
                {
                    if (ind1 is null)
                        ind1 = i;
                    else
                        ind2 = i;
                    continue;
                }
                else
                {
                    if (ind1 is not null)
                    {
                        GenerateRangesAndRequest(childPathFrom, childPathTo, ind1, ind2);
                        ind1 = ind2 = null;
                    }
                }
            }
            if (ind1 is not null)
            {
                GenerateRangesAndRequest(childPathFrom, childPathTo, ind1, ind2);
                ind1 = ind2 = null;
            }
        }

        private bool IsPathCleaned(Span<byte> pathNibbles)
        {
            foreach (byte[] nibblePrefix in _emptyPrefixes)
            {
                int commonPrefixLength = 0;
                int maxLength = Math.Min(pathNibbles.Length, nibblePrefix.Length);

                for (int i = 0; i < maxLength && pathNibbles[i] == nibblePrefix[i]; i++, commonPrefixLength++) { }

                if (commonPrefixLength == maxLength)
                {
                    if (_logger.IsInfo) _logger.Info($"Found a path that should be cleaned for {pathNibbles.ToString()} - deleted prefix {nibblePrefix}");
                    return true;
                }
            }
            return false;
        }

        public bool CanAccessByPath()
        {
            return _dbPrunner.IsPruningComplete;
        }

        public Task GetTask()
        {
            return _pruningTask;
        }

        private Task BuildCleaningTask()
        {
            return new Task(() =>
            {
                while (!_emptyPrefixes.IsCompleted)
                {
                    byte[] prefixNibbles = null;
                    try
                    {
                        prefixNibbles = _emptyPrefixes.Take();
                    }
                    catch (InvalidOperationException) { }

                    if (prefixNibbles is not null)
                    {
                        DeleteSubtreeWithRangeDelete(prefixNibbles, _keyValueStore);
                    }
                }
            });
        }
    }

    public class DeleteTreeVisitorContext
    {
        public byte[] StoreNibblePrefix;
        //public bool IsStorage => StoreNibblePrefix?.Length == 0;
    }

    public class DeleteTreeVisitor
    {
        public const byte BranchesCount = 16;

        protected IKeyValueStore _keyValueStore;
        protected DeleteTreeVisitorContext _context;
        protected TrieStoreByPath _trieStore;

        public DeleteTreeVisitor(IKeyValueStore keyValueStore, TrieStoreByPath trieStore, DeleteTreeVisitorContext context)
        {
            _keyValueStore = keyValueStore;
            _context = context;
            _trieStore = trieStore;
        }

        public void DeleteSubTree(Span<byte> pathNibbles)
        {
            byte[] rlp = TryLoadRlp(pathNibbles);
            if (rlp == null)
                return;

            TrieNode oldNode = new(NodeType.Unknown, pathNibbles.ToArray(), null, rlp)
            {
                StoreNibblePathPrefix = _context.StoreNibblePrefix
            };
            oldNode.ResolveNode(_trieStore);
            switch (oldNode.NodeType)
            {
                case NodeType.Branch:
                    DeleteBranch(oldNode);
                    break;
                case NodeType.Leaf:
                    DeleteLeaf(oldNode);
                    break;
                case NodeType.Extension:
                    DeleteExtension(oldNode);
                    break;
            }
        }

        public void CleanSubTree(Span<byte> pathNibbles)
        {
            byte[] rlp = TryLoadRlp(pathNibbles);
            if (rlp == null)
                return;

            TrieNode oldNode = new(NodeType.Unknown, pathNibbles.ToArray(), null, rlp)
            {
                StoreNibblePathPrefix = _context.StoreNibblePrefix
            };
            oldNode.ResolveNode(_trieStore);
            switch (oldNode.NodeType)
            {
                case NodeType.Branch:
                    DeleteBranch(oldNode);
                    break;
                case NodeType.Leaf:
                    DeleteLeaf(oldNode);
                    break;
                case NodeType.Extension:
                    DeleteExtension(oldNode);
                    break;
            }
        }

        public void DeleteLeaf(TrieNode node)
        {
            if (!node.IsLeaf)
                throw new ArgumentException(null, nameof(node));

            Span<byte> pathToNodeNibbles = stackalloc byte[node.StoreNibblePathPrefix.Length + node.PathToNode!.Length];
            node.StoreNibblePathPrefix.CopyTo(pathToNodeNibbles);
            Span<byte> pathToNodeSlice = pathToNodeNibbles.Slice(node.StoreNibblePathPrefix.Length);
            node.PathToNode.CopyTo(pathToNodeSlice);
            byte[] pathToNodeBytes = Nibbles.NibblesToByteStorage(pathToNodeNibbles);

            _keyValueStore[pathToNodeBytes] = null;
            _keyValueStore[Nibbles.NibblesToByteStorage(node.FullPath)] = null;
        }

        public void DeleteBranch(TrieNode node)
        {
            Span<byte> childPath = stackalloc byte[node.PathToNode.Length + 1];
            for (byte i = 0; i < BranchesCount; i++)
            {
                node.PathToNode.CopyTo(childPath);

                if (!node.IsChildNull(i))
                {
                    childPath[^1] = i;

                    DeleteSubTree(childPath);
                }

                _keyValueStore[Nibbles.NibblesToByteStorage(node.FullPath)] = null;
            }
        }

        public void DeleteExtension(TrieNode node)
        {
            Span<byte> childPath = stackalloc byte[node.PathToNode.Length + node.Key.Length];
            node.PathToNode.CopyTo(childPath);
            node.Key.CopyTo(childPath[node.PathToNode.Length..]);
            DeleteSubTree(childPath);

            _keyValueStore[Nibbles.NibblesToByteStorage(node.FullPath)] = null;
        }

        public void CleanBranch(TrieNode node)
        {
            Span<byte> childPath = stackalloc byte[node.PathToNode.Length + 1];
            for (byte i = 0; i < BranchesCount; i++)
            {
                node.PathToNode.CopyTo(childPath);
                childPath[^1] = i;

                if (!node.IsChildNull(i))
                {
                    CleanSubTree(childPath);
                }
                else
                {
                    DeleteSubTree(childPath);
                }

                _keyValueStore[Nibbles.NibblesToByteStorage(node.FullPath)] = null;
            }
        }

        public byte[]? TryLoadRlp(Span<byte> path)
        {
            byte[] keyPath = Nibbles.NibblesToByteStorage(path);
            byte[]? rlp = _keyValueStore[keyPath];
            if (rlp is null || rlp[0] != 128) return rlp;

            return _keyValueStore[rlp.AsSpan()[1..]];
        }
    }
}
