/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization.FastSync
{
    public class NodeDataFeed : INodeDataFeed
    {
        private static AccountDecoder _accountDecoder = new AccountDecoder();
        private StateSyncBatch _emptyBatch = new StateSyncBatch {RequestedNodes = new StateSyncItem[0]};

        private Keccak _fastSyncProgressKey = Keccak.Zero;
        private (DateTime small, DateTime full) _lastReportTime = (DateTime.MinValue, DateTime.MinValue);
        private long _lastSavedNodesCount;
        private long _consumedNodesCount;
        private long _savedStorageCount;
        private long _savedStateCount;
        private long _savedNodesCount;
        private long _savedAccounts;
        private long _savedCode;
        private long _requestedNodesCount;
        private long _secondsInSync;
        private long _dbChecks;
        private long _checkWasCached;
        private long _checkWasInDependencies;
        private long _stateWasThere;
        private long _stateWasNotThere;
        private long _emptishCount;
        private long _invalidFormatCount;
        private long _okCount;
        private long _badQualityCount;
        private long _notAssignedCount;
        private long _dataSize;

        private DateTime _lastReview = DateTime.UtcNow;
        private DateTime _currentSyncStart;
        private long _currentSyncStartSecondsInSync;
        public long TotalRequestsCount => _emptishCount + _invalidFormatCount + _badQualityCount + _okCount + _notAssignedCount;
        public long ProcessedRequestsCount => _emptishCount + _badQualityCount + _okCount;

        private byte _maxStateLevel; // for priority calculation (prefer depth)
        private uint _maxRightness; // for priority calculation (prefer left)

        private object _stateDbLock = new object();
        private object _codeDbLock = new object();
        private static object _nullObject = new object();

        private Stopwatch _networkWatch = new Stopwatch();
        private Stopwatch _handleWatch = new Stopwatch();

        private Keccak _rootNode;

        private ISnapshotableDb _codeDb;
        private ILogger _logger;
        private ISnapshotableDb _stateDb;

        private ConcurrentStack<StateSyncItem>[] _nodes = new ConcurrentStack<StateSyncItem>[3];
        private ConcurrentStack<StateSyncItem> Stream0 => _nodes[0];
        private ConcurrentStack<StateSyncItem> Stream1 => _nodes[1];
        private ConcurrentStack<StateSyncItem> Stream2 => _nodes[2];

        public int TotalNodesPending => Stream0.Count + Stream1.Count + Stream2.Count;
        private ConcurrentDictionary<StateSyncBatch, object> _pendingRequests = new ConcurrentDictionary<StateSyncBatch, object>();
        private Dictionary<Keccak, HashSet<DependentItem>> _dependencies = new Dictionary<Keccak, HashSet<DependentItem>>();
        private LruCache<Keccak, object> _alreadySaved = new LruCache<Keccak, object>(1024 * 64);

        public NodeDataFeed(ISnapshotableDb codeDb, ISnapshotableDb stateDb, ILogManager logManager)
        {
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            byte[] progress = _codeDb.Get(_fastSyncProgressKey);
            if (progress != null)
            {
                RlpStream rlpStream = new RlpStream(progress);
                rlpStream.ReadSequenceLength();
                _consumedNodesCount = rlpStream.DecodeLong();
                _savedStorageCount = rlpStream.DecodeLong();
                _savedStateCount = rlpStream.DecodeLong();
                _savedNodesCount = rlpStream.DecodeLong();
                _savedAccounts = rlpStream.DecodeLong();
                _savedCode = rlpStream.DecodeLong();
                _requestedNodesCount = rlpStream.DecodeLong();
                _dbChecks = rlpStream.DecodeLong();
                _stateWasThere = rlpStream.DecodeLong();
                _stateWasNotThere = rlpStream.DecodeLong();
                _dataSize = rlpStream.DecodeLong();

                if (rlpStream.Position != rlpStream.Length)
                {
                    _secondsInSync = rlpStream.DecodeLong();
                }
            }
        }

        private AddNodeResult AddNode(StateSyncItem syncItem, DependentItem dependentItem, string reason, bool missing = false)
        {
            if (!missing)
            {
                if (syncItem.Level <= 2)
                {
                    _syncProgress.ReportSynced(syncItem.Level, syncItem.ParentBranchChildIndex, syncItem.BranchChildIndex, syncItem.NodeDataType, NodeProgressState.Requested);
                }

                if (_alreadySaved.Get(syncItem.Hash) != null)
                {
                    Interlocked.Increment(ref _checkWasCached);
                    if (_logger.IsTrace) _logger.Trace($"Node already in the DB - skipping {syncItem.Hash}");
                    return AddNodeResult.AlreadySaved;
                }

                object lockToTake = syncItem.NodeDataType == NodeDataType.Code ? _codeDbLock : _stateDbLock;
                lock (lockToTake)
                {
                    ISnapshotableDb dbToCheck = syncItem.NodeDataType == NodeDataType.Code ? _codeDb : _stateDb;
                    Interlocked.Increment(ref _dbChecks);
                    bool keyExists = dbToCheck.KeyExists(syncItem.Hash);
                    if (keyExists)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Node already in the DB - skipping {syncItem.Hash}");
                        _alreadySaved.Set(syncItem.Hash, _nullObject);
                        Interlocked.Increment(ref _stateWasThere);
                        return AddNodeResult.AlreadySaved;
                    }

                    Interlocked.Increment(ref _stateWasNotThere);
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
                    Interlocked.Increment(ref _checkWasInDependencies);
                    if (_logger.IsTrace) _logger.Trace($"Node already requested - skipping {syncItem.Hash}");
                    return AddNodeResult.AlreadyRequested;
                }
            }

            PushToSelectedStream(syncItem);
            if (_logger.IsTrace) _logger.Trace($"Added a node {syncItem.Hash} - {reason}");
            return AddNodeResult.Added;
        }

        private void PushToSelectedStream(StateSyncItem stateSyncItem)
        {
            ConcurrentStack<StateSyncItem> selectedStream;
            double priority = CalculatePriority(stateSyncItem.NodeDataType, stateSyncItem.Level, stateSyncItem.Rightness);
            
            if (priority <= 0.5f)
            {
                selectedStream = Stream0;
            }
            else if (priority <= 1.5f)
            {
                selectedStream = Stream1;
            }
            else
            {
                selectedStream = Stream2;
            }

            selectedStream.Push(stateSyncItem);
        }

        private void RunChainReaction(Keccak hash)
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
                if (dependentItem.IsAccount) Interlocked.Increment(ref _savedAccounts);
                SaveNode(dependentItem.SyncItem, dependentItem.Value);
            }
        }

        private void SaveNode(StateSyncItem syncItem, byte[] data)
        {
            if (_logger.IsTrace) _logger.Trace($"SAVE {new string('+', syncItem.Level * 2)}{syncItem.NodeDataType.ToString().ToUpperInvariant()} {syncItem.Hash}");
            Interlocked.Increment(ref _savedNodesCount);
            switch (syncItem.NodeDataType)
            {
                case NodeDataType.State:
                {
                    Interlocked.Increment(ref _savedStateCount);
                    lock (_stateDbLock)
                    {
                        Interlocked.Add(ref _dataSize, data.Length);
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
                                Interlocked.Add(ref _dataSize, data.Length);
                                _codeDb.Set(syncItem.Hash, data);
                            }

                            _codesSameAsNodes.Remove(syncItem.Hash);
                        }
                    }

                    Interlocked.Increment(ref _savedStorageCount);
                    lock (_stateDbLock)
                    {
                        Interlocked.Add(ref _dataSize, data.Length);
                        _stateDb.Set(syncItem.Hash, data);
                    }

                    break;
                }
                case NodeDataType.Code:
                {
                    Interlocked.Increment(ref _savedCode);
                    lock (_codeDbLock)
                    {
                        Interlocked.Add(ref _dataSize, data.Length);
                        _codeDb.Set(syncItem.Hash, data);
                    }

                    break;
                }
            }

            if (syncItem.IsRoot)
            {
                if (_logger.IsInfo) _logger.Info($"Saving root {syncItem.Hash} of {_syncProgress.CurrentSyncBlock}");

                lock (_dependencies)
                {
                    if (_dependencies.Count != 0)
                    {
                        if (_logger.IsError) _logger.Error($"POSSIBLE FAST SYNC CORRUPTION | Dependencies hanging after the root node saved - count: {_dependencies.Count}, first: {_dependencies.Keys.First()}");
                    }
                }

                if (TotalNodesPending != 0)
                {
                    if (_logger.IsError) _logger.Error($"POSSIBLE FAST SYNC CORRUPTION | Nodes left after the root node saved - count: {TotalNodesPending}");
                }
            }

            _syncProgress.ReportSynced(syncItem.Level, syncItem.ParentBranchChildIndex, syncItem.BranchChildIndex, syncItem.NodeDataType, NodeProgressState.Saved);
            RunChainReaction(syncItem.Hash);
        }

        private float CalculatePriority(NodeDataType nodeDataType, byte level, uint rightness)
        {   
            if (nodeDataType != NodeDataType.State)
            {
                return 0;
            }

            if (level > _maxStateLevel)
            {
                _maxStateLevel = level;
            }
            
            if (rightness > _maxRightness)
            {
                _maxRightness = rightness;
            }

            float priority = 1.5f - (float) level / _maxStateLevel + (float) rightness / _maxRightness - (float)_syncProgress.LastProgress / 2;
            
            // int stream = priority <= 0.5f ? 0 : priority <= 1.5f ? 1 : 2;
            // _logger.Error($"{stream} | {priority} = 1.0f - (float) {level} / {_maxStateLevel} + (float){rightness} / {_maxRightness}");
            return priority;
        }

        private HashSet<Keccak> _codesSameAsNodes = new HashSet<Keccak>();

        public (NodeDataHandlerResult Result, int NodesConsumed) HandleResponse(StateSyncBatch batch)
        {   
            int requestLength = batch.RequestedNodes?.Length ?? 0;
            int responseLength = batch.Responses?.Length ?? 0;

            void AddAgainAllItems()
            {
                for (int i = 0; i < requestLength; i++)
                {
                    AddNode(batch.RequestedNodes[i], null, "missing", true);
                }
            }

            try
            {
                lock (_handleWatch)
                {
                    if (DateTime.UtcNow - _lastReview > TimeSpan.FromSeconds(60))
                    {
                        _lastReview = DateTime.UtcNow;
                        RunReview();
                    }
                    
                    _handleWatch.Restart();

                    bool requestWasMade = batch.AssignedPeer?.Current != null;
                    NodeDataHandlerResult result;
                    if (!requestWasMade)
                    {
                        AddAgainAllItems();
                        if (_logger.IsTrace) _logger.Trace($"Batch was not assigned to any peer.");
                        Interlocked.Increment(ref _notAssignedCount);
                        result = NodeDataHandlerResult.NotAssigned;
                        return (result, 0);
                    }

                    bool isMissingRequestData = batch.RequestedNodes == null;
                    bool isMissingResponseData = batch.Responses == null;
                    bool hasValidFormat = !isMissingRequestData && !isMissingResponseData;

                    if (!hasValidFormat)
                    {
                        AddAgainAllItems();
                        if (_logger.IsDebug) _logger.Debug($"Batch response had invalid format");
                        Interlocked.Increment(ref _invalidFormatCount);
                        result = NodeDataHandlerResult.InvalidFormat;
                        return (result, 0);
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
                            AddNode(currentStateSyncItem, null, "missing", true);
                            continue;
                        }

                        /* if the peer does not have details of this particular node */
                        byte[] currentResponseItem = batch.Responses[i];
                        if (currentResponseItem == null)
                        {
                            AddNode(batch.RequestedNodes[i], null, "missing", true);
                            continue;
                        }

                        /* node sent data that is not consistent with its hash - it happens surprisingly often */
                        if (Keccak.Compute(currentResponseItem) != currentStateSyncItem.Hash)
                        {
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

                    Interlocked.Add(ref _consumedNodesCount, nonEmptyResponses);
                    lock (_stateDbLock)
                    {
                        Rlp rlp = Rlp.Encode(
                            Rlp.Encode(_consumedNodesCount),
                            Rlp.Encode(_savedStorageCount),
                            Rlp.Encode(_savedStateCount),
                            Rlp.Encode(_savedNodesCount),
                            Rlp.Encode(_savedAccounts),
                            Rlp.Encode(_savedCode),
                            Rlp.Encode(_requestedNodesCount),
                            Rlp.Encode(_dbChecks),
                            Rlp.Encode(_stateWasThere),
                            Rlp.Encode(_stateWasNotThere),
                            Rlp.Encode(_dataSize),
                            Rlp.Encode(_secondsInSync));
                        lock (_codeDbLock)
                        {
                            _codeDb[_fastSyncProgressKey.Bytes] = rlp.Bytes;
                            _codeDb.Commit();
                            _stateDb.Commit();
                        }
                    }

                    if (_logger.IsTrace) _logger.Trace($"After handling response (non-empty responses {nonEmptyResponses}) of {batch.RequestedNodes.Length} from ({Stream0.Count}|{Stream1.Count}|{Stream2.Count}) nodes");

                    /* magic formula is ratio of our desired batch size - 1024 to Geth max batch size 384 times some missing nodes ratio */
                    bool isEmptish = (decimal) nonEmptyResponses / requestLength < 384m / 1024m * 0.75m;
                    if (isEmptish) Interlocked.Increment(ref _emptishCount);

                    /* here we are very forgiving for Geth nodes that send bad data fast */
                    bool isBadQuality = nonEmptyResponses > 64 && (decimal) invalidNodes / requestLength > 0.50m;
                    if (isBadQuality) Interlocked.Increment(ref _badQualityCount);

                    bool isEmpty = nonEmptyResponses == 0 && !isBadQuality;
                    if (isEmpty)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Peer sent no data in response to a request of length {batch.RequestedNodes.Length}");
                        result = NodeDataHandlerResult.NoData;
                        return (result, 0);
                    }

                    if (!isEmptish && !isBadQuality)
                    {
                        Interlocked.Increment(ref _okCount);
                    }

                    result = isEmptish
                        ? NodeDataHandlerResult.Emptish
                        : isBadQuality
                            ? NodeDataHandlerResult.BadQuality
                            : NodeDataHandlerResult.OK;

                    if (DateTime.UtcNow - _lastReportTime.small > TimeSpan.FromSeconds(1))
                    {
                        decimal savedNodesPerSecond = 1000m * (_savedNodesCount - _lastSavedNodesCount) / (decimal) (DateTime.UtcNow - _lastReportTime.small).TotalMilliseconds;
                        _lastSavedNodesCount = _savedNodesCount;
                        if (_logger.IsInfo) _logger.Info($"Time {TimeSpan.FromSeconds(_secondsInSync):dd\\.hh\\:mm\\:ss} | State {(decimal) _dataSize / 1000 / 1000,6:F2}MB | SNPS: {savedNodesPerSecond,6:F0} | P: {_pendingRequests.Count} | accounts {_savedAccounts} | enqueued nodes {Stream0.Count:D6} {Stream1.Count:D6} {Stream2.Count:D6} | AVTIH {_averageTimeInHandler:f2}");
                        if (DateTime.UtcNow - _lastReportTime.full > TimeSpan.FromSeconds(10))
                        {
                            long allChecks = _checkWasInDependencies + _checkWasCached + _stateWasThere + _stateWasNotThere;
                            if (_logger.IsInfo) _logger.Info($"OK {(decimal) _okCount / TotalRequestsCount:p2} | Emptish: {(decimal) _emptishCount / TotalRequestsCount:p2} | BadQuality: {(decimal) _badQualityCount / TotalRequestsCount:p2} | InvalidFormat: {(decimal) _invalidFormatCount / TotalRequestsCount:p2} | NotAssigned {(decimal) _notAssignedCount / TotalRequestsCount:p2}");
                            if (_logger.IsInfo) _logger.Info($"Consumed {(decimal) _consumedNodesCount / _requestedNodesCount:p2} | Saved {(decimal) _savedNodesCount / _requestedNodesCount:p2} | DB Reads : {(decimal) _dbChecks / _requestedNodesCount:p2} | DB checks {_stateWasThere}/{_stateWasNotThere + _stateWasThere} | Cached {(decimal) _checkWasCached / allChecks:P2} + {(decimal) _checkWasInDependencies / allChecks:P2}");
                            _lastReportTime.full = DateTime.UtcNow;
                        }

                        _lastReportTime.small = DateTime.UtcNow;
                    }

                    long total = _handleWatch.ElapsedMilliseconds + _networkWatch.ElapsedMilliseconds;
                    if (total != 0)
                    {
                        // calculate averages
                        if (_logger.IsTrace) _logger.Trace($"Prepare batch {_networkWatch.ElapsedMilliseconds}ms ({(decimal) _networkWatch.ElapsedMilliseconds / total:P0}) - Handle {_handleWatch.ElapsedMilliseconds}ms ({(decimal) _handleWatch.ElapsedMilliseconds / total:P0})");
                    }


                    if (_handleWatch.ElapsedMilliseconds > 250)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Handle watch {_handleWatch.ElapsedMilliseconds}, DB reads {_dbChecks - _lastDbReads}, ratio {(decimal) _handleWatch.ElapsedMilliseconds / Math.Max(1, _dbChecks - _lastDbReads)}");
                    }

                    _lastDbReads = _dbChecks;
                    _averageTimeInHandler = (_averageTimeInHandler * (ProcessedRequestsCount - 1) + _handleWatch.ElapsedMilliseconds) / ProcessedRequestsCount;
                    return (result, nonEmptyResponses);
                }
            }
            finally
            {
                _handleWatch.Stop();
                if (!_pendingRequests.TryRemove(batch, out _))
                {
                    _logger.Error("Cannot remove pending request");
                }
            }
        }

        private void HandleTrieNode(StateSyncItem currentStateSyncItem, byte[] currentResponseItem, ref int invalidNodes)
        {
            NodeDataType nodeDataType = currentStateSyncItem.NodeDataType;
            TrieNode trieNode = new TrieNode(NodeType.Unknown, new Rlp(currentResponseItem));
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
                    for (int childIndex = 0; childIndex < 16; childIndex++)
                    {
//                        if (currentStateSyncItem.Level <= 1)
//                        {
//                            _logger.Warn($"Testing {currentStateSyncItem.Level} {currentStateSyncItem.BranchChildIndex}.{childIndex}");
//                        }

                        Keccak childHash = trieNode.GetChildHash(childIndex);
                        if (alreadyProcessedChildHashes.Contains(childHash))
                        {
                            continue;
                        }

                        alreadyProcessedChildHashes.Add(childHash);

                        if (childHash != null)
                        {
                            AddNodeResult addChildResult = AddNode(new StateSyncItem(childHash, nodeDataType, currentStateSyncItem.Level + 1, currentStateSyncItem.Rightness + (uint)Math.Pow(16, Math.Max(0, 7 - currentStateSyncItem.Level)) * (uint)childIndex) {BranchChildIndex = (short) childIndex, ParentBranchChildIndex = currentStateSyncItem.BranchChildIndex}, dependentBranch, "branch child");
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
                        AddNodeResult addResult = AddNode(new StateSyncItem(next, nodeDataType, currentStateSyncItem.Level + trieNode.Path.Length, currentStateSyncItem.Rightness) {ParentBranchChildIndex = currentStateSyncItem.BranchChildIndex}, dependentItem, "extension child");
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
                        DependentItem dependentItem = new DependentItem(currentStateSyncItem, currentResponseItem, 0, true);
                        Account account = _accountDecoder.Decode(new RlpStream(trieNode.Value));
                        if (account.CodeHash != Keccak.OfAnEmptyString)
                        {
                            // prepare a branch without the code DB
                            // this only protects against being same as storage root?
                            if (account.CodeHash == account.StorageRoot)
                            {
                                lock (_codesSameAsNodes)
                                {
                                    _codesSameAsNodes.Add(account.CodeHash);
                                }
                            }
                            else
                            {
                                AddNodeResult addCodeResult = AddNode(new StateSyncItem(account.CodeHash, NodeDataType.Code, 0, currentStateSyncItem.Rightness), dependentItem, "code");
                                if (addCodeResult != AddNodeResult.AlreadySaved) dependentItem.Counter++;
                            }
                        }

                        if (account.StorageRoot != Keccak.EmptyTreeHash)
                        {
                            AddNodeResult addStorageNodeResult = AddNode(new StateSyncItem(account.StorageRoot, NodeDataType.Storage, 0, currentStateSyncItem.Rightness), dependentItem, "storage");
                            if (addStorageNodeResult != AddNodeResult.AlreadySaved) dependentItem.Counter++;
                        }

                        if (dependentItem.Counter == 0)
                        {
                            Interlocked.Increment(ref _savedAccounts);
                            SaveNode(currentStateSyncItem, currentResponseItem);
                        }
                    }
                    else
                    {
                        SaveNode(currentStateSyncItem, currentResponseItem);
                    }

                    break;
                default:
                    if (_logger.IsError) _logger.Error($"Unknown value {currentStateSyncItem.NodeDataType} of {nameof(NodeDataType)} at {currentStateSyncItem.Hash}");
                    invalidNodes++;
                    break;
            }
        }

        private long _lastDbReads;
        private decimal _averageTimeInHandler;

        private class DependentItemComparer : IEqualityComparer<DependentItem>
        {
            public bool Equals(DependentItem x, DependentItem y)
            {
                return x?.SyncItem.Hash == y?.SyncItem.Hash;
            }

            public int GetHashCode(DependentItem obj)
            {
                return obj?.SyncItem.Hash.GetHashCode() ?? 0;
            }
        }

        private void AddDependency(Keccak dependency, DependentItem dependentItem)
        {
            lock (_dependencies)
            {
                if (!_dependencies.ContainsKey(dependency))
                {
                    _dependencies[dependency] = new HashSet<DependentItem>(new DependentItemComparer());
                }

                _dependencies[dependency].Add(dependentItem);
            }
        }

        private bool TryTake(out StateSyncItem node)
        {
            if (!Stream0.TryPop(out node))
            {
                if (!Stream1.TryPop(out node))
                {
                    return Stream2.TryPop(out node);
                }
            }

            return true;
        }

        private void RunReview()
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            
            string reviewMessage = "Node sync queues review:" + Environment.NewLine;
            reviewMessage += $"  before {Stream0.Count:D6} {Stream1.Count:D6} {Stream2.Count:D6}" + Environment.NewLine;
            
            List<StateSyncItem> temp = new List<StateSyncItem>();
            while (Stream2.TryPop(out StateSyncItem poppedSyncItem))
            {
                temp.Add(poppedSyncItem);
            }

            foreach (StateSyncItem syncItem in temp)
            {
                PushToSelectedStream(syncItem);
            }
            
            reviewMessage += $"  after {Stream0.Count:D6} {Stream1.Count:D6} {Stream2.Count:D6}" + Environment.NewLine;
            
            stopwatch.Stop();
            reviewMessage += $"  time spent in review: {stopwatch.ElapsedMilliseconds}ms";
            if(_logger.IsInfo) _logger.Info(reviewMessage);
        }
        
        public StateSyncBatch PrepareRequest(int length)
        {
            if (_rootNode == Keccak.EmptyTreeHash)
            {
                return _emptyBatch;
            }

            StateSyncBatch batch = new StateSyncBatch();
            if (_logger.IsTrace) _logger.Trace($"Preparing a request of length {length} from ({Stream0.Count}|{Stream1.Count}|{Stream2.Count}) nodes");

            List<StateSyncItem> requestHashes = new List<StateSyncItem>();
            for (int i = 0; i < length; i++)
            {
                if (TryTake(out StateSyncItem requestItem))
                {
                    if (_logger.IsTrace) _logger.Trace($"Requesting {requestItem.Hash}");
                    requestHashes.Add(requestItem);
                }
                else
                {
                    break;
                }
            }

            batch.RequestedNodes = requestHashes.ToArray();

            StateSyncBatch result = batch.RequestedNodes == null ? _emptyBatch : batch;
            Interlocked.Add(ref _requestedNodesCount, result.RequestedNodes.Length);
            Interlocked.Exchange(ref _secondsInSync, _currentSyncStartSecondsInSync + (long)(DateTime.UtcNow - _currentSyncStart).TotalSeconds);

            if (_logger.IsTrace) _logger.Trace($"After preparing a request of {length} from ({Stream0.Count}|{Stream1.Count}|{Stream2.Count}) nodes");

            if (result.RequestedNodes.Length > 0)
            {
                _pendingRequests.TryAdd(result, _nullObject);
            }

            return result;
        }

        private NodeSyncProgress _syncProgress;

        public void SetNewStateRoot(long number, Keccak stateRoot)
        {
            _currentSyncStart = DateTime.UtcNow;
            _currentSyncStartSecondsInSync = _secondsInSync;
            
            _lastReportTime = (DateTime.UtcNow, DateTime.UtcNow);
            _lastSavedNodesCount = _savedNodesCount;
            if (_rootNode != stateRoot)
            {
                _syncProgress = new NodeSyncProgress(number, _logger);
                _rootNode = stateRoot;
                lock (_dependencies) _dependencies.Clear();
                lock (_codesSameAsNodes) _codesSameAsNodes.Clear();

                _nodes[0]?.Clear();
                _nodes[1]?.Clear();
                _nodes[2]?.Clear();

                if (_logger.IsDebug) _logger.Debug($"Clearing node stacks ({Stream0?.Count ?? 0}|{Stream1?.Count ?? 0}|{Stream2?.Count ?? 0})");
            }
            else
            {
                foreach ((StateSyncBatch pendingRequest, _) in _pendingRequests)
                {
                    // re-add the pending request
                    for (int i = 0; i < pendingRequest.RequestedNodes.Length; i++)
                    {
                        AddNode(pendingRequest.RequestedNodes[i], null, "pending request", true);
                    }
                }
            }

            _pendingRequests.Clear();

            if (_nodes[0] == null)
            {
                _nodes[0] = new ConcurrentStack<StateSyncItem>();
                _nodes[1] = new ConcurrentStack<StateSyncItem>();
                _nodes[2] = new ConcurrentStack<StateSyncItem>();
            }

            bool hasOnlyRootNode = false;
            if (TotalNodesPending == 1)
            {
                Stream0.TryPeek(out StateSyncItem node0);
                Stream1.TryPeek(out StateSyncItem node1);
                Stream2.TryPeek(out StateSyncItem node2);
                if ((node0 ?? node1 ?? node2).Hash == stateRoot)
                {
                    hasOnlyRootNode = true;
                }
            }

            if (!hasOnlyRootNode && _rootNode != Keccak.EmptyTreeHash)
            {
                AddNode(new StateSyncItem(stateRoot, NodeDataType.State, 0, 0), null, "initial");
            }
        }

        public bool IsFullySynced(Keccak stateRoot)
        {
            if (stateRoot == Keccak.EmptyTreeHash)
            {
                return true;
            }

            lock (_stateDbLock)
            {
                return _stateDb.Get(stateRoot) != null;
            }
        }

        private enum AddNodeResult
        {
            AlreadySaved,
            AlreadyRequested,
            Added
        }

        [DebuggerDisplay("{SyncItem.Hash} {Counter}")]
        private class DependentItem
        {
            public StateSyncItem SyncItem { get; }
            public byte[] Value { get; }
            public int Counter { get; set; }

            public bool IsAccount { get; }

            public DependentItem(StateSyncItem syncItem, byte[] value, int counter, bool isAccount = false)
            {
                SyncItem = syncItem;
                Value = value;
                Counter = counter;
                IsAccount = isAccount;
            }
        }
    }
}