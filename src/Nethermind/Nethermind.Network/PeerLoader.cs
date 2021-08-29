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
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public class PeerLoader : IPeerLoader
    {
        private readonly INetworkConfig _networkConfig;
        private readonly IDiscoveryConfig _discoveryConfig;
        private readonly INodeStatsManager _stats;
        private readonly INetworkStorage _peerStorage;
        private readonly ILogger _logger;

        public PeerLoader(INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig, INodeStatsManager stats, INetworkStorage peerStorage, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
        }

        public List<Peer> LoadPeers(IEnumerable<NetworkNode> staticNodes = null)
        {
            List<Peer> allPeers = new List<Peer>();
            LoadPeersFromDb(allPeers);
            
            LoadConfigPeers(allPeers, _discoveryConfig.Bootnodes, n =>
            {
                n.IsBootnode = true;
                if (_logger.IsDebug) _logger.Debug($"Bootnode     : {n}");
            });
            
            LoadConfigPeers(allPeers, _networkConfig.StaticPeers, n =>
            {
                n.IsStatic = true;
                if (_logger.IsInfo) _logger.Info($"Static node  : {n}");
            });

            if (!(staticNodes is null))
            {
                LoadConfigPeers(allPeers, staticNodes, n =>
                {
                    n.IsStatic = true;
                    if (_logger.IsInfo) _logger.Info($"Static node : {n}");
                });
            }

            return allPeers.Where(p => !_networkConfig.OnlyStaticPeers || p.Node.IsStatic).ToList();
        }

        private void LoadPeersFromDb(List<Peer> peers)
        {
            if (!_networkConfig.IsPeersPersistenceOn)
            {
                return;
            }

            NetworkNode[] networkNodes = _peerStorage.GetPersistedNodes();

            if (_logger.IsDebug) _logger.Debug($"Initializing persisted peers: {networkNodes.Length}.");

            foreach (NetworkNode persistedPeer in networkNodes)
            {
                Node node;
                try
                {
                    node = new Node(persistedPeer.NodeId, persistedPeer.Host, persistedPeer.Port);
                }
                catch (Exception)
                {
                    if(_logger.IsDebug) _logger.Error($"ERROR/DEBUG peer could not be loaded for {persistedPeer.NodeId}@{persistedPeer.Host}:{persistedPeer.Port}");
                    continue;
                }
                
                INodeStats nodeStats = _stats.GetOrAdd(node);
                nodeStats.CurrentPersistedNodeReputation = persistedPeer.Reputation;

                peers.Add(new Peer(node));

                if (_logger.IsTrace) _logger.Trace($"Adding a new peer candidate {node}");
            }
        }

        private void LoadConfigPeers(List<Peer> peers, string enodesString, Action<Node> nodeUpdate)
        {
            if (enodesString == null || !enodesString.Any())
            {
                return;
            }

            LoadConfigPeers(peers, NetworkNode.ParseNodes(enodesString, _logger), nodeUpdate);
        }

        private void LoadConfigPeers(List<Peer> peers, IEnumerable<NetworkNode> networkNodes, Action<Node> nodeUpdate)
        {
            foreach (NetworkNode networkNode in networkNodes)
            {
                Node node = new Node(networkNode.NodeId, networkNode.Host, networkNode.Port);
                nodeUpdate.Invoke(node);
                peers.Add(new Peer(node));
            }
        }
    }
}
