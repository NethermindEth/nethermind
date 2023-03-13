// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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

        public int PersistedNodesCount => GetPersistedNodes().Length;

        public NetworkNode[] GetPersistedNodes()
        {
            NetworkNode[] nodes = _nodes;
            return nodes is not null ? nodes : GenerateNodes();
        }

        private NetworkNode[] GenerateNodes()
        {
            NetworkNode[] nodes;
            lock (_lock)
            {
                nodes = _nodes;
                if (nodes is not null)
                {
                    // Already updated
                    return nodes;
                }

                List<NetworkNode> nodeList = _nodesList;
                if (nodeList.Count > 0)
                {
                    return (_nodes = nodeList.ToArray());
                }

                foreach (byte[]? nodeRlp in _fullDb.Values)
                {
                    if (nodeRlp is null)
                    {
                        continue;
                    }

                    try
                    {
                        NetworkNode node = GetNode(nodeRlp);
                        nodeList.Add(node);
                        _nodePublicKeys.Add(node.NodeId);
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Failed to add one of the persisted nodes (with RLP {nodeRlp.ToHexString()}), {e.Message}");
                    }
                }

                if (nodeList.Count == 0)
                {
                    return Array.Empty<NetworkNode>();
                }
                else
                {
                    return (_nodes = nodeList.ToArray());
                }
            }
        }

        public void UpdateNode(NetworkNode node)
        {
            lock (_lock)
            {
                (_currentBatch ?? (IKeyValueStore)_fullDb)[node.NodeId.Bytes] = Rlp.Encode(node).Bytes;
                _updateCounter++;

                if (!_nodePublicKeys.Contains(node.NodeId))
                {
                    _nodePublicKeys.Add(node.NodeId);
                    _nodesList.Add(node);
                    // New node, clear the cache
                    _nodes = null;
                }
                else
                {
                    Span<NetworkNode> span = CollectionsMarshal.AsSpan(_nodesList);
                    for (int i = 0; i < span.Length; i++)
                    {
                        if (node.NodeId == span[i].NodeId)
                        {
                            span[i] = node;
                        }
                    }
                }
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

        private void RemoveLocal(PublicKey nodeId)
        {
            lock (_lock)
            {
                Span<NetworkNode> span = CollectionsMarshal.AsSpan(_nodesList);
                for (int i = 0; i < span.Length; i++)
                {
                    if (nodeId == span[i].NodeId)
                    {
                        _nodesList.RemoveAt(i);
                        _nodePublicKeys.Remove(nodeId);
                        // New node, clear the cache
                        _nodes = null;
                        return;
                    }
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
