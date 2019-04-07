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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization
{
    public class NodeDataDownloader : INodeDataDownloader
    {
        private readonly ILogger _logger;
        private readonly IDb _codeDb;
        private readonly ISnapshotableDb _db;
        private INodeDataRequestExecutor _executor;

        public NodeDataDownloader(IDb codeDb, ISnapshotableDb db, ILogManager logManager)
        {
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public NodeDataDownloader(IDb codeDb, ISnapshotableDb db, INodeDataRequestExecutor executor, ILogManager logManager)
        {
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            SetExecutor(executor);
        }

        private async Task KeepSyncing()
        {
            NodeDataRequest[] dataRequests;
            do
            {
                dataRequests = PrepareRequests();
                Task[] tasks = dataRequests.Select(dr => _executor.ExecuteRequest(dr).ContinueWith(
                    t =>
                    {
                        if (t.IsCompleted)
                        {
                            HandleResponse(t.Result);
                        }
                    }
                )).ToArray();
                await Task.WhenAll(tasks);
            } while (dataRequests.Length != 0);
        }

        private AccountDecoder accountDecoder = new AccountDecoder();

        private void HandleResponse(NodeDataRequest request)
        {
            if(_logger.IsTrace) _logger.Trace($"Received node data - {request.Response.Length} items in response to {request.Response.Length}");
            
            for (int i = 0; i < request.Request.Length; i++)
            {
                if (request.Response.Length < i + 1)
                {
                    if(_logger.IsTrace) _logger.Trace($"Response missing - readding {request.Request[i].Hash}");
                    _nodes.Add(request.Request[i]);
                    continue;
                }
                
                byte[] bytes = request.Response[i];
                if (Keccak.Compute(bytes) != request.Request[i].Hash)
                {
                    throw new InvalidDataException("Peer sent invalid data");
                }

                if (bytes == null)
                {
                    _nodes.Add(request.Request[i]);
                }
                else
                {
                    NodeDataType nodeDataType = request.Request[i].NodeDataType;
                    if (nodeDataType == NodeDataType.Code)
                    {
                        _codeDb[request.Request[i].Hash.Bytes] = bytes;
                        continue;
                    }

                    _db[request.Request[i].Hash.Bytes] = bytes;
                    TrieNode node = new TrieNode(NodeType.Unknown, new Rlp(bytes));
                    node.ResolveNode(null);
                    switch (node.NodeType)
                    {
                        case NodeType.Unknown:
                            throw new InvalidOperationException("Unknown node type");
                        case NodeType.Branch:
                            node.BuildLookupTable();
                            for (int j = 0; j < 16; j++)
                            {
                                Keccak child = node.GetChildHash(j);
                                if (child != null)
                                {
                                    _nodes.Add((child, nodeDataType));
                                }
                            }

                            break;
                        case NodeType.Extension:
                            Keccak next = node[0].Keccak;
                            if (next != null)
                            {
                                _nodes.Add((next, nodeDataType));
                            }

                            break;
                        case NodeType.Leaf:
                            if (nodeDataType == NodeDataType.State)
                            {
                                Account account = accountDecoder.Decode(new Rlp.DecoderContext(node.Value));
                                if (account.CodeHash != Keccak.OfAnEmptyString)
                                {
                                    _nodes.Add((account.CodeHash, NodeDataType.Code));
                                }

                                if (account.StorageRoot != Keccak.EmptyTreeHash)
                                {
                                    _nodes.Add((account.StorageRoot, NodeDataType.Storage));
                                }
                            }

                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
        }

        private ConcurrentBag<(Keccak, NodeDataType)> _nodes = new ConcurrentBag<(Keccak, NodeDataType)>();

        private const int maxRequestSize = 256;

        private NodeDataRequest[] PrepareRequests()
        {
            List<NodeDataRequest> requests = new List<NodeDataRequest>();
            List<(Keccak Hash, NodeDataType NodeDataType)> requestHashes = new List<(Keccak, NodeDataType)>();
            while (_nodes.Count != 0)
            {
                NodeDataRequest request = new NodeDataRequest();

                for (int i = 0; i < maxRequestSize; i++)
                {
                    if (_nodes.TryTake(out (Keccak Hash, NodeDataType NodeType) result))
                    {
                        requestHashes.Add((result.Hash, result.NodeType));
                    }
                    else
                    {
                        break;
                    }
                }

                requests.Add(request);

                if (_logger.IsTrace) _logger.Trace($"Preparing a request with {requestHashes.Count} hashes");
                request.Request = requestHashes.ToArray();
            }


            var requestsArray = requests.ToArray();
            if (_logger.IsTrace)
            {
                _logger.Trace($"Prepared {requestsArray.Length} requests");
                for (int i = 0; i < requestsArray.Length; i++)
                {
                    _logger.Trace($"Request[{i}] - {requestsArray[i].Request.Length} nodes requested starting from {requestsArray[i].Request[0].Hash}");
                }
            }

            return requestsArray;
        }

        public async Task SyncNodeData((Keccak Hash, NodeDataType NodeDataType)[] initialNodes)
        {
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
                _nodes.Add((initialNodes[i].Hash, initialNodes[i].NodeDataType));
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