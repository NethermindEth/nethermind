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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;

namespace Nethermind.Synchronization.FastSync
{
    public partial class StateSyncFeed : SyncFeed<StateSyncBatch>, IDisposable
    {
        private const int AlreadySavedCapacity = 1024 * 64;
        private const int MaxRequestSize = 384;
        private const StateSyncBatch EmptyBatch = null;

        private static readonly AccountDecoder AccountDecoder = new AccountDecoder();

        private readonly DetailedProgress _data;
        private readonly IPendingSyncItems _pendingItems;

        private readonly Keccak _fastSyncProgressKey = Keccak.Zero;

        private DateTime _lastReview = DateTime.UtcNow;
        private DateTime _currentSyncStart;
        private long _currentSyncStartSecondsInSync;

        private readonly object _stateDbLock = new object();
        private readonly object _codeDbLock = new object();

        private readonly Stopwatch _networkWatch = new Stopwatch();
        private readonly Stopwatch _handleWatch = new Stopwatch();

        private Keccak _rootNode = Keccak.EmptyTreeHash;

        private readonly ILogger _logger;
        private readonly IDb _codeDb;
        private readonly IDb _stateDb;
        private readonly IDb _tempDb;
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly IBlockTree _blockTree;
        private readonly int _requestLimit;

        private readonly ConcurrentDictionary<StateSyncBatch, object> _pendingRequests = new ConcurrentDictionary<StateSyncBatch, object>();
        private Dictionary<Keccak, HashSet<DependentItem>> _dependencies = new Dictionary<Keccak, HashSet<DependentItem>>();
        private LruKeyCache<Keccak> _alreadySaved = new LruKeyCache<Keccak>(AlreadySavedCapacity, "saved nodes");
        private readonly HashSet<Keccak> _codesSameAsNodes = new HashSet<Keccak>();

        private StateSyncProgress _syncProgress;
        private int _hintsToResetRoot;

        public StateSyncFeed(ISnapshotableDb codeDb, ISnapshotableDb stateDb, IDb tempDb, ISyncModeSelector syncModeSelector, IBlockTree blockTree, ILogManager logManager, int requestLimit = 200)
            : base(logManager)
        {
            _codeDb = codeDb?.Innermost ?? throw new ArgumentNullException(nameof(codeDb));
            _stateDb = stateDb?.Innermost ?? throw new ArgumentNullException(nameof(stateDb));
            _tempDb = tempDb ?? throw new ArgumentNullException(nameof(tempDb));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _requestLimit = requestLimit;
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _syncModeSelector.Changed += SyncModeSelectorOnChanged;

            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            byte[] progress = _codeDb.Get(_fastSyncProgressKey);
            _data = new DetailedProgress(_blockTree.ChainId, progress);
            _pendingItems = new PendingSyncItems();
        }

        private void SyncModeSelectorOnChanged(object sender, SyncModeChangedEventArgs e)
        {
            if (CurrentState == SyncFeedState.Dormant)
            {
                if ((e.Current & SyncMode.StateNodes) == SyncMode.StateNodes)
                {
                    BlockHeader bestSuggested = _blockTree.BestSuggestedHeader;
                    if (bestSuggested == null || bestSuggested.Number == 0)
                    {
                        return;
                    }

                    if (_logger.IsInfo) _logger.Info($"Starting the node data sync from the {bestSuggested.ToString(BlockHeader.Format.Short)} {bestSuggested.StateRoot} root");
                    ResetStateRoot(bestSuggested.Number, bestSuggested.StateRoot);
                    Activate();
                }
            }
        }

        private AddNodeResult AddNodeToPending(StateSyncItem syncItem, DependentItem dependentItem, string reason, bool missing = false)
        {
            if (!missing)
            {
                if (syncItem.Level <= 2)
                {
                    _syncProgress.ReportSynced(syncItem.Level, syncItem.ParentBranchChildIndex, syncItem.BranchChildIndex, syncItem.NodeDataType, NodeProgressState.Requested);
                }

                if (_alreadySaved.Get(syncItem.Hash))
                {
                    Interlocked.Increment(ref _data.CheckWasCached);
                    if (_logger.IsTrace) _logger.Trace($"Node already in the DB - skipping {syncItem.Hash}");
                    return AddNodeResult.AlreadySaved;
                }

                object lockToTake = syncItem.NodeDataType == NodeDataType.Code ? _codeDbLock : _stateDbLock;
                lock (lockToTake)
                {
                    IDb dbToCheck = syncItem.NodeDataType == NodeDataType.Code ? _codeDb : _stateDb;
                    Interlocked.Increment(ref _data.DbChecks);
                    bool keyExists = dbToCheck.KeyExists(syncItem.Hash);
                    if (keyExists)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Node already in the DB - skipping {syncItem.Hash}");
                        _alreadySaved.Set(syncItem.Hash);
                        Interlocked.Increment(ref _data.StateWasThere);
                        return AddNodeResult.AlreadySaved;
                    }

                    Interlocked.Increment(ref _data.StateWasNotThere);
                }

                bool isAlreadyRequested;
                lock (_dependencies)
                {
                    isAlreadyRequested = _dependencies.ContainsKey(syncItem.Hash);
                    if (dependentItem != null)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Adding dependency {syncItem.Hash} -> {dependentItem.SyncItem.Hash}");
                        AddDependency(syncItem.Hash, dependentItem);
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

            _pendingItems.PushToSelectedStream(syncItem, _syncProgress.LastProgress);
            if (_logger.IsTrace) _logger.Trace($"Added a node {syncItem.Hash} - {reason}");
            return AddNodeResult.Added;
        }

        private void PossiblySaveDependentNodes(Keccak hash)
        {
            List<DependentItem> nodesToSave = new List<DependentItem>();
            lock (_dependencies)
            {
                if (_dependencies.ContainsKey(hash))
                {
                    HashSet<DependentItem> dependentItems = _dependencies[hash];

                    if (_logger.IsTrace)
                    {
                        string nodeNodes = dependentItems.Count == 1 ? "node" : "nodes";
                        _logger.Trace($"{dependentItems.Count} {nodeNodes} dependent on {hash}");
                    }

                    foreach (DependentItem dependentItem in dependentItems)
                    {
                        dependentItem.Counter--;
                        _dependencies.Remove(hash);
                        if (dependentItem.Counter == 0)
                        {
                            nodesToSave.Add(dependentItem);
                        }
                    }
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"No nodes dependent on {hash}");
                }
            }

            foreach (DependentItem dependentItem in nodesToSave)
            {
                if (dependentItem.IsAccount) Interlocked.Increment(ref _data.SavedAccounts);
                SaveNode(dependentItem.SyncItem, dependentItem.Value);
                _tempDb.Remove(dependentItem.SyncItem.Hash.Bytes);
            }
        }

        private void SaveNode(StateSyncItem syncItem, byte[] data)
        {
            if (_logger.IsTrace) _logger.Trace($"SAVE {new string('+', syncItem.Level * 2)}{syncItem.NodeDataType.ToString().ToUpperInvariant()} {syncItem.Hash}");
            Interlocked.Increment(ref _data.SavedNodesCount);
            switch (syncItem.NodeDataType)
            {
                case NodeDataType.State:
                {
                    Interlocked.Increment(ref _data.SavedStateCount);
                    lock (_stateDbLock)
                    {
                        Interlocked.Add(ref _data.DataSize, data.Length);
                        Interlocked.Increment(ref Metrics.SyncedStateTrieNodes);
                        _stateDb.Set(syncItem.Hash, data);
                    }

                    break;
                }
                case NodeDataType.Storage:
                {
                    lock (_codesSameAsNodes)
                    {
                        if (_codesSameAsNodes.Contains(syncItem.Hash))
                        {
                            lock (_codeDbLock)
                            {
                                Interlocked.Add(ref _data.DataSize, data.Length);
                                Interlocked.Increment(ref Metrics.SyncedCodes);
                                _codeDb.Set(syncItem.Hash, data);
                            }

                            _codesSameAsNodes.Remove(syncItem.Hash);
                        }
                    }

                    Interlocked.Increment(ref _data.SavedStorageCount);
                    lock (_stateDbLock)
                    {
                        Interlocked.Add(ref _data.DataSize, data.Length);
                        Interlocked.Increment(ref Metrics.SyncedStorageTrieNodes);
                        _stateDb.Set(syncItem.Hash, data);
                    }

                    break;
                }
                case NodeDataType.Code:
                {
                    Interlocked.Increment(ref _data.SavedCode);
                    lock (_codeDbLock)
                    {
                        Interlocked.Add(ref _data.DataSize, data.Length);
                        Interlocked.Increment(ref Metrics.SyncedCodes);
                        _codeDb.Set(syncItem.Hash, data);
                    }

                    break;
                }
            }

            if (syncItem.IsRoot)
            {
                if (_logger.IsInfo) _logger.Info($"Saving root {syncItem.Hash} of {_syncProgress.CurrentSyncBlock}");

                VerifyPostSyncCleanUp();
                _alreadySaved.Clear();
            }

            _syncProgress.ReportSynced(syncItem.Level, syncItem.ParentBranchChildIndex, syncItem.BranchChildIndex, syncItem.NodeDataType, NodeProgressState.Saved);
            PossiblySaveDependentNodes(syncItem.Hash);
        }

        private void VerifyPostSyncCleanUp()
        {
            lock (_dependencies)
            {
                if (_dependencies.Count != 0)
                {
                    if (_logger.IsError) _logger.Error($"POSSIBLE FAST SYNC CORRUPTION | Dependencies hanging after the root node saved - count: {_dependencies.Count}, first: {_dependencies.Keys.First()}");
                }

                _dependencies = new Dictionary<Keccak, HashSet<DependentItem>>();
                _alreadySaved = new LruKeyCache<Keccak>(AlreadySavedCapacity, "saved nodes");
            }

            if (_pendingItems.Count != 0)
            {
                if (_logger.IsError) _logger.Error($"POSSIBLE FAST SYNC CORRUPTION | Nodes left after the root node saved - count: {_pendingItems.Count}");
            }
        }

        public override SyncResponseHandlingResult HandleResponse(StateSyncBatch batch)
        {
            if (batch == EmptyBatch)
            {
                _logger.Error("Received empty batch as a response");
            }

            int requestLength = batch.RequestedNodes?.Length ?? 0;
            int responseLength = batch.Responses?.Length ?? 0;

            void AddAgainAllItems()
            {
                for (int i = 0; i < requestLength; i++)
                {
                    AddNodeToPending(batch.RequestedNodes[i], null, "missing", true);
                }
            }

            try
            {
                lock (_handleWatch)
                {
                    if (DateTime.UtcNow - _lastReview > TimeSpan.FromSeconds(60))
                    {
                        _lastReview = DateTime.UtcNow;
                        string reviewMessage = _pendingItems.RecalculatePriorities();
                        if (_logger.IsInfo) _logger.Info(reviewMessage);
                    }

                    _handleWatch.Restart();

                    bool requestWasMade = batch.Responses != null;
                    if (!requestWasMade)
                    {
                        AddAgainAllItems();
                        if (_logger.IsTrace) _logger.Trace($"Batch was not assigned to any peer.");
                        Interlocked.Increment(ref _data.NotAssignedCount);
                        return SyncResponseHandlingResult.NotAssigned;
                    }

                    bool isMissingRequestData = batch.RequestedNodes == null;
                    bool isMissingResponseData = batch.Responses == null;
                    bool hasValidFormat = !isMissingRequestData && !isMissingResponseData;

                    if (!hasValidFormat)
                    {
                        _hintsToResetRoot++;

                        AddAgainAllItems();
                        if (_logger.IsWarn) _logger.Warn($"Batch response had invalid format");
                        Interlocked.Increment(ref _data.InvalidFormatCount);
                        return isMissingRequestData ? SyncResponseHandlingResult.InternalError : SyncResponseHandlingResult.NotAssigned;
                    }

                    if (_logger.IsTrace) _logger.Trace($"Received node data - {responseLength} items in response to {requestLength}");
                    int nonEmptyResponses = 0;
                    int invalidNodes = 0;
                    for (int i = 0; i < batch.RequestedNodes.Length; i++)
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
                        if (currentResponseItem == null)
                        {
                            AddNodeToPending(batch.RequestedNodes[i], null, "missing", true);
                            continue;
                        }

                        /* node sent data that is not consistent with its hash - it happens surprisingly often */
                        if (!ValueKeccak.Compute(currentResponseItem).BytesAsSpan.SequenceEqual(currentStateSyncItem.Hash.Bytes))
                        {
                            AddNodeToPending(currentStateSyncItem, null, "missing", true);
                            if (_logger.IsTrace) _logger.Trace($"Peer sent invalid data (batch {requestLength}->{responseLength}) of length {batch.Responses[i]?.Length} of type {batch.RequestedNodes[i].NodeDataType} at level {batch.RequestedNodes[i].Level} of type {batch.RequestedNodes[i].NodeDataType} Keccak({batch.Responses[i].ToHexString()}) != {batch.RequestedNodes[i].Hash}");
                            invalidNodes++;
                            continue;
                        }

                        nonEmptyResponses++;
                        NodeDataType nodeDataType = currentStateSyncItem.NodeDataType;
                        if (nodeDataType == NodeDataType.Code)
                        {
                            SaveNode(currentStateSyncItem, currentResponseItem);
                            continue;
                        }

                        HandleTrieNode(currentStateSyncItem, currentResponseItem, ref invalidNodes);
                    }

                    Interlocked.Add(ref _data.ConsumedNodesCount, nonEmptyResponses);
                    StoreProgressInDb();

                    if (_logger.IsTrace) _logger.Trace($"After handling response (non-empty responses {nonEmptyResponses}) of {batch.RequestedNodes.Length} from ({_pendingItems.Description}) nodes");

                    /* magic formula is ratio of our desired batch size - 1024 to Geth max batch size 384 times some missing nodes ratio */
                    bool isEmptish = (decimal) nonEmptyResponses / Math.Max(requestLength, 1) < 384m / 1024m * 0.75m;
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
                    bool isBadQuality = nonEmptyResponses > 64 && (decimal) invalidNodes / Math.Max(requestLength, 1) > 0.50m;
                    if (isBadQuality) Interlocked.Increment(ref _data.BadQualityCount);

                    bool isEmpty = nonEmptyResponses == 0 && !isBadQuality;
                    if (isEmpty)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Peer sent no data in response to a request of length {batch.RequestedNodes.Length}");
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

                    _data.DisplayProgressReport(_pendingRequests.Keys, _logger);

                    long total = _handleWatch.ElapsedMilliseconds + _networkWatch.ElapsedMilliseconds;
                    if (total != 0)
                    {
                        // calculate averages
                        if (_logger.IsTrace) _logger.Trace($"Prepare batch {_networkWatch.ElapsedMilliseconds}ms ({(decimal) _networkWatch.ElapsedMilliseconds / total:P0}) - Handle {_handleWatch.ElapsedMilliseconds}ms ({(decimal) _handleWatch.ElapsedMilliseconds / total:P0})");
                    }

                    if (_handleWatch.ElapsedMilliseconds > 250)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Handle watch {_handleWatch.ElapsedMilliseconds}, DB reads {_data.DbChecks - _data.LastDbReads}, ratio {(decimal) _handleWatch.ElapsedMilliseconds / Math.Max(1, _data.DbChecks - _data.LastDbReads)}");
                    }

                    _data.LastDbReads = _data.DbChecks;
                    _data.AverageTimeInHandler = (_data.AverageTimeInHandler * (_data.ProcessedRequestsCount - 1) + _handleWatch.ElapsedMilliseconds) / _data.ProcessedRequestsCount;
                    Interlocked.Add(ref _data.HandledNodesCount, nonEmptyResponses);
                    return result;
                }
            }
            catch (Exception e)
            {
                _logger.Error("Error when handling state sync response", e);
                return SyncResponseHandlingResult.InternalError;
            }
            finally
            {
                _handleWatch.Stop();
                if (!_pendingRequests.TryRemove(batch, out _))
                {
                    if (_logger.IsDebug) _logger.Debug($"Cannot remove pending request {batch}");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing pending request {batch}");
                }
            }
        }

        private void StoreProgressInDb()
        {
            byte[] serializedData = _data.Serialize();
            lock (_stateDbLock)
            {
                lock (_codeDbLock)
                {
                    _codeDb[_fastSyncProgressKey.Bytes] = serializedData;
                }
            }
        }

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.State;

        private void HandleTrieNode(StateSyncItem currentStateSyncItem, byte[] currentResponseItem, ref int invalidNodes)
        {
            NodeDataType nodeDataType = currentStateSyncItem.NodeDataType;
            TrieNode trieNode = new TrieNode(NodeType.Unknown, currentResponseItem);
            trieNode.ResolveNode(null);
            switch (trieNode.NodeType)
            {
                case NodeType.Unknown:
                    invalidNodes++;
                    if (_logger.IsError) _logger.Error($"Node {currentStateSyncItem.Hash} resolved to {nameof(NodeType.Unknown)}");
                    break;
                case NodeType.Branch:
                    DependentItem dependentBranch = new DependentItem(currentStateSyncItem, currentResponseItem, 0);

                    // children may have the same hashes (e.g. a set of accounts with the same code at different addresses)
                    HashSet<Keccak> alreadyProcessedChildHashes = new HashSet<Keccak>();
                    for (int childIndex = 15; childIndex >= 0; childIndex--)
                    {
                        Keccak childHash = trieNode.GetChildHash(childIndex);
                        if (alreadyProcessedChildHashes.Contains(childHash))
                        {
                            continue;
                        }

                        alreadyProcessedChildHashes.Add(childHash);

                        if (childHash != null)
                        {
                            AddNodeResult addChildResult = AddNodeToPending(new StateSyncItem(childHash, nodeDataType, currentStateSyncItem.Level + 1, CalculateRightness(trieNode.NodeType, currentStateSyncItem, childIndex)) {BranchChildIndex = (short) childIndex, ParentBranchChildIndex = currentStateSyncItem.BranchChildIndex}, dependentBranch, "branch child");
                            if (addChildResult != AddNodeResult.AlreadySaved)
                            {
                                dependentBranch.Counter++;
                            }
                            else
                            {
                                _syncProgress.ReportSynced(currentStateSyncItem.Level + 1, currentStateSyncItem.BranchChildIndex, childIndex, currentStateSyncItem.NodeDataType, NodeProgressState.AlreadySaved);
                            }
                        }
                        else
                        {
                            _syncProgress.ReportSynced(currentStateSyncItem.Level + 1, currentStateSyncItem.BranchChildIndex, childIndex, currentStateSyncItem.NodeDataType, NodeProgressState.Empty);
                        }
                    }

                    if (dependentBranch.Counter == 0)
                    {
                        SaveNode(currentStateSyncItem, currentResponseItem);
                    }

                    break;
                case NodeType.Extension:
                    Keccak next = trieNode[0].Keccak;
                    if (next != null)
                    {
                        DependentItem dependentItem = new DependentItem(currentStateSyncItem, currentResponseItem, 1);
                        AddNodeResult addResult = AddNodeToPending(new StateSyncItem(next, nodeDataType, currentStateSyncItem.Level + trieNode.Path.Length, CalculateRightness(trieNode.NodeType, currentStateSyncItem, 0)) {ParentBranchChildIndex = currentStateSyncItem.BranchChildIndex}, dependentItem, "extension child");
                        if (addResult == AddNodeResult.AlreadySaved)
                        {
                            SaveNode(currentStateSyncItem, currentResponseItem);
                        }
                    }
                    else
                    {
                        /* this happens when we have a short RLP format of the node
                                     * that would not be stored as Keccak but full RLP*/
                        SaveNode(currentStateSyncItem, currentResponseItem);
                    }

                    break;
                case NodeType.Leaf:
                    if (nodeDataType == NodeDataType.State)
                    {
                        _pendingItems.MaxStateLevel = 64;
                        DependentItem dependentItem = new DependentItem(currentStateSyncItem, currentResponseItem, 0, true);
                        (Keccak codeHash, Keccak storageRoot) = AccountDecoder.DecodeHashesOnly(new RlpStream(trieNode.Value));
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
                            }
                            else
                            {
                                AddNodeResult addCodeResult = AddNodeToPending(new StateSyncItem(codeHash, NodeDataType.Code, 0, currentStateSyncItem.Rightness), dependentItem, "code");
                                if (addCodeResult != AddNodeResult.AlreadySaved) dependentItem.Counter++;
                            }
                        }

                        if (storageRoot != Keccak.EmptyTreeHash)
                        {
                            AddNodeResult addStorageNodeResult = AddNodeToPending(new StateSyncItem(storageRoot, NodeDataType.Storage, 0, currentStateSyncItem.Rightness), dependentItem, "storage");
                            if (addStorageNodeResult != AddNodeResult.AlreadySaved) dependentItem.Counter++;
                        }

                        if (dependentItem.Counter == 0)
                        {
                            Interlocked.Increment(ref _data.SavedAccounts);
                            SaveNode(currentStateSyncItem, currentResponseItem);
                        }
                    }
                    else
                    {
                        _pendingItems.MaxStorageLevel = 64;
                        SaveNode(currentStateSyncItem, currentResponseItem);
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
                return currentStateSyncItem.Rightness + (uint) Math.Pow(16, Math.Max(0, 7 - currentStateSyncItem.Level)) * (uint) childIndex;
            }

            if (nodeType == NodeType.Extension)
            {
                return currentStateSyncItem.Rightness + (uint) Math.Pow(16, Math.Max(0, 7 - currentStateSyncItem.Level)) * 16 - 1;
            }

            throw new InvalidOperationException($"Not designed for {nodeType}");
        }

        private void AddDependency(Keccak dependency, DependentItem dependentItem)
        {
            lock (_dependencies)
            {
                if (!_dependencies.ContainsKey(dependency))
                {
                    _dependencies[dependency] = new HashSet<DependentItem>(DependentItemComparer.Instance);
                }

                _dependencies[dependency].Add(dependentItem);
                _tempDb[dependentItem.SyncItem.Hash.Bytes] = dependentItem.Value;
            }
        }

        public override async Task<StateSyncBatch> PrepareRequest()
        {
            bool stateSyncMode = (_syncModeSelector.Current & SyncMode.StateNodes) == SyncMode.StateNodes;
            bool tooManyRequests = _pendingRequests.Count >= _requestLimit;
            if (!stateSyncMode || tooManyRequests)
            {
                return null;
            }

            try
            {
                if (_rootNode == Keccak.EmptyTreeHash)
                {
                    if (_logger.IsDebug) _logger.Debug("Falling asleep - root is empty tree");
                    FallAsleep();
                    return EmptyBatch;
                }

                if (_hintsToResetRoot >= 32)
                {
                    if (_logger.IsDebug) _logger.Debug("Falling asleep - many missing responses");
                    FallAsleep();
                    return EmptyBatch;
                }

                lock (_stateDbLock)
                {
                    // if finished downloading
                    if (_stateDb.KeyExists(_rootNode))
                    {
                        VerifyPostSyncCleanUp();
                        FallAsleep();
                        return EmptyBatch;
                    }
                }

                List<StateSyncItem> requestHashes = _pendingItems.TakeBatch(MaxRequestSize);
                // foreach (StateSyncItem stateSyncItem in requestHashes)
                // {
                //     if (_tempDb.Get(stateSyncItem.Hash) != null)
                //     {
                //         Interlocked.Increment(ref _beamSyncedUsable);
                //         _logger.Error($"Could have used the beam synced item {_beamSyncedUsable}");
                //     }
                // }
                
                LogRequestInfo(requestHashes);

                if (requestHashes.Count > 0)
                {
                    StateSyncBatch result = new StateSyncBatch();
                    result.RequestedNodes = requestHashes.ToArray();
                    Interlocked.Add(ref _data.RequestedNodesCount, result.RequestedNodes.Length);
                    Interlocked.Exchange(ref _data.SecondsInSync, _currentSyncStartSecondsInSync + (long) (DateTime.UtcNow - _currentSyncStart).TotalSeconds);

                    if (_logger.IsTrace) _logger.Trace($"After preparing a request of {requestHashes.Count} from ({_pendingItems.Description}) nodes | {_dependencies.Count}");
                    if (_logger.IsTrace) _logger.Trace($"Adding pending request {result}");
                    _pendingRequests.TryAdd(result, null);

                    Interlocked.Increment(ref Metrics.StateSyncRequests);
                    return await Task.FromResult(result);
                }

                if (requestHashes.Count == 0)
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
        
        public void ResetStateRoot(long blockNumber, Keccak stateRoot)
        {
            Interlocked.Exchange(ref _hintsToResetRoot, 0);
            
            if (_logger.IsInfo) _logger.Info($"Setting state sync state root to {blockNumber} {stateRoot}");
            _currentSyncStart = DateTime.UtcNow;
            _currentSyncStartSecondsInSync = _data.SecondsInSync;

            _data.LastReportTime = (DateTime.UtcNow, DateTime.UtcNow);
            _data.LastSavedNodesCount = _data.SavedNodesCount;
            _data.LastRequestedNodesCount = _data.RequestedNodesCount;
            if (_rootNode != stateRoot)
            {
                _syncProgress = new StateSyncProgress(blockNumber, _logger);
                _rootNode = stateRoot;
                lock (_dependencies) _dependencies.Clear();
                lock (_codesSameAsNodes) _codesSameAsNodes.Clear();

                _pendingItems.Clear();

                if (_logger.IsDebug) _logger.Debug($"Clearing node stacks ({_pendingItems.Description})");
            }
            else
            {
                foreach ((StateSyncBatch pendingRequest, _) in _pendingRequests)
                {
                    // re-add the pending request
                    for (int i = 0; i < pendingRequest.RequestedNodes.Length; i++)
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
                    StateSyncItem potentialRoot = _pendingItems.PeekState();
                    if (potentialRoot?.Hash == _rootNode)
                    {
                        hasOnlyRootNode = true;
                    }
                }

                if (!hasOnlyRootNode)
                {
                    AddNodeToPending(new StateSyncItem(_rootNode, NodeDataType.State, 0, 0), null, "initial");
                }
            }
        }

        private enum AddNodeResult
        {
            AlreadySaved,
            AlreadyRequested,
            Added
        }

        public void Dispose()
        {
            _syncModeSelector.Changed -= SyncModeSelectorOnChanged;
        }
    }
}
