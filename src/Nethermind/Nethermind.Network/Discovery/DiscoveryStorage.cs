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

using System.IO;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Model;
using Nethermind.Db;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Store;

namespace Nethermind.Network.Discovery
{
    public class DiscoveryStorage : IDiscoveryStorage
    {
        private readonly INetworkConfig _configurationProvider;
        private readonly INodeFactory _nodeFactory;
        private readonly IPerfService _perfService;
        private readonly IFullDb _db;
        private readonly ILogger _logger;
        private long _updateCounter = 0;
        private long _removeCounter = 0;

        public DiscoveryStorage(IConfigProvider configurationProvider, INodeFactory nodeFactory, ILogger logger, IPerfService perfService)
        {
            _configurationProvider = configurationProvider.NetworkConfig;
            _nodeFactory = nodeFactory;
            _logger = logger;
            _perfService = perfService;
            _db = new FullDbOnTheRocks(Path.Combine(_configurationProvider.DbBasePath, FullDbOnTheRocks.DiscoveryNodesDbPath));
        }

        public (Node Node, long PersistedReputation)[] GetPersistedNodes()
        {
            return _db.Values.Select(GetNode).ToArray();
        }

        public void UpdateNodes(INodeLifecycleManager[] nodes)
        {
            //add / update valid nodes
            for (var i = 0; i < nodes.Length; i++)
            {
                var manager = nodes[i];
                var node = manager.ManagedNode;
                var networkNode = new NetworkNode(node.Id.Bytes, node.Host, node.Port, node.Description, manager.NodeStats.NewPersistedNodeReputation);
                _db[networkNode.NodeId.Bytes] = Rlp.Encode(networkNode).Bytes;
                _updateCounter++;
            }
        }

        public void RemoveNodes(INodeLifecycleManager[] nodes)
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                _db.Remove(nodes[i].ManagedNode.Id.Bytes);
                _removeCounter++;
            }
        }

        public void StartBatch()
        {
            _db.StartBatch();
            _updateCounter = 0;
            _removeCounter = 0;
        }

        public void Commit()
        {
            var key = _perfService.StartPerfCalc();
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Commiting discovery nodes, updates: {_updateCounter}, removes: {_removeCounter}");
            }
            _db.CommitBatch();
            _perfService.EndPerfCalc(key, "DiscoveryStorage commit");
        }

        public bool AnyPendingChange()
        {
            return _updateCounter > 0 || _removeCounter > 0;
        }

        //public async Task PersistNodesAsync(INodeLifecycleManager[] nodes)
        //{
        //    await Task.Run(() => PersistNodes(nodes));
        //}

        //public void PersistNodes(INodeLifecycleManager[] nodes)
        //{
        //    var existingKeys = _db.Keys;

        //    //add / update valid nodes
        //    for (var i = 0; i < nodes.Length; i++)
        //    {
        //        var manager = nodes[i];
        //        var node = manager.ManagedNode;
        //        var networkNode = new NetworkNode(node.Id.Bytes, node.Host, node.Port, node.Description, manager.NodeStats.NewPersistedNodeReputation);
        //        _db[networkNode.PublicKey.Bytes] = Rlp.Encode(networkNode).Bytes;
        //    }

        //    //delete removed nodes
        //    var nodesToRemove = existingKeys.Where(x => nodes.All(y => !Bytes.UnsafeCompare(x, y.ManagedNode.Id.Bytes))).ToArray();

        //    if (_logger.IsInfoEnabled)
        //    {
        //        _logger.Info($"Updated notes: {nodes.Length}\n{string.Join('\n', nodes.Select(x => x.ManagedNode.ToString()))}");
        //        _logger.Info($"Removed notes: {nodesToRemove.Length}");
        //    }

        //    if (!nodesToRemove.Any())
        //    {
        //        return;
        //    }

        //    for (var i = 0; i < nodesToRemove.Length; i++)
        //    {
        //        _db.Remove(nodesToRemove[i]);
        //    }
        //}

        private (Node, long) GetNode(byte[] networkNodeRaw)
        {
            var persistedNode = Rlp.Decode<NetworkNode>(networkNodeRaw);
            var node = _nodeFactory.CreateNode(persistedNode.NodeId, persistedNode.Host, persistedNode.Port);
            node.Description = persistedNode.Description;
            return (node, persistedNode.Reputation);
        }
    }
}