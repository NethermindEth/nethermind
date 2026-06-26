// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network
{
    public class NetworkStorage(IFullDb? fullDb, ILogManager? logManager) : INetworkStorage
    {
        private static readonly NetworkNodeDecoder NodeDecoder = NetworkNodeDecoder.Instance;

        private readonly Lock _lock = new();
        private readonly IFullDb _fullDb = fullDb ?? throw new ArgumentNullException(nameof(fullDb));
        private readonly ILogger _logger = logManager?.GetClassLogger<NetworkStorage>() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly Dictionary<PublicKey, NetworkNode> _nodesDict = [];
        private long _updateCounter;
        private long _removeCounter;
        private NetworkNode[]? _nodes;
        private bool _loadedFromDb;

        public int PersistedNodesCount => GetPersistedNodes().Length;

        public NetworkNode[] GetPersistedNodes()
        {
            NetworkNode[]? nodes = _nodes;
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

                EnsureLoadedFromDbNoLock();

                return _nodesDict.Count == 0 ? [] : CopyDictToArray();
            }
        }

        private NetworkNode[] CopyDictToArray()
        {
            NetworkNode[] nodes = new NetworkNode[_nodesDict.Count];
            _nodesDict.Values.CopyTo(nodes, 0);
            return (_nodes = nodes);
        }

        private void EnsureLoadedFromDbNoLock()
        {
            if (!_loadedFromDb)
            {
                LoadFromDbNoLock();
                _loadedFromDb = true;
            }
        }

        private void LoadFromDbNoLock()
        {
            foreach (byte[] nodeRlp in _fullDb.Values)
            {
                try
                {
                    NetworkNode node = GetNode(nodeRlp);
                    _nodesDict.TryAdd(node.NodeId, node);
                }
                catch (Exception e)
                {
                    if (_logger.IsDebug) _logger.Debug($"Failed to add one of the persisted nodes (with RLP {nodeRlp.ToHexString()}), {e.Message}");
                }
            }
        }

        public void UpdateNode(NetworkNode node)
        {
            using ArrayPoolSpan<byte> rlp = NodeDecoder.EncodeToArrayPoolSpan(node);
            lock (_lock)
            {
                UpdateNodeImpl(node, rlp);
            }
        }

        private void UpdateNodeImpl(NetworkNode node, ReadOnlySpan<byte> rlp)
        {
            EnsureLoadedFromDbNoLock();

            (_currentBatch ?? (IWriteOnlyKeyValueStore)_fullDb).PutSpan(node.NodeId.Bytes, rlp);
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
            List<(NetworkNode Node, ArrayPoolSpan<byte> Rlp)> encodedNodes = [];
            try
            {
                foreach (NetworkNode node in nodes)
                {
                    encodedNodes.Add((node, NodeDecoder.EncodeToArrayPoolSpan(node)));
                }

                lock (_lock)
                {
                    for (int i = 0; i < encodedNodes.Count; i++)
                    {
                        (NetworkNode node, ArrayPoolSpan<byte> rlp) = encodedNodes[i];
                        UpdateNodeImpl(node, rlp);
                    }
                }
            }
            finally
            {
                for (int i = 0; i < encodedNodes.Count; i++)
                {
                    encodedNodes[i].Rlp.Dispose();
                }
            }
        }

        public void RemoveNode(PublicKey nodeId)
        {
            lock (_lock)
            {
                EnsureLoadedFromDbNoLock();

                (_currentBatch ?? (IWriteOnlyKeyValueStore)_fullDb)[nodeId.Bytes] = null;
                _removeCounter++;

                if (_nodesDict.Remove(nodeId))
                {
                    // Clear the cache
                    _nodes = null;
                }
            }
        }

        private IWriteBatch? _currentBatch;

        public void StartBatch()
        {
            lock (_lock)
            {
                DiscardBatchNoLock();
                _currentBatch = _fullDb.StartWriteBatch();
            }
        }

        public void Commit()
        {
            IWriteBatch? currentBatch;
            lock (_lock)
            {
                if (_logger.IsTrace) _logger.Trace($"[{_fullDb.Name}] Committing nodes, updates: {_updateCounter}, removes: {_removeCounter}");
                currentBatch = _currentBatch;
                _currentBatch = null;
                _updateCounter = 0;
                _removeCounter = 0;
            }

            try
            {
                currentBatch?.Dispose();
            }
            catch
            {
                ClearLocalCache();
                throw;
            }

            if (_logger.IsTrace)
            {
                LogDbContent(_fullDb.Values);
            }
        }

        private void DiscardBatchNoLock()
        {
            IWriteBatch? currentBatch = _currentBatch;
            _currentBatch = null;
            _updateCounter = 0;
            _removeCounter = 0;

            if (currentBatch is not null)
            {
                currentBatch.Clear();
                currentBatch.Dispose();
                ClearLocalCacheNoLock();
            }
        }

        private void ClearLocalCache()
        {
            lock (_lock)
            {
                ClearLocalCacheNoLock();
            }
        }

        private void ClearLocalCacheNoLock()
        {
            _nodesDict.Clear();
            _nodes = null;
            _loadedFromDb = false;
        }

        public bool AnyPendingChange() => _updateCounter > 0 || _removeCounter > 0;

        private static NetworkNode GetNode(byte[] networkNodeRaw) => NodeDecoder.DecodeCompleteNotNull(networkNodeRaw);

        private void LogDbContent(IEnumerable<byte[]?> values)
        {
            StringBuilder sb = new();
            sb.AppendLine($"[{_fullDb.Name}]");
            foreach (byte[]? value in values)
            {
                if (value is null)
                {
                    continue;
                }

                NetworkNode node = GetNode(value);
                sb.AppendLine($"{node.NodeId}@{node.Host}:{node.Port}, Rep: {node.Reputation}");
            }

            _logger.Trace(sb.ToString());
        }
    }
}
