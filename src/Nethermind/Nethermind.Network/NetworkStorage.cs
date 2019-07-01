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
using System.Collections.Generic;
using System.IO;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Store;

namespace Nethermind.Network
{
    public class NetworkStorage : INetworkStorage
    {
        private readonly IFullDb _fullDb;
        private readonly ILogger _logger;
        private long _updateCounter;
        private long _removeCounter;

        public NetworkStorage(IFullDb fullDb, ILogManager logManager)
        {
            _fullDb = fullDb ?? throw new ArgumentNullException(nameof(fullDb));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public NetworkNode[] GetPersistedNodes()
        {
            List<NetworkNode> nodes = new List<NetworkNode>();
            foreach (byte[] nodeRlp in _fullDb.Values)
            {
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
            _fullDb[node.NodeId.Bytes] = Rlp.Encode(node).Bytes;
            _updateCounter++;
        }

        public void UpdateNodes(IEnumerable<NetworkNode> nodes)
        {
            foreach (NetworkNode node in nodes)
            {
                UpdateNode(node);
            }
        }

        public void RemoveNodes(NetworkNode[] nodes)
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                _fullDb.Remove(nodes[i].NodeId.Bytes);
                _removeCounter++;
            }
        }

        public void StartBatch()
        {
            _fullDb.StartBatch();
            _updateCounter = 0;
            _removeCounter = 0;
        }

        public void Commit()
        {
            if (_logger.IsTrace) _logger.Trace($"[{_fullDb.Description}] Committing nodes, updates: {_updateCounter}, removes: {_removeCounter}");
            _fullDb.CommitBatch();
            if (_logger.IsTrace)
            {
                LogDbContent(_fullDb.Values);
            }
        }

        public bool AnyPendingChange()
        {
            return _updateCounter > 0 || _removeCounter > 0;
        }

        private NetworkNode GetNode(byte[] networkNodeRaw)
        {
            var persistedNode = Rlp.Decode<NetworkNode>(networkNodeRaw);
            return persistedNode;
        }

        private void LogDbContent(IEnumerable<byte[]> values)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{_fullDb.Description}]");
            foreach (var value in values)
            {
                var node = GetNode(value);
                sb.AppendLine($"{node.NodeId}@{node.Host}:{node.Port}, Rep: {node.Reputation}");
            }

            _logger.Trace(sb.ToString());
        }
    }
}