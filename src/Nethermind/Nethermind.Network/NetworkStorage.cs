// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        private readonly List<NetworkNode> _nodesList = new();
        private readonly HashSet<PublicKey> _nodePublicKeys = new();
        private long _updateCounter;
        private long _removeCounter;
        private NetworkNode[] _nodes;

        public NetworkStorage(IFullDb? fullDb, ILogManager? logManager)
        {
            _fullDb = fullDb ?? throw new ArgumentNullException(nameof(fullDb));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public int PersistedNodesCount
        {
            get
            {
                NetworkNode[] nodes = _nodes;
                return nodes?.Length ?? _fullDb.Count;
            }
        }

        public NetworkNode[] GetPersistedNodes()
        {
            NetworkNode[] nodes = _nodes;
            return nodes is not null ? nodes : GenerateNodes();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private NetworkNode[] GenerateNodes()
        {
            _nodesList.Clear();
            foreach (byte[]? nodeRlp in _fullDb.Values)
            {
                if (nodeRlp is null)
                {
                    continue;
                }

                try
                {
                    _nodesList.Add(GetNode(nodeRlp));
                }
                catch (Exception e)
                {
                    if (_logger.IsDebug) _logger.Debug($"Failed to add one of the persisted nodes (with RLP {nodeRlp.ToHexString()}), {e.Message}");
                }
            }

            NetworkNode[] nodes = _nodes = _nodesList.ToArray();
            return nodes;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void UpdateNode(NetworkNode node)
        {
            (_currentBatch ?? (IKeyValueStore)_fullDb)[node.NodeId.Bytes] = Rlp.Encode(node).Bytes;
            _updateCounter++;

            if (!_nodePublicKeys.Contains(node.NodeId))
            {
                _nodePublicKeys.Add(node.NodeId);
                // New node clear the cache
                _nodes = null;
            }
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

            RemoveLocal(nodeId);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void RemoveLocal(PublicKey nodeId)
        {
            int length = _nodesList.Count;
            for (int i = 0; i < length; i++)
            {
                if (nodeId == _nodesList[i].NodeId)
                {
                    _nodesList.RemoveAt(i);
                    _nodes = _nodesList.ToArray();
                    _nodePublicKeys.Remove(nodeId);
                    return;
                }
            }
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
