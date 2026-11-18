// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    /// <summary>
    /// This class should be split into multiple sources
    /// </summary>
    public sealed record NodesLoaderOptions(bool LoadBootnodesAsPeerCandidates = true);

    public class NodesLoader(
        INetworkConfig networkConfig,
        INodeStatsManager stats,
        [KeyFilter(DbNames.PeersDb)] INetworkStorage peerStorage,
        IEnode enode,
        ILogManager logManager,
        NodesLoaderOptions options,
        PeerAddressPolicy addressPolicy) : INodeSource
    {

        private readonly INetworkConfig _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        private readonly INodeStatsManager _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        private readonly INetworkStorage _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
        private readonly IEnode _enode = enode ?? throw new ArgumentNullException(nameof(enode));
        private readonly ILogger _logger = logManager?.GetClassLogger<NodesLoader>() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly NodesLoaderOptions _options = options ?? throw new ArgumentNullException(nameof(options));
        private readonly PeerAddressPolicy _addressPolicy = addressPolicy ?? ThrowMissingAddressPolicy(nameof(addressPolicy));

        public IAsyncEnumerable<Node> DiscoverNodes(CancellationToken cancellationToken)
        {
            List<Node> allPeers = [];
            LoadPeersFromDb(allPeers);

            if (!_networkConfig.OnlyStaticPeers && _options.LoadBootnodesAsPeerCandidates)
            {
                LoadConfigPeers(allPeers, _networkConfig.Bootnodes, n =>
                {
                    n.IsBootnode = true;
                    if (_logger.IsDebug) _logger.Debug($"Bootnode     : {n:hide}");
                });
            }

            LoadConfigPeers(allPeers, _networkConfig.StaticPeers, n =>
            {
                n.IsStatic = true;
                if (_logger.IsInfo) _logger.Info($"Static node  : {n:hide}");
            });

            IEnumerable<Node> combined = allPeers
                .Where(p => p.Id != _enode.PublicKey)
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

            Dictionary<IPAddress, int> addressCounts = [];
            foreach (NetworkNode networkNode in networkNodes.OrderByDescending(static n => n.Reputation))
            {
                if (peers.Count >= _networkConfig.MaxPersistedPeerCount) break;
                Node node;
                try
                {
                    if (networkNode.NodeId == _enode.PublicKey ||
                        !PersistedPeerPolicy.TryGetAddress(networkNode, out IPAddress address,
                            _addressPolicy.AllowedAddresses) ||
                        addressCounts.GetValueOrDefault(address) >= PersistedPeerPolicy.MaxPeersPerAddress) continue;
                    node = new Node(networkNode);
                    PersistedPeerPolicy.TryReserveAddress(addressCounts, address);
                }
                catch (Exception)
                {
                    _logger.DebugError($"peer could not be loaded for {networkNode:hide}");
                    continue;
                }

                if (_stats.GetStatsOrNull(node) is { } nodeStats)
                    nodeStats.CurrentPersistedNodeReputation = networkNode.Reputation;

                peers.Add(node);

                if (_logger.IsTrace) _logger.Trace($"Adding a new peer candidate {node:hide}");
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

        private void LoadConfigPeers(List<Node> peers, IEnumerable<NetworkNode> networkNodes, Action<Node> nodeUpdate)
        {
            foreach (NetworkNode networkNode in networkNodes)
            {
                if (!networkNode.IsEnode)
                {
                    continue;
                }

                Node node;
                try
                {
                    node = new Node(networkNode);
                }
                catch (Exception exception) when (exception is ArgumentException or FormatException or SocketException or InvalidOperationException)
                {
                    if (_logger.IsDebug) _logger.Debug($"Configured peer could not be loaded for {networkNode.NodeId}: {exception}");
                    continue;
                }
                nodeUpdate.Invoke(node);
                peers.Add(node);
            }
        }

        public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }

        [DoesNotReturn, StackTraceHidden]
        private static PeerAddressPolicy ThrowMissingAddressPolicy(string paramName) =>
            throw new ArgumentNullException(paramName);
    }
}
