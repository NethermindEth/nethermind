// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private readonly object _lock = new();
        private readonly IFullDb _fullDb;
        private readonly ILogger _logger;
        private readonly Dictionary<PublicKey, NetworkNode> _nodesDict = new();
        private long _updateCounter;
        private long _removeCounter;
        private NetworkNode[]? _nodes;

        public NetworkStorage(IFullDb? fullDb, ILogManager? logManager)
        {
            _fullDb = fullDb ?? throw new ArgumentNullException(nameof(fullDb));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public int PersistedNodesCount => GetPersistedNodes().Length;

        public NetworkNode[] GetPersistedNodes()
        {
            NetworkNode[] nodes = _nodes;
            return nodes ?? GenerateNodes();
        }

        private NetworkNode[] GenerateNodes()
        {
            lock (_lock)
            {
                NetworkNode[]? nodes = _nodes;
                if (nodes is not null)
                {
                    // Already updated
                    return nodes;
                }

                if (_nodesDict.Count == 0)
                {
                    LoadFromDb();
                }

                return _nodesDict.Count == 0 ? Array.Empty<NetworkNode>() : CopyDictToArray();
            }
        }

        private NetworkNode[] CopyDictToArray()
        {
            NetworkNode[] nodes = new NetworkNode[_nodesDict.Count];
            _nodesDict.Values.CopyTo(nodes, 0);
            return (_nodes = nodes);
        }

        private void LoadFromDb()
        {
            foreach (byte[]? nodeRlp in _fullDb.Values)
            {
                if (nodeRlp is null)
                {
                    continue;
                }

                try
                {
                    NetworkNode node = GetNode(nodeRlp);
                    _nodesDict[node.NodeId] = node;
                }
                catch (Exception e)
                {
                    if (_logger.IsDebug) _logger.Debug($"Failed to add one of the persisted nodes (with RLP {nodeRlp.ToHexString()}), {e.Message}");
                }
            }
        }

        public void UpdateNode(NetworkNode node)
        {
            lock (_lock)
            {
                UpdateNodeImpl(node);
            }
        }

        private void UpdateNodeImpl(NetworkNode node)
        {
            (_currentBatch ?? (IKeyValueStore)_fullDb)[node.NodeId.Bytes] = Rlp.Encode(node).Bytes;
            _updateCounter++;

            if (!_nodesDict.ContainsKey(node.NodeId))
            {
                _nodesDict[node.NodeId] = node;
                // New node, clear the cache
                _nodes = null;
            }
            else
            {
                _nodesDict[node.NodeId] = node;
            }
        }

        public void UpdateNodes(IEnumerable<NetworkNode> nodes)
        {
            lock (_lock)
            {
                foreach (NetworkNode node in nodes)
                {
                    UpdateNodeImpl(node);
                }
            }
        }

        public void RemoveNode(PublicKey nodeId)
        {
            (_currentBatch ?? (IKeyValueStore)_fullDb)[nodeId.Bytes] = null;
            _removeCounter++;

            RemoveLocal(nodeId);
        }

        private void RemoveLocal(PublicKey nodeId)
        {
            lock (_lock)
            {
                if (_nodesDict.Remove(nodeId))
                {
                    // Clear the cache
                    _nodes = null;
                }
            }
        }

        private ISpanKeyBatch? _currentBatch;

        public void StartBatch()
        {
            _currentBatch = _fullDb.StartLargeKeyBatch();
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
