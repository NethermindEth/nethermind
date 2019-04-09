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

        private const int MaxRequestSize = 1024;
        private int _lastDownloadedNodesCount = 0;
        private int _downloadedNodesCount;

        private readonly ILogger _logger;
        private readonly ISnapshotableDb _stateDb;
        private readonly IDb _codeDb;
        private INodeDataRequestExecutor _executor;

        private object _responseLock = new object();

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

        private async Task KeepSyncing()
        {
            NodeDataRequest[] dataRequests;
            do
            {
                dataRequests = PrepareRequests();
                for (int i = 0; i < dataRequests.Length; i++)
                {
                    await _executor.ExecuteRequest(dataRequests[i]).ContinueWith(t =>
                    {
                        if (t.IsCompleted)
                        {
                            HandleResponse(t.Result);
                        }
                    });
                }
            } while (dataRequests.Length != 0);

            if (_logger.IsInfo) _logger.Info($"Finished downloading node data (downloaded {_downloadedNodesCount})");
        }


        private void AddNode(RequestItem requestItem)
        {
            lock (_stateDb)
            {
                if (requestItem.NodeDataType == NodeDataType.State && _stateDb.Get(requestItem.Hash) != null
                    || requestItem.NodeDataType == NodeDataType.Storage && _stateDb.Get(requestItem.Hash) != null
                    || requestItem.NodeDataType == NodeDataType.Code && _codeDb.Get(requestItem.Hash) != null)
                {
                    // TODO: this will cause no finalized sync (if we do not store pending requests)
                    // pending requests should have a state root block marked...?
                    return;
                }
            }

            ConcurrentQueue<RequestItem> selectedStream;
            if (requestItem.Priority == 0 && _stream0.Count < _maxStream0Count)
            {
                selectedStream = _stream0;
            }
            else if (requestItem.Priority <= 1 && _stream1.Count < _maxStream1Count)
            {
                selectedStream = _stream1;
            }
            else
            {
                selectedStream = _stream2;
            }

            selectedStream.Enqueue(requestItem);
        }

        private int _maxStream0Count = 4096;
        private int _maxStream1Count = 2048;

        private void HandleResponse(NodeDataRequest request)
        {
            Interlocked.Add(ref _pendingRequests, -1);
            
            if (_downloadedNodesCount > _lastDownloadedNodesCount + 10000)
            {
                _lastDownloadedNodesCount = _downloadedNodesCount;
                if (_logger.IsInfo) _logger.Info($"Downloading nodes (downloaded {_downloadedNodesCount}) - pending requests {_pendingRequests}");
            }

            if (request.Request == null)
            {
                throw new EthSynchronizationException("Received a response with a missing request.");
            }

            if (request.Response == null)
            {
                throw new EthSynchronizationException("Node sent an empty response");
            }

            if (_logger.IsTrace) _logger.Trace($"Received node data - {request.Response.Length} items in response to {request.Request.Length}");

            int missing = 0;
            int added = 0;

            for (int i = 0; i < request.Request.Length; i++)
            {
                RequestItem currentRequestItem = request.Request[i];
                byte[] currentResponseItem = request.Response[i];
                if (request.Response.Length < i + 1)
                {
                    missing++;
                    AddNode(currentRequestItem);
                    continue;
                }

                if (Keccak.Compute(currentResponseItem) != currentRequestItem.Hash)
                {
                    if (_logger.IsWarn) _logger.Warn($"Peer sent invalid data of length {request.Response[i]?.Length} of type {request.Request[i].NodeDataType} at level {request.Request[i].Level} Keccak({request.Response[i].ToHexString()}) != {request.Request[i].Hash}");
                    throw new EthSynchronizationException("Node sent invalid data");
                }

                if (currentResponseItem == null)
                {
                    missing++;
                    AddNode(request.Request[i]);
                }
                else
                {
                    added++;

                    throw new Exception("Save only on return");
                    
                    NodeDataType nodeDataType = currentRequestItem.NodeDataType;
                    if (nodeDataType == NodeDataType.Code)
                    {
                        // save only on return
                        _codeDb[currentRequestItem.Hash.Bytes] = currentResponseItem;
                        continue;
                    }

                    lock (_responseLock)
                    {
                        // save only on return
                        _stateDb[currentRequestItem.Hash.Bytes] = currentResponseItem;
                    }

                    TrieNode trieNode = new TrieNode(NodeType.Unknown, new Rlp(currentResponseItem));
                    trieNode.ResolveNode(null);
                    switch (trieNode.NodeType)
                    {
                        case NodeType.Unknown:
                            throw new InvalidOperationException("Unknown node type");
                        case NodeType.Branch:
                            trieNode.BuildLookupTable();
                            for (int j = 0; j < 16; j++)
                            {
                                Keccak child = trieNode.GetChildHash(j);
                                if (child != null)
                                {
                                    AddNode(new RequestItem(child, nodeDataType, currentRequestItem.Level + 1, Math.Max(currentRequestItem.Priority, (int) Math.Sqrt(j) / 2)));
                                }
                            }

                            break;
                        case NodeType.Extension:
                            Keccak next = trieNode[0].Keccak;
                            if (next != null)
                            {
                                AddNode(new RequestItem(next, nodeDataType, currentRequestItem.Level + 1, currentRequestItem.Priority));
                            }

                            break;
                        case NodeType.Leaf:
                            if (nodeDataType == NodeDataType.State)
                            {
                                Account account = accountDecoder.Decode(new Rlp.DecoderContext(trieNode.Value));
                                if (account.CodeHash != Keccak.OfAnEmptyString)
                                {
                                    // always add code with priority 0
                                    AddNode(new RequestItem(account.CodeHash, NodeDataType.Code, 0, 0));
                                }

                                if (account.StorageRoot != Keccak.EmptyTreeHash)
                                {
                                    // add storage wth priority 0? let us try
                                    AddNode(new RequestItem(account.StorageRoot, NodeDataType.Storage, 0, 0));
                                }
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

            Interlocked.Add(ref _downloadedNodesCount, added);

            if (_logger.IsTrace) _logger.Trace($"Received node data: requested {request.Request.Length}, missing {missing}, added {added}");
            if (_logger.IsTrace) _logger.Trace($"Handled responses - now {TotalCount} at ({_stream0.Count}|{_stream1.Count}|{_stream2.Count}) nodes");
        }

        private ConcurrentQueue<RequestItem>[] _nodes = new ConcurrentQueue<RequestItem>[3];
        private ConcurrentQueue<RequestItem> _stream0 => _nodes[0];
        private ConcurrentQueue<RequestItem> _stream1 => _nodes[1];
        private ConcurrentQueue<RequestItem> _stream2 => _nodes[2];

        private int TotalCount => _stream0.Count + _stream1.Count + _stream2.Count;

        private bool TryTake(out RequestItem node)
        {
            if (!_stream0.TryDequeue(out node))
            {
                if (!_stream1.TryDequeue(out node))
                {
                    if (!_stream2.TryDequeue(out node))
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
        
        // TODO: depth first
        private NodeDataRequest[] PrepareRequests()
        {
            /* IDEA1: store all path with both hashes and values for all the nodes on the path to the leaf */
            /* store separately unresolved storage? */
            /* only save to the DB when everything is resolved below */
            /* always save code*/
            /* create 16 streams and take from the left? does it work together with the above?*/
            /* was it enough to hold parent info? */
            /* display confirmation on the number of synced accounts, code, storage bits */

            List<NodeDataRequest> requests = new List<NodeDataRequest>();
            if (_logger.IsTrace) _logger.Trace($"Preparing requests from ({_stream0.Count}|{_stream1.Count}|{_stream2.Count}) nodes  - pending requests {_pendingRequests}");

            while (TotalCount != 0 && _pendingRequests + requests.Count < MaxPendingRequestsCount)
            {
                NodeDataRequest request = new NodeDataRequest();
                List<RequestItem> requestHashes = new List<RequestItem>();
                for (int i = 0; i < MaxRequestSize; i++)
                {
                    lock (_stateDb)
                    {
                        if (TryTake(out RequestItem result))
                        {
                            requestHashes.Add(result);
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                request.Request = requestHashes.ToArray();
                requests.Add(request);

                if (_logger.IsTrace) _logger.Trace($"Preparing a request with {requestHashes.Count} hashes");
            }


            var requestsArray = requests.ToArray();
            if (_logger.IsTrace)
            {
                for (int i = 0; i < requestsArray.Length; i++)
                {
                    _logger.Trace($"Request[{i}] - {requestsArray[i].Request.Length} nodes requested starting from {requestsArray[i].Request[0].Hash}");
                }
            }

            Interlocked.Add(ref _pendingRequests, requestsArray.Length);
            return requestsArray;
        }

        public async Task SyncNodeData((Keccak Hash, NodeDataType NodeDataType)[] initialNodes)
        {
            if (_stream0 == null)
            {
                _nodes[0] = new ConcurrentQueue<RequestItem>();
                _nodes[1] = new ConcurrentQueue<RequestItem>();
                _nodes[2] = new ConcurrentQueue<RequestItem>();
            }

            if (_logger.IsTrace)
            {
                _logger.Trace($"Syncing node data");
                for (int i = 0; i < initialNodes.Length; i++)
                {
                    _logger.Trace($"Initial node: {initialNodes[i].Hash}");
                }
            }

            if (initialNodes.Length == 0 || (initialNodes.Length == 1 && initialNodes[0].Hash == Keccak.EmptyTreeHash))
            {
                return;
            }

            for (int i = 0; i < initialNodes.Length; i++)
            {
                AddNode(new RequestItem(initialNodes[i].Hash, initialNodes[i].NodeDataType, 0, 0));
            }

            await KeepSyncing();
        }

        public void SetExecutor(INodeDataRequestExecutor executor)
        {
            if (_logger.IsTrace) _logger.Trace($"Setting request executor to {executor.GetType().Name}");
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }
    }
}