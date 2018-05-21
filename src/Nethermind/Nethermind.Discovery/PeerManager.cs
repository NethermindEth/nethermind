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


using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Discovery.Lifecycle;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Discovery.Stats;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;

namespace Nethermind.Discovery
{
    /// <summary>
    /// Responsible for periodically getting best peers from Dicovery Manager and updating SynchManager
    /// </summary>
    public class PeerManager : IPeerManager
    {
        private readonly IRlpxPeer _localPeer;
        private readonly ILogger _logger;
        private readonly IDiscoveryManager _discoveryManager;
        private readonly INodeFactory _nodeFactory;

        private readonly ConcurrentDictionary<PublicKey, Peer> _activePeers = new ConcurrentDictionary<PublicKey, Peer>();
        private readonly ConcurrentDictionary<PublicKey, Peer> _newPeers = new ConcurrentDictionary<PublicKey, Peer>();

        //TODO Timer to periodically check active peers and move new to active based on max size and compatibility - stats and capabilities + update peers in synchronization manager
        //TODO Remove active and synch on disconnect
        //TODO Update Stats on disconnect, other events
        //TODO Move Discover to Network
        //TODO update runner to run discovery

        public PeerManager(IRlpxPeer localPeer, IDiscoveryManager discoveryManager, ILogger logger, INodeFactory nodeFactory)
        {
            _localPeer = localPeer;
            _logger = logger;
            _nodeFactory = nodeFactory;
            _discoveryManager = discoveryManager;

            discoveryManager.NodeDiscovered += OnNodeDiscovered;
            localPeer.ConnectionInitialized += OnRemoteConnectionInitialized;
        }

        private void OnRemoteConnectionInitialized(object sender, ConnectionInitializedEventArgs eventArgs)
        {
            var id = eventArgs.Session.RemoteNodeId;
            if (_newPeers.TryGetValue(id, out Peer peer))
            {
                peer.Session = eventArgs.Session;
                peer.Session.PeerDisconnected += OnPeerDisconnected;
                return;
            }

            if (_logger.IsWarnEnabled)
            {
                _logger.Warn($"Connected to Peer via rlpx before discovery connection: {id.ToString(false)}");
            }

            var manager =_discoveryManager.GetNodeLifecycleManager(id);
            if (manager != null)
            {
                peer = new Peer(manager)
                {
                    Session = eventArgs.Session
                };
                _newPeers.AddOrUpdate(id, peer, (x, y) => y);
                peer.Session.PeerDisconnected += OnPeerDisconnected;
                return;
            }

            if (_logger.IsErrorEnabled)
            {
                _logger.Error($"Connected to Peer via rlpx for remote which was not received from discovery: {id.ToString(false)}");
            }
        }

        private void OnPeerDisconnected(object sender, DisconnectEventArgs e)
        {
            var peer = (IP2PSession) sender;
            peer.PeerDisconnected -= OnPeerDisconnected;

            if (_activePeers.TryRemove(peer.RemoteNodeId, out var removedPeer))
            {
                removedPeer.NodeStats.AddNodeStatsDisconnectEvent(e.DisconnectType, e.DisconnectReason);
            }

            if (_newPeers.TryRemove(peer.RemoteNodeId, out removedPeer))
            {
                removedPeer.NodeStats.AddNodeStatsDisconnectEvent(e.DisconnectType, e.DisconnectReason);
            }

            //TODO remove peer from synching
        }

        private void OnNodeDiscovered(object sender, NodeEventArgs nodeEventArgs)
        {
            var id = nodeEventArgs.Manager.ManagedNode.Id;
            if (_newPeers.ContainsKey(id) || _activePeers.ContainsKey(id))
            {
                return;
            }

            var peer = new Peer(nodeEventArgs.Manager);
            _newPeers.AddOrUpdate(id, peer, (x, y) => y);
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Adding newly discovered node to New collection {id.ToString(false)}@{nodeEventArgs.Manager.ManagedNode.Host}:{nodeEventArgs.Manager.ManagedNode.Port}");
            }
        }

        private class Peer
        {
            public Peer(INodeLifecycleManager manager)
            {
                Node = manager.ManagedNode;
                NodeLifecycleManager = manager;
                NodeStats = manager.NodeStats;
            }

            public Node Node { get; }
            public INodeLifecycleManager NodeLifecycleManager { get; }
            public INodeStats NodeStats { get; }
            public IP2PSession Session { get; set; }           
        }
    }
}