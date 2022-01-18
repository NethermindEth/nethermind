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
using System.Linq;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    /// <summary>
    /// This class should be split into multiple sources
    /// </summary>
    public class NodesLoader : INodeSource
    {
        private readonly INetworkConfig _networkConfig;
        private readonly INodeStatsManager _stats;
        private readonly INetworkStorage _peerStorage;
        private readonly IRlpxHost _rlpxHost;
        private readonly ILogger _logger;

        public NodesLoader(
            INetworkConfig networkConfig,
            INodeStatsManager stats,
            INetworkStorage peerStorage,
            IRlpxHost rlpxHost,
            ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _rlpxHost = rlpxHost ?? throw new ArgumentNullException(nameof(rlpxHost));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        }

        public List<Node> LoadInitialList()
        {
            List<Node> allPeers = new();
            LoadPeersFromDb(allPeers);
            
            LoadConfigPeers(allPeers, _networkConfig.Bootnodes, n =>
            {
                n.IsBootnode = true;
                if (_logger.IsDebug) _logger.Debug($"Bootnode     : {n}");
            });
            
            LoadConfigPeers(allPeers, _networkConfig.StaticPeers, n =>
            {
                n.IsStatic = true;
                if (_logger.IsInfo) _logger.Info($"Static node  : {n}");
            });

            return allPeers
                .Where(p => p.Id != _rlpxHost.LocalNodeId)
                .Where(p => !_networkConfig.OnlyStaticPeers || p.IsStatic).ToList();
        }

        private void LoadPeersFromDb(List<Node> peers)
        {
            if (!_networkConfig.IsPeersPersistenceOn)
            {
                return;
            }

            NetworkNode[] networkNodes = _peerStorage.GetPersistedNodes();

            if (_logger.IsDebug) _logger.Debug($"Initializing persisted peers: {networkNodes.Length}.");

            foreach (NetworkNode networkNode in networkNodes)
            {
                Node node;
                try
                {
                    node = new Node(networkNode);
                }
                catch (Exception)
                {
                    if(_logger.IsDebug) _logger.Error($"ERROR/DEBUG peer could not be loaded for {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}");
                    continue;
                }
                
                INodeStats nodeStats = _stats.GetOrAdd(node);
                nodeStats.CurrentPersistedNodeReputation = networkNode.Reputation;

                peers.Add(node);

                if (_logger.IsTrace) _logger.Trace($"Adding a new peer candidate {node}");
            }
        }

        private void LoadConfigPeers(List<Node> peers, string? enodesString, Action<Node> nodeUpdate)
        {
            if (enodesString == null || !enodesString.Any())
            {
                return;
            }

            LoadConfigPeers(peers, NetworkNode.ParseNodes(enodesString, _logger), nodeUpdate);
        }

        private static void LoadConfigPeers(List<Node> peers, IEnumerable<NetworkNode> networkNodes, Action<Node> nodeUpdate)
        {
            foreach (NetworkNode networkNode in networkNodes)
            {
                Node node = new(networkNode);
                nodeUpdate.Invoke(node);
                peers.Add(node);
            }
        }

        public event EventHandler<NodeEventArgs>? NodeAdded { add { } remove { } }
        
        public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
    }
}
