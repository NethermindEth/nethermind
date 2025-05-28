// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Db;
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
            [KeyFilter(DbNames.PeersDb)] INetworkStorage peerStorage,
            IRlpxHost rlpxHost,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _rlpxHost = rlpxHost ?? throw new ArgumentNullException(nameof(rlpxHost));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        }

        public IAsyncEnumerable<Node> DiscoverNodes(CancellationToken cancellationToken)
        {
            List<Node> allPeers = new();
            LoadPeersFromDb(allPeers);

            if (!_networkConfig.OnlyStaticPeers)
            {
                LoadConfigPeers(allPeers, _networkConfig.Bootnodes, n =>
                {
                    n.IsBootnode = true;
                    if (_logger.IsDebug) _logger.Debug($"Bootnode     : {n}");
                });
            }

            LoadConfigPeers(allPeers, _networkConfig.StaticPeers, n =>
            {
                n.IsStatic = true;
                if (_logger.IsInfo) _logger.Info($"Static node  : {n}");
            });

            IEnumerable<Node> combined = allPeers
                .Where(p => p.Id != _rlpxHost.LocalNodeId)
                .Where(p => !_networkConfig.OnlyStaticPeers || p.IsStatic);

            return combined.ToAsyncEnumerable();
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
                    if (_logger.IsDebug) _logger.Error($"ERROR/DEBUG peer could not be loaded for {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}");
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
            if (enodesString is null || enodesString.Length == 0)
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


        public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
    }
}
