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
using Nethermind.Core.Crypto;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization
{
    public class NodeDataDownloader
    {
        private readonly ISnapshotableDb _db;
        private Keccak _root;
            
        public NodeDataDownloader(ISnapshotableDb db)
        {
            _db = db;
        }
            
        public void SyncNodeData(Keccak root)
        {
            _root = root;
            _nodes.Add((root, NodeType.State));
        }

        private ConcurrentBag<(Keccak, NodeType)> _nodes = new ConcurrentBag<(Keccak, NodeType)>();

        private const int maxRequestSize = 256;

        public void HandleResponse(NodeDataRequest request)
        {
            throw new NotImplementedException();
        }
        
        public NodeDataRequest PrepareRequest()
        {
            List<(Keccak, NodeType)> requestHashes = new List<(Keccak, NodeType)>();
            for (int i = 0; i < maxRequestSize; i++)
            {
                if (_nodes.TryTake(out (Keccak Hash, NodeType NodeType) result))
                {
                    requestHashes.Add((result.Hash, result.NodeType));
                }
                else
                {
                    break;
                }
            }

            var request = new NodeDataRequest();
            request.Request = requestHashes.ToArray();
            return request;
        }
    }
}