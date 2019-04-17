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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization
{
    public class NodeDataDownloader : INodeDataDownloader
    {
        private static AccountDecoder accountDecoder = new AccountDecoder();

        private const int MaxRequestSize = 384;
        private int _lastDownloadedNodesCount;
        private int _consumedNodesCount;
        private int _savedStorageCount;
        private int _savedStateCount;
        private int _savedNodesCount;
        private int _savedAccounts;
        private int _savedCode;
        private int _requestedNodesCount;
        private int _dbChecks;
        private int _checkWasCached;
        private int _checkWasInDeps;
        private int _stateWasThere;
        private int _stateWasNotThere;

        private readonly ILogger _logger;
        private readonly ISnapshotableDb _stateDb;
        private readonly IDb _codeDb;
        private INodeDataRequestExecutor _executor;

        private object _responseLock = new object();

        private Dictionary<Keccak, List<DependentItem>> _dependencies = new Dictionary<Keccak, List<DependentItem>>();

        public NodeDataDownloader(IDb codeDb, ISnapshotableDb stateDb, ILogManager logManager)
        {
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public NodeDataDownloader(IDb codeDb, ISnapshotableDb stateDb, INodeDataRequestExecutor executor, ILogManager logManager)
            : this(codeDb, stateDb, logManager)
        {
            SetExecutor(executor);
        }

        private async Task KeepSyncing(CancellationToken token)
        {
            StateSyncBatch[] dataBatches;
            do
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                dataBatches = PrepareRequests();
                for (int i = 0; i < dataBatches.Length; i++)
                {
                    if (_logger.IsDebug) _logger.Debug($"Sending requests for {dataBatches[i].StateSyncs.Length} nodes");
                    _requestedNodesCount += dataBatches[i].StateSyncs.Length;
                    _networkWatch.Restart();
                    await _executor.ExecuteRequest(token, dataBatches[i]).ContinueWith(t =>
                    {
                        if (t.IsCompleted)
                        {
                            _networkWatch.Stop();
                            _logger.Info($"Network: {_networkWatch.ElapsedMilliseconds}");
                            HandleResponse(t.Result);
                        }

                        if (t.IsCanceled)
                        {
                            throw new EthSynchronizationException("Canceled");
                        }

                        if (t.IsFaulted)
                        {
                            throw t.Exception;
                        }
                    });
                }
            } while (dataBatches.Length != 0);

            if (_logger.IsInfo) _logger.Info($"Finished downloading node data (downloaded {_consumedNodesCount})");
        }

        private enum AddNodeResult
        {
            AlreadySaved,
            AlreadyRequested,
            Added
        }

        private LruCache<Keccak, object> _alreadySaved = new LruCache<Keccak, object>(1024 * 1024);

        private AddNodeResult AddNode(StateSyncItem stateSyncItem, string reason, bool missing = false, bool skipDbChecks = false)
        {
            if (_logger.IsTrace) _logger.Trace($"Trying to add a node {stateSyncItem.Hash} - {reason}");
            if (!missing)
            {
                if (_alreadySaved.Get(stateSyncItem.Hash) != null)
                {
                    _checkWasCached++;
                    if (_logger.IsTrace) _logger.Trace($"Trying to add a node {stateSyncItem.Hash} - node already in the DB");
                    return AddNodeResult.AlreadySaved;
                }

                // same items can have same hashes and we only need them once
                if (_dependencies.ContainsKey(stateSyncItem.Hash))
                {
                    _checkWasInDeps++;
                    if (_logger.IsTrace) _logger.Trace($"Trying to add a node {stateSyncItem.Hash} - node already included in the dependencies");
                    return AddNodeResult.AlreadyRequested;
                }

                if (!skipDbChecks)
                {
                    lock (_responseLock)
                    {
                        if (stateSyncItem.NodeDataType != NodeDataType.Code)
                        {
                            _dbChecks++;
                            bool keyExists = _stateDb.KeyExists(stateSyncItem.Hash);
                            if (keyExists)
                            {
                                if (_logger.IsTrace) _logger.Trace($"Trying to add a state node {stateSyncItem.Hash} - node already in the DB");
                                _alreadySaved.Set(stateSyncItem.Hash, new object());
                                _stateWasThere++;
                                return AddNodeResult.AlreadySaved;
                            }
                            else
                            {
                                _stateWasNotThere++;
                            }
                        }

                        if (stateSyncItem.NodeDataType == NodeDataType.Code && _codeDb.Get(stateSyncItem.Hash) != null)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Trying to add a code node {stateSyncItem.Hash} - node already in the DB");
                            _alreadySaved.Set(stateSyncItem.Hash, new object());
                            return AddNodeResult.AlreadySaved;
                        }
                    }
                }
            }

            ConcurrentStack<StateSyncItem> selectedStream;
            if (stateSyncItem.Priority < 0.5f)
            {
                selectedStream = _stream0;
            }
            else if (stateSyncItem.Priority <= 1.5f)
            {
                selectedStream = _stream1;
            }
            else
            {
                selectedStream = _stream2;
            }

            if (_logger.IsTrace) _logger.Trace($"Added a node {stateSyncItem.Hash} - {reason}");
            selectedStream.Push(stateSyncItem);
            return AddNodeResult.Added;
        }

        private class DependentItem
        {
            public StateSyncItem SyncItem { get; set; }
            public byte[] Value { get; }
            public int Counter { get; set; }

            public DependentItem(StateSyncItem syncItem, byte[] value, int counter)
            {
                SyncItem = syncItem;
                Value = value;
                Counter = counter;
            }
        }

        private void RunChainReaction(Keccak hash, string reason)
        {
            if (_logger.IsTrace) _logger.Trace($"Run chain reaction - {hash} - {reason}");

            if (_dependencies.ContainsKey(hash))
            {
                List<DependentItem> dependentItems = _dependencies[hash];
                foreach (DependentItem dependentItem in dependentItems)
                {
                    dependentItem.Counter--;
                    _dependencies.Remove(hash);
                    if (dependentItem.Counter == 0)
                    {
                        SaveNode(dependentItem.SyncItem, dependentItem.Value);
                    }
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"No nodes dependent on {hash}");
            }
        }

        private void SaveNode(StateSyncItem syncItem, byte[] data)
        {
            _savedNodesCount++;

            if (syncItem.NodeDataType == NodeDataType.State)
            {
                _savedStateCount++;
                lock (_responseLock)
                {
                    _stateDb.Set(syncItem.Hash, data);
                }
            }

            if (syncItem.NodeDataType == NodeDataType.Storage)
            {
                _savedStorageCount++;
                lock (_responseLock)
                {
                    _stateDb.Set(syncItem.Hash, data);
                }
            }

            if (syncItem.NodeDataType == NodeDataType.Code)
            {
                _savedCode++;
                lock (_responseLock)
                {
                    _codeDb.Set(syncItem.Hash, data);
                }
            }

            RunChainReaction(syncItem.Hash, "DB save");
        }

        private int maxStateLevel;
        
        private float GetPriority(StateSyncItem parent)
        {
            if (parent.NodeDataType != NodeDataType.State)
            {
                return 0;
            }
            
            if (parent.Level > maxStateLevel)
            {
                maxStateLevel = parent.Level;
            }

            return Math.Max(1 - (float)parent.Level / maxStateLevel, parent.Priority - (float)parent.Level / maxStateLevel);
        }

        private void HandleResponse(StateSyncBatch batch)
        {
            _handleWatch.Restart();
            
            Interlocked.Add(ref _pendingRequests, -1);

            if (_consumedNodesCount > _lastDownloadedNodesCount + 1000)
            {
                _lastDownloadedNodesCount = _consumedNodesCount;
                if (_logger.IsInfo) _logger.Info($"Nodes requested {_requestedNodesCount}, consumed {_consumedNodesCount}, missed {_requestedNodesCount - _consumedNodesCount}, saved {_savedNodesCount} nodes, {_savedAccounts} accounts, {_savedCode} bytecodes, {_savedStateCount - _savedAccounts} states, {_savedStorageCount} storage) - pending requests {_pendingRequests}, queued nodes {_stream0.Count}|{_stream1.Count}|{_stream2.Count}, DB checks {_stateWasThere}/{_stateWasNotThere + _stateWasThere} cached({_checkWasCached}+{_checkWasInDeps})");
                if (_logger.IsInfo) _logger.Info($"Consume ratio    : {(decimal)_consumedNodesCount/_requestedNodesCount:p2})");
                if (_logger.IsInfo) _logger.Info($"Save ratio       : {(decimal)_savedNodesCount/_requestedNodesCount:p2})");
                if (_logger.IsInfo) _logger.Info($"DB checks ratio  : {(decimal)_dbChecks/_requestedNodesCount:p2})");
            }

            if (batch.StateSyncs == null)
            {
                throw new EthSynchronizationException("Received a response with a missing request.");
            }

            if (batch.Responses == null)
            {
                throw new EthSynchronizationException("Node sent an empty response");
            }

            if (_logger.IsDebug) _logger.Debug($"Received node data - {batch.Responses.Length} items in response to {batch.StateSyncs.Length}");

            int missing = 0;
            int added = 0;

            for (int i = 0; i < batch.StateSyncs.Length; i++)
            {
                StateSyncItem currentStateSyncItem = batch.StateSyncs[i];
                if (_logger.IsTrace) _logger.Trace($"Processing response for {currentStateSyncItem.Hash}");

                if (batch.Responses.Length < i + 1)
                {
                    missing++;
                    AddNode(currentStateSyncItem, "missing", true);
                    continue;
                }

                byte[] currentResponseItem = batch.Responses[i];
                if (currentResponseItem == null)
                {
                    missing++;
                    AddNode(batch.StateSyncs[i], "missing", true);
                }
                else
                {
                    if (Keccak.Compute(currentResponseItem) != currentStateSyncItem.Hash)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Peer sent invalid data of length {batch.Responses[i]?.Length} of type {batch.StateSyncs[i].NodeDataType} at level {batch.StateSyncs[i].Level} of type {batch.StateSyncs[i].NodeDataType} Keccak({batch.Responses[i].ToHexString()}) != {batch.StateSyncs[i].Hash}");
                        throw new EthSynchronizationException("Node sent invalid data");
                    }

                    added++;

                    NodeDataType nodeDataType = currentStateSyncItem.NodeDataType;
                    if (nodeDataType == NodeDataType.Code)
                    {
                        SaveNode(currentStateSyncItem, currentResponseItem);
                        continue;
                    }

                    TrieNode trieNode = new TrieNode(NodeType.Unknown, new Rlp(currentResponseItem));
                    trieNode.ResolveNode(null);
                    switch (trieNode.NodeType)
                    {
                        case NodeType.Unknown:
                            throw new InvalidOperationException("Unknown node type");
                        case NodeType.Branch:
                            trieNode.BuildLookupTable();
                            List<Keccak> branchDependencies = new List<Keccak>();
                            for (int childIndex = 0; childIndex < 16; childIndex++)
                            {
                                Keccak child = trieNode.GetChildHash(childIndex);
                                if (child != null)
                                {
                                    AddNodeResult isDependency = AddNode(new StateSyncItem(child, nodeDataType, currentStateSyncItem.Level + 1, GetPriority(currentStateSyncItem)), "branch child");
                                    if (isDependency == AddNodeResult.Added)
                                    {
                                        branchDependencies.Add(child);
                                    }
                                }
                            }

                            DependentItem dependentBranch = new DependentItem(currentStateSyncItem, currentResponseItem, branchDependencies.Count);
                            foreach (Keccak dependency in branchDependencies)
                            {
                                AddDependency(dependency, dependentBranch);
                            }

                            if (branchDependencies.Count == 0)
                            {
                                SaveNode(currentStateSyncItem, currentResponseItem);
                            }

                            break;
                        case NodeType.Extension:
                            Keccak next = trieNode[0].Keccak;
                            if (next != null)
                            {
                                AddNodeResult isDependent = AddNode(new StateSyncItem(next, nodeDataType, currentStateSyncItem.Level + 1, currentStateSyncItem.Priority), "extension child");
                                if (isDependent == AddNodeResult.Added)
                                {
                                    AddDependency(next, new DependentItem(currentStateSyncItem, currentResponseItem, 1));
                                }
                                else if (isDependent == AddNodeResult.AlreadySaved)
                                {
                                    SaveNode(currentStateSyncItem, currentResponseItem);
                                }
                            }
                            else
                            {
                                throw new InvalidOperationException("Not expected Next to be null in the extension");
                            }

                            break;
                        case NodeType.Leaf:
                            int counter = 0;

                            if (nodeDataType == NodeDataType.State)
                            {
                                AddNodeResult hasStorage = AddNodeResult.AlreadySaved;
                                AddNodeResult hasCode = AddNodeResult.AlreadySaved;
                                Account account = accountDecoder.Decode(new Rlp.DecoderContext(trieNode.Value));
                                if (account.CodeHash != Keccak.OfAnEmptyString)
                                {
                                    hasCode = AddNode(new StateSyncItem(account.CodeHash, NodeDataType.Code, 0, 0), "code");
                                    if (hasStorage != AddNodeResult.AlreadySaved) counter++;
                                }

                                if (account.StorageRoot != Keccak.EmptyTreeHash)
                                {
                                    hasStorage = AddNode(new StateSyncItem(account.StorageRoot, NodeDataType.Storage, 0, 0), "storage");
                                    if (hasStorage != AddNodeResult.AlreadySaved) counter++;
                                }

                                DependentItem dependentItem = new DependentItem(currentStateSyncItem, currentResponseItem, counter);
                                if (hasCode != AddNodeResult.AlreadySaved)
                                {
                                    AddDependency(account.CodeHash, dependentItem);
                                }

                                if (hasStorage != AddNodeResult.AlreadySaved)
                                {
                                    AddDependency(account.StorageRoot, dependentItem);
                                }
                            }

                            if (counter == 0)
                            {
                                if (_logger.IsTrace) _logger.Trace($"Saving account - {currentStateSyncItem.Hash} in the DB");
                                if (currentStateSyncItem.NodeDataType == NodeDataType.State)
                                {
                                    _savedAccounts++;
                                }

                                SaveNode(currentStateSyncItem, currentResponseItem);
                            }

                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }

            lock (_responseLock)
            {
                _stateDb.Commit();
            }

            Interlocked.Add(ref _consumedNodesCount, added);

            if (added == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Peer sent no data in response to a request of length {batch.StateSyncs.Length}");
                throw new EthSynchronizationException("Node sent no data");
            }

            if (_logger.IsTrace) _logger.Trace($"Received node data: requested {batch.StateSyncs.Length}, missing {missing}, added {added}");
            if (_logger.IsTrace) _logger.Trace($"Handled responses - now {TotalCount} at ({_stream0.Count}|{_stream1.Count}|{_stream2.Count}) nodes");
            
            _handleWatch.Stop();
            _logger.Info($"Handle {_handleWatch.ElapsedMilliseconds}");
        }

        private void AddDependency(Keccak dependency, DependentItem dependentItem)
        {
            if (_dependencies.ContainsKey(dependency))
            {
                _dependencies[dependency].Add(dependentItem);
                return;
            }

            _dependencies.Add(dependency, new List<DependentItem>() {dependentItem});
        }

        private ConcurrentStack<StateSyncItem>[] _nodes = new ConcurrentStack<StateSyncItem>[3];
        private ConcurrentStack<StateSyncItem> _stream0 => _nodes[0];
        private ConcurrentStack<StateSyncItem> _stream1 => _nodes[1];
        private ConcurrentStack<StateSyncItem> _stream2 => _nodes[2];

        private int TotalCount => _stream0.Count + _stream1.Count + _stream2.Count;

        private bool TryTake(out StateSyncItem node)
        {
            if (!_stream0.TryPop(out node))
            {
                if (!_stream1.TryPop(out node))
                {
                    if (!_stream2.TryPop(out node))
                    {
                        return false;
                    }

                    return true;
                }
            }

            return true;
        }

        private int _pendingRequests;
        private const int MaxPendingRequestsCount = 1;

        private Stopwatch _prepareWatch = new Stopwatch();
        private Stopwatch _networkWatch = new Stopwatch();
        private Stopwatch _handleWatch = new Stopwatch();
        
        // TODO: depth first
        private StateSyncBatch[] PrepareRequests()
        {
            _prepareWatch.Reset();
            /* IDEA1: store all path with both hashes and values for all the nodes on the path to the leaf */
            /* store separately unresolved storage? */
            /* only save to the DB when everything is resolved below */
            /* always save code*/
            /* create 16 streams and take from the left? does it work together with the above?*/
            /* was it enough to hold parent info? */
            /* display confirmation on the number of synced accounts, code, storage bits */

            List<StateSyncBatch> requests = new List<StateSyncBatch>();
            if (_logger.IsTrace) _logger.Trace($"Preparing requests from ({_stream0.Count}|{_stream1.Count}|{_stream2.Count}) nodes  - pending requests {_pendingRequests}");

            while (TotalCount != 0 && _pendingRequests + requests.Count < MaxPendingRequestsCount)
            {
                StateSyncBatch batch = new StateSyncBatch();
                List<StateSyncItem> requestHashes = new List<StateSyncItem>();
                for (int i = 0; i < MaxRequestSize; i++)
                {
                    lock (_stateDb)
                    {
                        if (TryTake(out StateSyncItem result))
                        {
                            if (_logger.IsTrace) _logger.Trace($"Requesting {result.Hash}");
                            requestHashes.Add(result);
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                batch.StateSyncs = requestHashes.ToArray();
                requests.Add(batch);

                if (_logger.IsTrace) _logger.Trace($"Preparing a request with {requestHashes.Count} hashes");
            }


            var requestsArray = requests.ToArray();
            if (_logger.IsTrace)
            {
                for (int i = 0; i < requestsArray.Length; i++)
                {
                    if (_logger.IsTrace) _logger.Trace($"Request[{i}] - {requestsArray[i].StateSyncs.Length} nodes requested starting from {requestsArray[i].StateSyncs[0].Hash}");
                }
            }

            Interlocked.Add(ref _pendingRequests, requestsArray.Length);
            
            _prepareWatch.Stop();
            _logger.Info($"Prepare {_prepareWatch.ElapsedMilliseconds}");
            return requestsArray;
        }

        private Keccak _rootNode;

        public async Task<int> SyncNodeData(CancellationToken token, Keccak rootNode)
        {
            if (_rootNode != rootNode || _pendingRequests == 1)
            {
                _logger.Info($"Changing the sync root node to {rootNode}");
                _rootNode = rootNode;
                _dependencies.Clear();
                _stream0?.Clear();
                _stream1?.Clear();
                _stream2?.Clear();
                _pendingRequests = 0;
            }

            if (_stream0 == null)
            {
                _nodes[0] = new ConcurrentStack<StateSyncItem>();
                _nodes[1] = new ConcurrentStack<StateSyncItem>();
                _nodes[2] = new ConcurrentStack<StateSyncItem>();
            }

            if (rootNode == Keccak.EmptyTreeHash)
            {
                return _consumedNodesCount;
            }

            AddNode(new StateSyncItem(rootNode, NodeDataType.State, 0, 1), "initial");

            await KeepSyncing(token);
            return _consumedNodesCount;
        }

        public void SetExecutor(INodeDataRequestExecutor executor)
        {
            if (_logger.IsTrace) _logger.Trace($"Setting request executor to {executor.GetType().Name}");
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }
    }
}