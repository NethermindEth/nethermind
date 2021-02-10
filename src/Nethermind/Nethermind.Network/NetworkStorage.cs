//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.Text;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network
{
    public class NetworkStorage : INetworkStorage
    {
        private readonly IFullDb _fullDb;
        private readonly ILogger _logger;
        private long _updateCounter;
        private long _removeCounter;

        public NetworkStorage(IFullDb? fullDb, ILogManager? logManager)
        {
            _fullDb = fullDb ?? throw new ArgumentNullException(nameof(fullDb));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public int PersistedNodesCount
        {
            get
            {
                return _fullDb.Values.Count;
            }
        }

        public NetworkNode[] GetPersistedNodes()
        {
            List<NetworkNode> nodes = new();
            foreach (byte[]? nodeRlp in _fullDb.Values)
            {
                if (nodeRlp == null)
                {
                    continue;
                }
                
                try
                {
                    nodes.Add(GetNode(nodeRlp));
                }
                catch (Exception e)
                {
                    if (_logger.IsDebug) _logger.Debug($"Failed to add one of the persisted nodes (with RLP {nodeRlp.ToHexString()}), {e.Message}");
                }
            }

            return nodes.ToArray();
        }

        public void UpdateNode(NetworkNode node)
        {
            (_currentBatch ?? (IKeyValueStore)_fullDb)[node.NodeId.Bytes] = Rlp.Encode(node).Bytes;
            _updateCounter++;
        }

        public void UpdateNodes(IEnumerable<NetworkNode> nodes)
        {
            foreach (NetworkNode node in nodes)
            {
                UpdateNode(node);
            }
        }

        public void RemoveNode(PublicKey nodeId)
        {
            (_currentBatch ?? (IKeyValueStore)_fullDb)[nodeId.Bytes] = null;
            _removeCounter++;
        }

        private IBatch? _currentBatch;
        
        public void StartBatch()
        {
            _currentBatch = _fullDb.StartBatch();
            _updateCounter = 0;
            _removeCounter = 0;
        }

        public void Commit()
        {
            if (_logger.IsTrace) _logger.Trace($"[{_fullDb.Name}] Committing nodes, updates: {_updateCounter}, removes: {_removeCounter}");
            _currentBatch?.Dispose();
            if (_logger.IsTrace)
            {
                LogDbContent(_fullDb.Values);
            }
        }

        public bool AnyPendingChange()
        {
            return _updateCounter > 0 || _removeCounter > 0;
        }

        private static NetworkNode GetNode(byte[] networkNodeRaw)
        {
            NetworkNode persistedNode = Rlp.Decode<NetworkNode>(networkNodeRaw);
            return persistedNode;
        }

        private void LogDbContent(IEnumerable<byte[]> values)
        {
            StringBuilder sb = new();
            sb.AppendLine($"[{_fullDb.Name}]");
            foreach (byte[] value in values)
            {
                NetworkNode node = GetNode(value);
                sb.AppendLine($"{node.NodeId}@{node.Host}:{node.Port}, Rep: {node.Reputation}");
            }

            _logger.Trace(sb.ToString());
        }
    }
}
