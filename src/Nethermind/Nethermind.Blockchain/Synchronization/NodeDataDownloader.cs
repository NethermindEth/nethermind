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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization
{
    public interface INodeDataRequestExecutor
    {
        Task<NodeDataRequest> ExecuteRequest(NodeDataRequest request);
    }

    public class NodeDataDownloader
    {
        private readonly ILogger _logger;
        private readonly IDb _codeDb;
        private readonly ISnapshotableDb _db;
        private readonly INodeDataRequestExecutor _executor;

        public NodeDataDownloader(IDb codeDb, ISnapshotableDb db, INodeDataRequestExecutor executor, ILogManager logManager)
        {
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public async Task SyncNodeData(Keccak root)
        {
            if (root == Keccak.EmptyTreeHash)
            {
                return;
            }

            _nodes.Add((root, NodeDataType.State));
            await KeepSyncing();
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
            for (int i = 0; i < request.Request.Length; i++)
            {
                byte[] bytes = request.Response[i];
                if (bytes == null)
                {
                    _nodes.Add(request.Request[i]);
                }
                else
                {
                    if (request.Request[i].Item2 == NodeDataType.Code)
                    {
                        _codeDb[request.Request[i].Item1.Bytes] = bytes;
                        continue;
                    }
                    
                    _db[request.Request[i].Item1.Bytes] = bytes;
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
                                    _nodes.Add((child, NodeDataType.State));
                                }
                            }
                            break;
                        case NodeType.Extension:
                            Keccak next = node[0].Keccak;
                            if (next != null)
                            {
                                _nodes.Add((next, NodeDataType.State));
                            }

                            break;
                        case NodeType.Leaf:
                            Account account = accountDecoder.Decode(new Rlp.DecoderContext(node.Value));
                            if (account.CodeHash != Keccak.OfAnEmptyString)
                            {
                                _nodes.Add((account.CodeHash, NodeDataType.Code));
                            }
                            // storage tree
                            // code
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
            List<(Keccak, NodeDataType)> requestHashes = new List<(Keccak, NodeDataType)>();
            while (_nodes.Count != 0)
            {
                NodeDataRequest request = new NodeDataRequest();
                requests.Add(request);
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

                request.Request = requestHashes.ToArray();
            }

            return requests.ToArray();
        }
    }
}