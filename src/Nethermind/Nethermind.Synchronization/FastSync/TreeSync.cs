// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.ByPathState;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;
using Nethermind.Trie.ByPath;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.FastSync
{
    public class TreeSync
    {
        public const int AlreadySavedCapacity = 1024 * 1024;
        public const int MaxRequestSize = 384;

        private const StateSyncBatch EmptyBatch = null;

        private static readonly AccountDecoder AccountDecoder = new();

        private readonly DetailedProgress _data;
        private readonly IPendingSyncItems _pendingItems;

        private readonly Keccak _fastSyncProgressKey = Keccak.Zero;

        private DateTime _lastReview = DateTime.UtcNow;
        private DateTime _currentSyncStart;
        private long _currentSyncStartSecondsInSync;

        private DateTime _lastResetRoot = DateTime.UtcNow - TimeSpan.FromHours(1);
        private TimeSpan _minTimeBetweenReset = TimeSpan.FromMinutes(2);

        private readonly ReaderWriterLockSlim _stateDbLock = new();
        private readonly ReaderWriterLockSlim _codeDbLock = new();

        private readonly Stopwatch _networkWatch = new();
        private long _handleWatch = new();

        private Keccak _rootNode = Keccak.EmptyTreeHash;
        private int _rootSaved;

        private readonly ILogger _logger;
        private readonly IDb _codeDb;
        private readonly IDb _stateDb;
        private readonly IByPathStateDb _pathStateDb;
        private readonly ITrieStore _stateStore;

        private readonly IBlockTree _blockTree;

        // This is not exactly a lock for read and write, but a RWLock serves it well. It protects the five field
        // below which need to be cleared atomically during reset root, hence the write lock, while allowing
        // concurrent request handling with the read lock.
        private readonly ReaderWriterLockSlim _syncStateLock = new();
        private readonly ConcurrentDictionary<StateSyncBatch, object?> _pendingRequests = new();
        private Dictionary<StateSyncItem, HashSet<DependentItem>> _dependencies = new(StateSyncItemComparer.Instance);
        // private LruKeyCache<Keccak> _alreadySavedNode = new(AlreadySavedCapacity, "saved nodes");
        // private LruKeyCache<Keccak> _alreadySavedCode = new(AlreadySavedCapacity, "saved nodes");
        private readonly HashSet<Keccak> _codesSameAsNodes = new();
        private readonly ConcurrentDictionary<Keccak, List<byte>> _additionalLeafNibbles;
        private byte[] farRightPath = new byte[64];
        private bool rootChanged = false;

        private BranchProgress _branchProgress;
        private int _hintsToResetRoot;
        private long _blockNumber;
        private SyncMode _syncMode;

        public TreeSync(SyncMode syncMode, IDb codeDb, IColumnsDb<StateColumns> stateDb, IBlockTree blockTree, TrieNodeResolverCapability resolverCapability, ILogManager logManager)
        {
            _syncMode = syncMode;
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));

            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            byte[] progress = _codeDb.Get(_fastSyncProgressKey);
            _data = new DetailedProgress(_blockTree.NetworkId, progress);
            _pendingItems = new PendingSyncItems();
            _branchProgress = new BranchProgress(0, _logger);

            if (resolverCapability == TrieNodeResolverCapability.Hash)
            {
                _stateStore = new TrieStore(stateDb, logManager);
            }
            else if (resolverCapability == TrieNodeResolverCapability.Path)
            {
                _pathStateDb = stateDb as IByPathStateDb;
                _stateStore = new TrieStoreByPath(stateDb, logManager);
            }

            _additionalLeafNibbles = new ConcurrentDictionary<Keccak, List<byte>>();
        }

        public async Task<StateSyncBatch?> PrepareRequest(SyncMode syncMode)
        {
            try
            {
                List<StateSyncItem> requestItems = _pendingItems.TakeBatch(MaxRequestSize);
                LogRequestInfo(requestItems);

                long secondsInCurrentSync = (long)(DateTime.UtcNow - _currentSyncStart).TotalSeconds;

                if (requestItems.Count > 0)
                {
                    StateSyncBatch result = new(_rootNode, requestItems[0].NodeDataType, requestItems);

                    Interlocked.Add(ref _data.RequestedNodesCount, result.RequestedNodes.Count);
                    Interlocked.Exchange(ref _data.SecondsInSync, _currentSyncStartSecondsInSync + secondsInCurrentSync);

                    if (_logger.IsTrace) _logger.Trace($"After preparing a request of {requestItems.Count} from ({_pendingItems.Description}) nodes | {_dependencies.Count}");
                    if (_logger.IsTrace) _logger.Trace($"Adding pending request {result}");
                    _pendingRequests.TryAdd(result, null);

                    Interlocked.Increment(ref Metrics.StateSyncRequests);
                    return await Task.FromResult(result);
                }

                if (requestItems.Count == 0 && secondsInCurrentSync >= Timeouts.Eth.TotalSeconds)
                {
                    // trying to reproduce past behaviour where we can recognize the transition time this way
                    Interlocked.Increment(ref _hintsToResetRoot);
                }

                return await Task.FromResult(EmptyBatch);
            }
            catch (Exception e)
            {
                _logger.Error("Error when preparing a batch", e);
                return await Task.FromResult(EmptyBatch);
            }
        }

        public SyncResponseHandlingResult HandleResponse(StateSyncBatch? batch, PeerInfo? peerInfo = null)
        {
            if (batch == EmptyBatch)
            {
                if (_logger.IsError) _logger.Error("Received empty batch as a response");
                return SyncResponseHandlingResult.InternalError;
            }

            if (_logger.IsTrace) _logger.Trace($"Removing pending request {batch}");

            try
            {
                _syncStateLock.EnterReadLock();
                try
                {
                    if (!_pendingRequests.TryRemove(batch, out _))
                    {
                        if (_logger.IsDebug) _logger.Debug($"Cannot remove pending request {batch}");
                        return SyncResponseHandlingResult.OK;
                    }

                    int requestLength = batch.RequestedNodes?.Count ?? 0;
                    int responseLength = batch.Responses?.Length ?? 0;

                    void AddAgainAllItems()
                    {
                        for (int i = 0; i < requestLength; i++)
                        {
                            AddNodeToPending(batch.RequestedNodes![i], null, "missing", true);
                        }
                    }

                    if (DateTime.UtcNow - _lastReview > TimeSpan.FromSeconds(60))
                    {
                        _lastReview = DateTime.UtcNow;
                        string reviewMessage = _pendingItems.RecalculatePriorities();
                        if (_logger.IsDebug) _logger.Debug(reviewMessage);
                    }

                    Stopwatch handleWatch = Stopwatch.StartNew();

                    bool isMissingRequestData = batch.RequestedNodes is null;
                    if (isMissingRequestData)
                    {
                        Interlocked.Increment(ref _hintsToResetRoot);

                        AddAgainAllItems();
                        if (_logger.IsWarn) _logger.Warn("Batch response had invalid format");
                        Interlocked.Increment(ref _data.InvalidFormatCount);
                        return SyncResponseHandlingResult.InternalError;
                    }

                    if (peerInfo == null)
                    {
                        AddAgainAllItems();
                        if (_logger.IsTrace) _logger.Trace("Batch was not assigned to any peer.");
                        Interlocked.Increment(ref _data.NotAssignedCount);
                        return SyncResponseHandlingResult.NotAssigned;
                    }

                    if (batch.Responses is null)
                    {
                        AddAgainAllItems();
                        if (_logger.IsTrace) _logger.Trace($"Peer {peerInfo} failed to satisfy request.");
                        Interlocked.Increment(ref _data.NotAssignedCount);
                        return SyncResponseHandlingResult.LesserQuality;
                    }

                    if (_logger.IsTrace)
                        _logger.Trace($"Received node data - {responseLength} items in response to {requestLength}");
                    int nonEmptyResponses = 0;
                    int invalidNodes = 0;
                    for (int i = 0; i < batch.RequestedNodes!.Count; i++)
                    {
                        StateSyncItem currentStateSyncItem = batch.RequestedNodes[i];

                        /* if the peer has limit on number of requests in a batch then the response will possibly be
                           shorter than the request */
                        if (batch.Responses.Length < i + 1)
                        {
                            AddNodeToPending(currentStateSyncItem, null, "missing", true);
                            continue;
                        }

                        /* if the peer does not have details of this particular node */
                        byte[] currentResponseItem = batch.Responses[i];
                        if (currentResponseItem is null)
                        {
                            AddNodeToPending(batch.RequestedNodes[i], null, "missing", true);
                            continue;
                        }

                        /* node sent data that is not consistent with its hash - it happens surprisingly often */
                        if (!ValueKeccak.Compute(currentResponseItem).BytesAsSpan
                                .SequenceEqual(currentStateSyncItem.Hash.Bytes))
                        {
                            AddNodeToPending(currentStateSyncItem, null, "missing", true);
                            if (_logger.IsTrace)
                                _logger.Trace(
                                    $"Peer sent invalid data (batch {requestLength}->{responseLength}) of length {batch.Responses[i]?.Length} of type {batch.RequestedNodes[i].NodeDataType} at level {batch.RequestedNodes[i].Level} of type {batch.RequestedNodes[i].NodeDataType} Keccak({batch.Responses[i].ToHexString()}) != {batch.RequestedNodes[i].Hash}");
                            invalidNodes++;
                            continue;
                        }

                        nonEmptyResponses++;
                        NodeDataType nodeDataType = currentStateSyncItem.NodeDataType;
                        if (nodeDataType == NodeDataType.Code)
                        {
                            bool processAsStorage = false;
                            lock (_codesSameAsNodes)
                            {
                                // It could be that a code request was sent before another branch found storage same as
                                // code situation, in which case, the storage request (for later branch) would not get
                                // sent out because of _dependencies check.
                                processAsStorage = _codesSameAsNodes.Contains(currentStateSyncItem.Hash);
                            }

                            // It is possible that the _codesSameAsNode is populated during processing of the code
                            // before its _dependencies record is removed. But this *should* be fairly rare, we dont
                            // want to introduce more lock and when the state root change, the request would have been
                            // re-sent for the storage. So user should not be blocked by this.
                            if (!processAsStorage)
                            {
                                SaveNode(currentStateSyncItem, currentResponseItem, null);
                                continue;
                            }

                            currentStateSyncItem = new StateSyncItem(currentStateSyncItem, NodeDataType.Storage);
                        }

                        HandleTrieNode(currentStateSyncItem, currentResponseItem, ref invalidNodes);
                    }

                    Interlocked.Add(ref _data.ConsumedNodesCount, nonEmptyResponses);
                    StoreProgressInDb();

                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"After handling response (non-empty responses {nonEmptyResponses}) of {batch.RequestedNodes.Count} from ({_pendingItems.Description}) nodes");

                    /* magic formula is ratio of our desired batch size - 1024 to Geth max batch size 384 times some missing nodes ratio */
                    bool isEmptish = (decimal)nonEmptyResponses / Math.Max(requestLength, 1) < 384m / 1024m * 0.75m;
                    if (isEmptish)
                    {
                        Interlocked.Increment(ref _hintsToResetRoot);
                        Interlocked.Increment(ref _data.EmptishCount);
                    }
                    else
                    {
                        Interlocked.Exchange(ref _hintsToResetRoot, 0);
                    }

                    /* here we are very forgiving for Geth nodes that send bad data fast */
                    bool isBadQuality = nonEmptyResponses > 64 &&
                                        (decimal)invalidNodes / Math.Max(requestLength, 1) > 0.50m;
                    if (isBadQuality) Interlocked.Increment(ref _data.BadQualityCount);

                    bool isEmpty = nonEmptyResponses == 0 && !isBadQuality;
                    if (isEmpty)
                    {
                        if (_logger.IsDebug)
                            _logger.Debug(
                                $"Peer sent no data in response to a request of length {batch.RequestedNodes.Count}");
                        return SyncResponseHandlingResult.NoProgress;
                    }

                    if (!isEmptish && !isBadQuality)
                    {
                        Interlocked.Increment(ref _data.OkCount);
                    }

                    SyncResponseHandlingResult result = isEmptish
                        ? SyncResponseHandlingResult.Emptish
                        : isBadQuality
                            ? SyncResponseHandlingResult.LesserQuality
                            : SyncResponseHandlingResult.OK;

                    _data.DisplayProgressReport(_pendingRequests.Count, _branchProgress, _logger);

                    long total = handleWatch.ElapsedMilliseconds + _networkWatch.ElapsedMilliseconds;
                    if (total != 0)
                    {
                        // calculate averages
                        if (_logger.IsTrace)
                            _logger.Trace(
                                $"Prepare batch {_networkWatch.ElapsedMilliseconds}ms ({(decimal)_networkWatch.ElapsedMilliseconds / total:P0}) - Handle {handleWatch.ElapsedMilliseconds}ms ({(decimal)handleWatch.ElapsedMilliseconds / total:P0})");
                    }

                    if (handleWatch.ElapsedMilliseconds > 250)
                    {
                        if (_logger.IsDebug)
                            _logger.Debug(
                                $"Handle watch {handleWatch.ElapsedMilliseconds}, DB reads {_data.DbChecks - _data.LastDbReads}, ratio {(decimal)handleWatch.ElapsedMilliseconds / Math.Max(1, _data.DbChecks - _data.LastDbReads)}");
                    }

                    Interlocked.Add(ref _handleWatch, handleWatch.ElapsedMilliseconds);
                    _data.LastDbReads = _data.DbChecks;
                    _data.AverageTimeInHandler =
                        (_data.AverageTimeInHandler * (_data.ProcessedRequestsCount - 1) +
                         _handleWatch) / _data.ProcessedRequestsCount;

                    Interlocked.Add(ref _data.HandledNodesCount, nonEmptyResponses);
                    return result;
                }
                finally
                {
                    _syncStateLock.ExitReadLock();
                }
            }
            catch (Exception e)
            {
                _logger.Error("Error when handling state sync response", e);
                return SyncResponseHandlingResult.InternalError;
            }
        }

        public (bool continueProcessing, bool finishSyncRound) ValidatePrepareRequest(SyncMode currentSyncMode)
        {
            if (_rootSaved == 1)
            {
                if (_logger.IsInfo) _logger.Info("StateNode sync: falling asleep - root saved");
                VerifyPostSyncCleanUp();
                return (false, true);
            }

            if ((currentSyncMode & _syncMode) != _syncMode)
            {
                return (false, false);
            }

            if (_rootNode == Keccak.EmptyTreeHash)
            {
                if (_logger.IsDebug) _logger.Info("StateNode sync: falling asleep - root is empty tree");
                return (false, true);
            }

            if (_hintsToResetRoot >= 32 && DateTime.UtcNow - _lastResetRoot > _minTimeBetweenReset)
            {
                if (_logger.IsDebug) _logger.Info("StateNode sync: falling asleep - many missing responses");
                return (false, true);
            }

            bool rootNodeKeyExists;
            _stateDbLock.EnterReadLock();
            try
            {
                // it finished downloading
                rootNodeKeyExists = _stateStore.Capability switch
                {
                    TrieNodeResolverCapability.Hash => _stateDb.KeyExists(_rootNode),
                    TrieNodeResolverCapability.Path => _stateStore.ExistsInDB(_rootNode, Array.Empty<byte>()),
                    _ => throw new ArgumentOutOfRangeException()
                };
            }
            catch (ObjectDisposedException)
            {
                return (false, false);
            }
            finally
            {
                _stateDbLock.ExitReadLock();
            }

            if (rootNodeKeyExists)
            {
                try
                {
                    _logger.Info($"STATE SYNC FINISHED:{Metrics.StateSyncRequests}, {Metrics.SyncedStateTrieNodes}");

                    VerifyPostSyncCleanUp();
                    return (false, true);
                }
                catch (ObjectDisposedException)
                {
                    return (false, false);
                }
            }

            return (true, false);
        }

        public void ResetStateRoot(SyncFeedState currentState)
        {
            ResetStateRoot(_blockNumber, _rootNode, currentState);
        }

        public void ResetStateRootToBestSuggested(SyncFeedState currentState)
        {
            BlockHeader bestSuggested = _blockTree.BestSuggestedHeader;
            if (bestSuggested is null || bestSuggested.Number == 0)
            {
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Starting the node data sync from the {bestSuggested.ToString(BlockHeader.Format.Short)} {bestSuggested.StateRoot} root");

            ResetStateRoot(bestSuggested.Number, bestSuggested.StateRoot!, currentState);
        }

        public void ResetStateRoot(long blockNumber, Keccak stateRoot, SyncFeedState currentState, bool forceRootChange = true)
        {
            _syncStateLock.EnterWriteLock();
            try
            {
                _lastResetRoot = DateTime.UtcNow;
                if (currentState != SyncFeedState.Dormant)
                {
                    throw new InvalidOperationException("Cannot reset state sync on an active feed");
                }

                Interlocked.Exchange(ref _hintsToResetRoot, 0);

                if (_logger.IsInfo) _logger.Info($"Setting state sync state root to {blockNumber} {stateRoot}");
                _currentSyncStart = DateTime.UtcNow;
                _currentSyncStartSecondsInSync = _data.SecondsInSync;

                _data.LastReportTime = (DateTime.UtcNow, DateTime.UtcNow);
                _data.LastSavedNodesCount = _data.SavedNodesCount;
                _data.LastRequestedNodesCount = _data.RequestedNodesCount;
                if (_rootNode != stateRoot)
                {
                    if (stateRoot != Keccak.EmptyTreeHash && _rootNode != Keccak.EmptyTreeHash || forceRootChange)
                        rootChanged = true;

                    if (rootChanged)
                    {
                        _pathStateDb?.StartPrunning();
                    }

                    _branchProgress = new BranchProgress(blockNumber, _logger);
                    _blockNumber = blockNumber;
                    _rootNode = stateRoot;
                    lock (_dependencies) _dependencies.Clear();
                    lock (_codesSameAsNodes) _codesSameAsNodes.Clear();

                    if (_logger.IsDebug) _logger.Debug($"Clearing node stacks ({_pendingItems.Description})");
                    _pendingItems.Clear();
                    Interlocked.Exchange(ref _rootSaved, 0);
                }
                else
                {
                    foreach ((StateSyncBatch pendingRequest, _) in _pendingRequests)
                    {
                        // re-add the pending request
                        for (int i = 0; i < pendingRequest.RequestedNodes.Count; i++)
                        {
                            AddNodeToPending(pendingRequest.RequestedNodes[i], null, "pending request", true);
                        }
                    }
                }

                _pendingRequests.Clear();

                bool hasOnlyRootNode = false;

                if (_rootNode != Keccak.EmptyTreeHash)
                {
                    if (_pendingItems.Count == 1)
                    {
                        // state root can only be located on state stream
                        StateSyncItem? potentialRoot = _pendingItems.PeekState();
                        if (potentialRoot?.Hash == _rootNode)
                        {
                            hasOnlyRootNode = true;
                        }
                    }

                    if (!hasOnlyRootNode)
                    {
                        AddNodeToPending(new StateSyncItem(_rootNode, null, null, NodeDataType.State), null, "initial");
                    }
                }
            }
            finally
            {
                _syncStateLock.ExitWriteLock();
            }
        }

        public DetailedProgress GetDetailedProgress()
        {
            return _data;
        }

        private AddNodeResult AddNodeToPending(StateSyncItem syncItem, DependentItem? dependentItem, string reason, bool missing = false)
        {
            if (!missing)
            {
                if (syncItem.Level <= 2)
                {
                    _branchProgress.ReportSynced(syncItem, NodeProgressState.Requested);
                }

                // LruKeyCache<Keccak> alreadySavedCache =
                //     syncItem.NodeDataType == NodeDataType.Code ? _alreadySavedCode : _alreadySavedNode;

                // Keccak hashToCheckInCache = syncItem.Hash;

                // Keccak hashToCheckInCache;
                // if (syncItem.NodeDataType != NodeDataType.Code && _stateStore.Capability == TrieNodeResolverCapability.Path)
                // {
                //     hashToCheckInCache = Keccak.Compute(syncItem.Hash.Bytes.Concat(syncItem.PathNibbles).ToArray());
                // }
                // else
                // {
                //     hashToCheckInCache = syncItem.Hash;
                // }

                // if (alreadySavedCache.Get(hashToCheckInCache))
                // {
                //     Interlocked.Increment(ref _data.CheckWasCached);
                //     _logger.Info($"Node already in the DB - skipping {syncItem.Hash}");
                //     _branchProgress.ReportSynced(syncItem, NodeProgressState.AlreadySaved);
                //     return AddNodeResult.AlreadySaved;
                // }

                ReaderWriterLockSlim lockToTake = syncItem.NodeDataType == NodeDataType.Code ? _codeDbLock : _stateDbLock;
                lockToTake.EnterReadLock();
                try
                {
                    IDb dbToCheck = syncItem.NodeDataType == NodeDataType.Code ? _codeDb : _stateDb;
                    Interlocked.Increment(ref _data.DbChecks);
                    bool keyExists;
                    if (_stateStore.Capability == TrieNodeResolverCapability.Path)
                    {
                        switch (syncItem.NodeDataType)
                        {
                            case NodeDataType.State:
                                keyExists = _stateStore.ExistsInDB(syncItem.Hash, syncItem.PathNibbles);
                                break;
                            case NodeDataType.Storage:
                                Span<byte> storagePath = new byte[66 + syncItem.PathNibbles.Length];
                                syncItem.AccountPathNibbles.CopyTo(storagePath);
                                storagePath[64] = 8;
                                storagePath[65] = 0;
                                syncItem.PathNibbles.CopyTo(storagePath.Slice(66));
                                keyExists = _stateStore.ExistsInDB(syncItem.Hash, storagePath.ToArray());
                                break;
                            case NodeDataType.None:
                            case NodeDataType.Code:
                            case NodeDataType.All:
                            default:
                                keyExists = dbToCheck.KeyExists(syncItem.Hash);
                                break;
                        }
                    }
                    else
                    {
                        keyExists = dbToCheck.KeyExists(syncItem.Hash);
                    }

                    if (keyExists)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Node already in the DB - skipping {syncItem.Hash}");
                        // alreadySavedCache.Set(syncItem.Hash);
                        Interlocked.Increment(ref _data.StateWasThere);
                        _branchProgress.ReportSynced(syncItem, NodeProgressState.AlreadySaved);
                        return AddNodeResult.AlreadySaved;
                    }

                    Interlocked.Increment(ref _data.StateWasNotThere);
                }
                finally
                {
                    lockToTake.ExitReadLock();
                }

                bool isAlreadyRequested;
                lock (_dependencies)
                {
                    isAlreadyRequested = _dependencies.ContainsKey(syncItem);
                    if (dependentItem is not null)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Adding dependency Hash: {syncItem.Hash} PathNibbles: {syncItem.PathNibbles.ToHexString()} AccountPath: {syncItem.AccountPathNibbles.ToHexString()} -> {dependentItem.SyncItem.Hash} PathNibbles: {dependentItem.SyncItem.PathNibbles.ToHexString()} AccountPath: {dependentItem.SyncItem.AccountPathNibbles.ToHexString()}");
                        AddDependency(syncItem, dependentItem);
                    }
                }

                /* same items can have same hashes and we only need them once
                 * there is an issue when we have an item, we add it to dependencies, then we request it and the request times out
                 * and we never request it again because it was already on the dependencies list */
                if (isAlreadyRequested)
                {
                    Interlocked.Increment(ref _data.CheckWasInDependencies);
                    if (_logger.IsTrace) _logger.Trace($"Node already requested - skipping {syncItem.Hash}");
                    return AddNodeResult.AlreadyRequested;
                }
            }

            _pendingItems.PushToSelectedStream(syncItem, _branchProgress.LastProgress);
            if (_logger.IsTrace) _logger.Trace($"Added a node {syncItem.Hash} - {reason}");
            return AddNodeResult.Added;
        }

        private void PossiblySaveDependentNodes(StateSyncItem item)
        {
            List<DependentItem> nodesToSave = new();
            lock (_dependencies)
            {
                if (_dependencies.TryGetValue(item, out HashSet<DependentItem> value))
                {
                    HashSet<DependentItem> dependentItems = value;

                    if (_logger.IsTrace)
                    {
                        string nodeNodes = dependentItems.Count == 1 ? "node" : "nodes";
                        if (_logger.IsTrace) _logger.Trace($"{dependentItems.Count} {nodeNodes} dependent on {item.Hash} {item.PathNibbles.ToHexString()}");
                    }

                    foreach (DependentItem dependentItem in dependentItems)
                    {
                        dependentItem.Counter--;

                        if (dependentItem.Counter == 0)
                        {
                            nodesToSave.Add(dependentItem);
                        }
                    }

                    _dependencies.Remove(item);
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"No nodes dependent on {item}");
                }
            }

            foreach (DependentItem dependentItem in nodesToSave)
            {
                if (dependentItem.IsAccount) Interlocked.Increment(ref _data.SavedAccounts);
                SaveNode(dependentItem.SyncItem, dependentItem.Value, dependentItem.Node);
            }
        }

        private void SaveNode(StateSyncItem syncItem, byte[] data, TrieNode? node)
        {
            if (_logger.IsTrace) _logger.Trace($"SAVE {new string('+', syncItem.Level * 2)}{syncItem.NodeDataType.ToString().ToUpperInvariant()} {syncItem.Hash}");
            Interlocked.Increment(ref _data.SavedNodesCount);
            switch (syncItem.NodeDataType)
            {
                case NodeDataType.State:
                    {
                        Debug.Assert(node is not null);
                        Interlocked.Increment(ref _data.SavedStateCount);
                        _stateDbLock.EnterWriteLock();
                        try
                        {
                            Interlocked.Add(ref _data.DataSize, data.Length);
                            Interlocked.Increment(ref Metrics.SyncedStateTrieNodes);
                            switch (_stateStore.Capability)
                            {
                                case TrieNodeResolverCapability.Hash:
                                    _stateDb.Set(syncItem.Hash, data);
                                    break;
                                case TrieNodeResolverCapability.Path:
                                    _stateStore.SaveNodeDirectly(0, node, withDelete: rootChanged);
                                    if (node.IsLeaf && _additionalLeafNibbles.TryRemove(syncItem.Hash, out List<byte> additionalNibbles))
                                    {
                                        // TODO: what is this for?
                                        foreach (byte nibble in additionalNibbles)
                                        {
                                            TrieNode clone = node.Clone();
                                            clone.PathToNode[^1] = nibble;
                                            _stateStore.SaveNodeDirectly(0, clone, withDelete: rootChanged);
                                        }
                                    }
                                    if (node.IsLeaf && Bytes.Comparer.Compare(node.FullPath, farRightPath) > 0)
                                    {
                                        Interlocked.Exchange(ref farRightPath, node.FullPath);
                                        if (_logger.IsInfo) _logger.Info($"Far right path set to {Nibbles.ToCompactHexEncoding(farRightPath).ToHexString()}");
                                    }
                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }
                        finally
                        {
                            _stateDbLock.ExitWriteLock();
                        }

                        break;
                    }
                case NodeDataType.Storage:
                    {
                        Debug.Assert(node is not null);
                        lock (_codesSameAsNodes)
                        {
                            if (_codesSameAsNodes.Contains(syncItem.Hash))
                            {
                                _codeDbLock.EnterWriteLock();
                                try
                                {
                                    Interlocked.Add(ref _data.DataSize, data.Length);
                                    Interlocked.Increment(ref Metrics.SyncedCodes);
                                    _codeDb.Set(syncItem.Hash, data);
                                }
                                finally
                                {
                                    _codeDbLock.ExitWriteLock();
                                }

                                _codesSameAsNodes.Remove(syncItem.Hash);
                            }
                        }

                        Interlocked.Increment(ref _data.SavedStorageCount);

                        _stateDbLock.EnterWriteLock();
                        try
                        {
                            Interlocked.Add(ref _data.DataSize, data.Length);
                            Interlocked.Increment(ref Metrics.SyncedStorageTrieNodes);
                            switch (_stateStore.Capability )
                            {
                                case TrieNodeResolverCapability.Hash:
                                    _stateDb.Set(syncItem.Hash, data);
                                    break;
                                case TrieNodeResolverCapability.Path:
                                    _stateStore.SaveNodeDirectly(0, node, withDelete: rootChanged);
                                    if (node.IsLeaf && _additionalLeafNibbles.TryRemove(syncItem.Hash, out List<byte> additionalNibbles))
                                    {
                                        foreach (byte nibble in additionalNibbles)
                                        {
                                            TrieNode clone = node.Clone();
                                            clone.PathToNode[^1] = nibble;
                                            _stateStore.SaveNodeDirectly(0, clone, withDelete: rootChanged);
                                        }
                                    }
                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }
                        finally
                        {
                            _stateDbLock.ExitWriteLock();
                        }

                        break;
                    }
                case NodeDataType.Code:
                    {
                        Interlocked.Increment(ref _data.SavedCode);
                        _codeDbLock.EnterWriteLock();
                        try
                        {
                            Interlocked.Add(ref _data.DataSize, data.Length);
                            Interlocked.Increment(ref Metrics.SyncedCodes);
                            _codeDb.Set(syncItem.Hash, data);
                        }
                        finally
                        {
                            _codeDbLock.ExitWriteLock();
                        }

                        break;
                    }
            }

            if (syncItem.IsRoot)
            {
                if (_logger.IsInfo) _logger.Info($"Saving root {syncItem.Hash} of {_branchProgress.CurrentSyncBlock}");

                _pathStateDb?.EndOfCleanupRequests();

                _stateDb.Flush();
                _codeDb.Flush();

                Interlocked.Exchange(ref _rootSaved, 1);
            }

            _branchProgress.ReportSynced(syncItem.Level, syncItem.ParentBranchChildIndex, syncItem.BranchChildIndex, syncItem.NodeDataType, NodeProgressState.Saved);
            PossiblySaveDependentNodes(syncItem);
        }

        private void VerifyPostSyncCleanUp()
        {
            lock (_dependencies)
            {
                if (_dependencies.Count != 0)
                {
                    if (_logger.IsError) _logger.Error($"POSSIBLE FAST SYNC CORRUPTION | Dependencies hanging after the root node saved - count: {_dependencies.Count}, first: {_dependencies.Keys.First()}");
                }

                _dependencies = new Dictionary<StateSyncItem, HashSet<DependentItem>>(StateSyncItemComparer.Instance);
                // _alreadySaved = new LruKeyCache<Keccak>(AlreadySavedCapacity, "saved nodes");
            }

            if (_pendingItems.Count != 0)
            {
                if (_logger.IsError) _logger.Error($"POSSIBLE FAST SYNC CORRUPTION | Nodes left after the root node saved - count: {_pendingItems.Count}");
            }

            CleanupMemory();
        }

        private void CleanupMemory()
        {
            _syncStateLock.EnterWriteLock();
            try
            {
                _pendingRequests.Clear();
                _dependencies.Clear();
                // _alreadySavedNode.Clear();
                // _alreadySavedCode.Clear();
                _codesSameAsNodes.Clear();
            }
            finally
            {
                _syncStateLock.ExitWriteLock();
            }
        }

        private void StoreProgressInDb()
        {
            byte[] serializedData = _data.Serialize();
            _stateDbLock.EnterWriteLock();
            try
            {
                _codeDbLock.EnterWriteLock();
                try
                {
                    _codeDb[_fastSyncProgressKey.Bytes] = serializedData;
                }
                finally
                {
                    _codeDbLock.ExitWriteLock();
                }
            }
            finally
            {
                _stateDbLock.ExitWriteLock();
            }
        }

        private void HandleTrieNode(StateSyncItem currentStateSyncItem, byte[] currentResponseItem, ref int invalidNodes)
        {
            NodeDataType nodeDataType = currentStateSyncItem.NodeDataType;
            TrieNode trieNode = new TrieNode(NodeType.Unknown, currentStateSyncItem.PathNibbles, currentStateSyncItem.Hash, currentResponseItem);

            // _logger.Info($"Node Trace: {trieNode}");
            // check if this is a storage node - then it needs a storage prefix
            if (currentStateSyncItem.AccountPathNibbles is not null && currentStateSyncItem.AccountPathNibbles.Length != 0)
            {
                Span<byte> storagePrefix = new byte[66];
                currentStateSyncItem.AccountPathNibbles.CopyTo(storagePrefix);
                storagePrefix[64] = 8;
                storagePrefix[65] = 0;
                trieNode.StoreNibblePathPrefix = storagePrefix.ToArray();
            }
            trieNode.ResolveNode(NullTrieNodeResolver.Instance); // TODO: will this work now?
            // _logger.Info($"Decode Node: {trieNode}");
            // _logger.Info($"{trieNode.PathToNode?.ToHexString()} {trieNode.Key?.ToHexString()} {trieNode.StoreNibblePathPrefix.ToHexString()} {trieNode.Keccak}");

            switch (trieNode.NodeType)
            {
                case NodeType.Unknown:
                    invalidNodes++;
                    if (_logger.IsError) _logger.Error($"Node {currentStateSyncItem.Hash} resolved to {nameof(NodeType.Unknown)}");
                    break;
                case NodeType.Branch:
                    // Note the counter is set to 16 first before decrementing at each loop. This is because it is possible
                    // than the node is downloaded during the loop which may trigger a save on this node.
                    DependentItem dependentBranch = new(currentStateSyncItem, currentResponseItem, 16, false, trieNode);

                    // children may have the same hashes (e.g. a set of accounts with the same code at different addresses)
                    HashSet<Keccak?> alreadyProcessedChildHashes = new();

                    Span<byte> branchChildPath = stackalloc byte[currentStateSyncItem.PathNibbles.Length + 1];
                    currentStateSyncItem.PathNibbles.CopyTo(branchChildPath[..currentStateSyncItem.PathNibbles.Length]);

                    for (int childIndex = 15; childIndex >= 0; childIndex--)
                    {
                        Keccak? childHash = trieNode.GetChildHash(childIndex);
                        if (childHash is not null && alreadyProcessedChildHashes.Contains(childHash))
                        {
                            if (_additionalLeafNibbles.TryGetValue(childHash, out List<byte> leafNibbles))
                                leafNibbles.Add((byte)childIndex);
                            else
                                _additionalLeafNibbles.TryAdd(childHash, new List<byte> { (byte)childIndex });

                            dependentBranch.Counter--;
                            continue;
                        }

                        alreadyProcessedChildHashes.Add(childHash);

                        if (childHash is not null)
                        {
                            branchChildPath[currentStateSyncItem.PathNibbles.Length] = (byte)childIndex;

                            AddNodeResult addChildResult = AddNodeToPending(
                                new StateSyncItem(childHash, currentStateSyncItem.AccountPathNibbles, branchChildPath.ToArray(), nodeDataType, currentStateSyncItem.Level + 1, CalculateRightness(trieNode.NodeType, currentStateSyncItem, childIndex))
                                {
                                    BranchChildIndex = (short)childIndex,
                                    ParentBranchChildIndex = currentStateSyncItem.BranchChildIndex
                                },
                                dependentBranch,
                                "branch child");

                            if (addChildResult != AddNodeResult.AlreadySaved)
                            {
                            }
                            else
                            {
                                _branchProgress.ReportSynced(currentStateSyncItem.Level + 1, currentStateSyncItem.BranchChildIndex, childIndex, currentStateSyncItem.NodeDataType, NodeProgressState.AlreadySaved);
                                dependentBranch.Counter--;
                            }
                        }
                        else
                        {
                            _branchProgress.ReportSynced(currentStateSyncItem.Level + 1, currentStateSyncItem.BranchChildIndex, childIndex, currentStateSyncItem.NodeDataType, NodeProgressState.Empty);
                            dependentBranch.Counter--;
                        }
                    }

                    if (dependentBranch.Counter == 0)
                    {
                        SaveNode(currentStateSyncItem, currentResponseItem, trieNode);
                    }

                    break;
                case NodeType.Extension:
                    Keccak? next = trieNode.GetChild(NullTrieNodeResolver.Instance, 0)?.Keccak;
                    if (next is not null)
                    {
                        DependentItem dependentItem = new(currentStateSyncItem, currentResponseItem, 1, false, trieNode);

                        // Add nibbles to StateSyncItem.PathNibbles
                        Span<byte> childPath = stackalloc byte[currentStateSyncItem.PathNibbles.Length + trieNode.Key!.Length];
                        currentStateSyncItem.PathNibbles.CopyTo(childPath[..currentStateSyncItem.PathNibbles.Length]);
                        trieNode.Key!.CopyTo(childPath[currentStateSyncItem.PathNibbles.Length..]);

                        AddNodeResult addResult = AddNodeToPending(
                            new StateSyncItem(
                                next,
                                currentStateSyncItem.AccountPathNibbles,
                                childPath.ToArray(),
                                nodeDataType,
                                currentStateSyncItem.Level + trieNode.Key!.Length,
                                CalculateRightness(trieNode.NodeType, currentStateSyncItem, 0))
                            { ParentBranchChildIndex = currentStateSyncItem.BranchChildIndex },
                            dependentItem,
                            "extension child");

                        if (addResult == AddNodeResult.AlreadySaved)
                        {
                            SaveNode(currentStateSyncItem, currentResponseItem, trieNode);
                        }
                    }
                    else
                    {
                        /* this happens when we have a short RLP format of the node
                                     * that would not be stored as Keccak but full RLP*/
                        SaveNode(currentStateSyncItem, currentResponseItem, trieNode);
                    }

                    break;
                case NodeType.Leaf:
                    if (nodeDataType == NodeDataType.State)
                    {
                        _pendingItems.MaxStateLevel = 64;
                        DependentItem dependentItem = new(currentStateSyncItem, currentResponseItem, 2, true, trieNode);
                        (Keccak codeHash, Keccak storageRoot) = AccountDecoder.DecodeHashesOnly(new RlpStream(trieNode.Value.ToArray()));
                        if (codeHash != Keccak.OfAnEmptyString)
                        {
                            // prepare a branch without the code DB
                            // this only protects against being same as storage root?
                            if (codeHash == storageRoot)
                            {
                                lock (_codesSameAsNodes)
                                {
                                    _codesSameAsNodes.Add(codeHash);
                                }

                                dependentItem.Counter--;
                            }
                            else
                            {
                                AddNodeResult addCodeResult = AddNodeToPending(new StateSyncItem(codeHash, null, null, NodeDataType.Code, 0, currentStateSyncItem.Rightness), dependentItem, "code");
                                if (addCodeResult == AddNodeResult.AlreadySaved) dependentItem.Counter--;
                            }
                        }
                        else
                        {
                            dependentItem.Counter--;
                        }

                        if (storageRoot != Keccak.EmptyTreeHash)
                        {
                            // it's a leaf with a storage, so we need to copy the current path (full 64 nibbles) to StateSyncItem.AccountPathNibbles
                            // and StateSyncItem.PathNibbles will start from null (storage root)
                            Span<byte> childPath = stackalloc byte[currentStateSyncItem.PathNibbles.Length + trieNode.Key!.Length];
                            currentStateSyncItem.PathNibbles.CopyTo(childPath[..currentStateSyncItem.PathNibbles.Length]);
                            trieNode.Key!.CopyTo(childPath[currentStateSyncItem.PathNibbles.Length..]);

                            AddNodeResult addStorageNodeResult = AddNodeToPending(new StateSyncItem(storageRoot, childPath.ToArray(), null, NodeDataType.Storage, 0, currentStateSyncItem.Rightness), dependentItem, "storage");
                            if (addStorageNodeResult != AddNodeResult.AlreadySaved)
                            {
                            }
                            else if (codeHash == storageRoot)
                            {
                                // Maybe a storageRoot equal to the codeHash was stored by another branch.
                                // Double checking code...
                                AddNodeResult addCodeResult = AddNodeToPending(new StateSyncItem(codeHash, null, null, NodeDataType.Code, 0, currentStateSyncItem.Rightness), dependentItem, "code");
                                if (addCodeResult == AddNodeResult.AlreadySaved) dependentItem.Counter--;
                            }
                            else
                            {
                                dependentItem.Counter--;
                            }
                        }
                        else
                        {
                            dependentItem.Counter--;
                        }

                        if (dependentItem.Counter == 0)
                        {
                            Interlocked.Increment(ref _data.SavedAccounts);
                            SaveNode(currentStateSyncItem, currentResponseItem, trieNode);
                        }
                    }
                    else
                    {
                        _pendingItems.MaxStorageLevel = 64;
                        SaveNode(currentStateSyncItem, currentResponseItem, trieNode);
                    }

                    break;
                default:
                    if (_logger.IsError) _logger.Error($"Unknown value {currentStateSyncItem.NodeDataType} of {nameof(NodeDataType)} at {currentStateSyncItem.Hash}");
                    invalidNodes++;
                    break;
            }
        }

        private static uint CalculateRightness(NodeType nodeType, StateSyncItem currentStateSyncItem, int childIndex)
        {
            if (nodeType == NodeType.Branch)
            {
                return currentStateSyncItem.Rightness + (uint)Math.Pow(16, Math.Max(0, 7 - currentStateSyncItem.Level)) * (uint)childIndex;
            }

            if (nodeType == NodeType.Extension)
            {
                return currentStateSyncItem.Rightness + (uint)Math.Pow(16, Math.Max(0, 7 - currentStateSyncItem.Level)) * 16 - 1;
            }

            throw new InvalidOperationException($"Not designed for {nodeType}");
        }

        /// <summary>
        /// Stores items that cannot be yet persisted. These items will be persisted as soon as all their descendants
        /// get persisted.
        /// </summary>
        /// <param name="dependency">Sync item that this item is dependent on.</param>
        /// <param name="dependentItem">Item that can only be persisted if all its dependenies are persisted</param>
        private void AddDependency(StateSyncItem dependency, DependentItem dependentItem)
        {
            lock (_dependencies)
            {
                if (!_dependencies.ContainsKey(dependency))
                {
                    _dependencies[dependency] = new HashSet<DependentItem>(DependentItemComparer.Instance);
                }

                _dependencies[dependency].Add(dependentItem);
            }
        }

        private void LogRequestInfo(List<StateSyncItem> requestHashes)
        {
            int requestSize = requestHashes.Count;
            if (requestSize < MaxRequestSize)
            {
                if (_logger.IsDebug) _logger.Debug($"Sending limited size request {requestSize} at level {_pendingItems.MaxStateLevel}");
            }

            if (_logger.IsTrace) _logger.Trace($"Preparing a request of length {requestSize} from ({_pendingItems.Description}) nodes");
            if (_logger.IsTrace)
            {
                foreach (StateSyncItem stateSyncItem in requestHashes)
                {
                    _logger.Trace($"Requesting {stateSyncItem.Hash}");
                }
            }
        }

        private enum AddNodeResult
        {
            AlreadySaved,
            AlreadyRequested,
            Added
        }
    }
}
