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
using System.Linq;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public class PeerLoader : IPeerLoader
    {
        private readonly INetworkConfig _networkConfig;
        private readonly INodeStatsManager _stats;
        private readonly INetworkStorage _peerStorage;
        private readonly ILogger _logger;

        public PeerLoader(INetworkConfig networkConfig, INodeStatsManager stats, INetworkStorage peerStorage, ILogManager logManager)
        {
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _logger = logManager.GetClassLogger();
        }

        public List<Peer> LoadPeers(IEnumerable<NetworkNode> staticNodes = null)
        {
            List<Peer> allPeers = new List<Peer>();
            LoadPeersFromDb(allPeers);
            LoadConfigPeers(allPeers, _networkConfig.Bootnodes, n =>
            {
                n.IsBootnode = true;
                if (_logger.IsInfo) _logger.Info($"Bootnode     : {n}");
            });
            LoadConfigPeers(allPeers, _networkConfig.StaticPeers, n =>
            {
                n.IsStatic = true;
                if (_logger.IsInfo) _logger.Info($"Static node  : {n}");
            });
            LoadConfigPeers(allPeers, _networkConfig.TrustedPeers, n =>
            {
                n.IsTrusted = true;
                if (_logger.IsInfo) _logger.Info($"Trusted node : {n}");
            });
            if (!(staticNodes is null))
            {
                LoadConfigPeers(allPeers, staticNodes, n =>
                {
                    n.IsStatic = true;
                    if (_logger.IsInfo) _logger.Info($"Static node : {n}");
                });
            }

            return allPeers;
        }

        private void LoadPeersFromDb(List<Peer> peers)
        {
            if (!_networkConfig.IsPeersPersistenceOn)
            {
                return;
            }

            var networkNodes = _peerStorage.GetPersistedNodes();

            if (_logger.IsInfo) _logger.Info($"Initializing persisted peers: {networkNodes.Length}.");

            foreach (var persistedPeer in networkNodes)
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
                
                var nodeStats = _stats.GetOrAdd(node);
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
            foreach (var networkNode in networkNodes)
            {
                var node = new Node(networkNode.NodeId, networkNode.Host, networkNode.Port);
                nodeUpdate.Invoke(node);
                peers.Add(new Peer(node));
            }
        }
    }
}