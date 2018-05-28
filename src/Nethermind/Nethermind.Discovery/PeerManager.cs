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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Discovery.Stats;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
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
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly ISynchronizationManager _synchronizationManager;
        private Timer _peerTimer;

        private readonly ConcurrentDictionary<PublicKey, Peer> _activePeers = new ConcurrentDictionary<PublicKey, Peer>();
        private readonly ConcurrentDictionary<PublicKey, Peer> _newPeers = new ConcurrentDictionary<PublicKey, Peer>();

        //TODO Timer to periodically check active peers and move new to active based on max size and compatibility - stats and capabilities + update peers in synchronization manager
        //TODO Remove active and synch on disconnect
        //TODO Update Stats on disconnect, other events
        //TODO Move Discover to Network
        //TODO update runner to run discovery

        public PeerManager(IRlpxPeer localPeer, IDiscoveryManager discoveryManager, ILogger logger, INodeFactory nodeFactory, IDiscoveryConfigurationProvider configurationProvider, ISynchronizationManager synchronizationManager)
        {
            _localPeer = localPeer;
            _logger = logger;
            _nodeFactory = nodeFactory;
            _configurationProvider = configurationProvider;
            _synchronizationManager = synchronizationManager;
            _discoveryManager = discoveryManager;

            discoveryManager.NodeDiscovered += OnNodeDiscovered;
            localPeer.ConnectionInitialized += OnRemoteConnectionInitialized;
        }

        public void StartPeerTimer()
        {
            if (_logger.IsInfoEnabled)
            {
                _logger.Info("Starting peer timer");
            }

            _peerTimer = new Timer(_configurationProvider.ActivePeerUpdateInterval);
            _peerTimer.Elapsed += async (sender, e) => await RunPeerUpdate();
            _peerTimer.Start();
        }

        public void StopPeerTimer()
        {
            try
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info("Stopping peer timer");
                }
                _peerTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during peer timer stop", e);
            }
        }

        public async Task RunPeerUpdate()
        {
            if (_activePeers.Count >= _configurationProvider.ActivePeersMaxCount)
            {
                return;
            }

            var availibleActiveCount = _configurationProvider.ActivePeersMaxCount - _activePeers.Count;
            var allCandidates = _newPeers.Where(x => x.Value.NodeStats.DidEventHappen(NodeStatsEvent.Eth62Initialized)).ToArray();
            var newActiveCount = 0;
            var disconnectedCount = 0;
            for (var i = 0; i < allCandidates.Length; i++)
            {
                var candidate = allCandidates[i];
                
                if (availibleActiveCount <= 0)
                {
                    //TODO confirm we want to do this disconnect
                    await candidate.Value.Session.InitiateDisconnectAsync(DisconnectReason.TooManyPeers);
                    disconnectedCount++;
                    continue;
                }

                _newPeers.TryRemove(candidate.Key, out _);
                _activePeers.AddOrUpdate(candidate.Key, candidate.Value, (x, y) => y);
                await _synchronizationManager.AddPeer(candidate.Value.Eth62ProtocolHandler);
                availibleActiveCount--;
                newActiveCount++;
            }

            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Added {newActiveCount} active peers. Disconnected: {disconnectedCount}");
            }
        }

        public IReadOnlyCollection<Peer> NewPeers => _newPeers.Values.ToArray();
        public IReadOnlyCollection<Peer> ActivePeers => _activePeers.Values.ToArray();

        private void OnRemoteConnectionInitialized(object sender, ConnectionInitializedEventArgs eventArgs)
        {
            var id = eventArgs.Session.RemoteNodeId;
            if (_newPeers.TryGetValue(id, out Peer peer))
            {
                peer.Session = eventArgs.Session;
                peer.Session.PeerDisconnected += OnPeerDisconnected;
                peer.Session.ProtocolInitialized += async (s, e) => await OnProtocolInitialized(s, e);
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

                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Remote connection initialized {id.ToString(false)}");
                }

                return;
            }

            if (_logger.IsErrorEnabled)
            {
                _logger.Error($"Connected to Peer via rlpx for remote which was not received from discovery: {id.ToString(false)}");
            }
        }

        private async Task OnProtocolInitialized(object sender, ProtocolInitializedEventArgs e)
        {
            var session = (IP2PSession)sender;
            if (_newPeers.TryGetValue(session.RemoteNodeId, out var peer))
            {
                switch (e.ProtocolHandler)
                {
                    case P2PProtocolHandler _:
                        var result = await ValidateProtocol(Protocol.P2P, peer, e);
                        if (!result)
                        {
                            return;
                        }
                        peer.NodeStats.AddNodeStatsEvent(NodeStatsEvent.P2PInitialized);
                        break;
                    case Eth62ProtocolHandler _:
                        result = await ValidateProtocol(Protocol.Eth, peer, e);
                        if (!result)
                        {
                            return;
                        }
                        peer.NodeStats.AddNodeStatsEvent(NodeStatsEvent.Eth62Initialized);
                        break;
                }
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Protocol Initialized: {session.RemoteNodeId.ToString(false)}, {e.ProtocolHandler.GetType().Name}");
                }
            }
            else
            {
                if (_logger.IsErrorEnabled)
                {
                    _logger.Error($"Protocol initialized for peer not present in new collection, id: {session.RemoteNodeId.ToString(false)}.");
                }
            }
        }

        private async Task<bool> ValidateProtocol(string protocol, Peer peer, ProtocolInitializedEventArgs eventArgs)
        {
            switch (protocol)
            {
                case Protocol.P2P:
                    var args = (P2PProtocolInitializedEventArgs)eventArgs;
                    if (args.P2PVersion < 4 || args.P2PVersion > 5)
                    {
                        if (_logger.IsInfoEnabled)
                        {
                            _logger.Info($"Initiating disconnect, incorrect P2PVersion: {args.P2PVersion}, id: {peer.Node.Id.ToString(false)}");
                        }
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.IncompatibleP2PVersion);
                        return false;
                    }

                    if (!args.Capabilities.Any(x => x.ProtocolCode == Protocol.Eth && x.Version == 62))
                    {
                        if (_logger.IsInfoEnabled)
                        {
                            _logger.Info($"Initiating disconnect, no Eth62 capability, supported capabilities: [{string.Join(",", args.Capabilities.Select(x => $"{x.ProtocolCode}v{x.Version}"))}], id: {peer.Node.Id.ToString(false)}");
                        }
                        //TODO confirm disconnect reason
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.Other);
                        return false;
                    }
                    break;
                case Protocol.Eth:
                    var ethArgs = (Eth62ProtocolInitializedEventArgs)eventArgs;
                    if (ethArgs.ChainId != _synchronizationManager.ChainId)
                    {
                        if (_logger.IsInfoEnabled)
                        {
                            _logger.Info($"Initiating disconnect, different chainId: {ethArgs.ChainId}, our chainId: {_synchronizationManager.ChainId}, peer id: {peer.Node.Id.ToString(false)}");
                        }
                        //TODO confirm disconnect reason
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.Other);
                        return false;
                    }
                    break;
            }
            return true;
        }

        private void OnPeerDisconnected(object sender, DisconnectEventArgs e)
        {
            var peer = (IP2PSession) sender;
            peer.PeerDisconnected -= OnPeerDisconnected;

            if (_activePeers.TryRemove(peer.RemoteNodeId, out var removedPeer))
            {
                removedPeer.NodeStats.AddNodeStatsDisconnectEvent(e.DisconnectType, e.DisconnectReason);
                _synchronizationManager.RemovePeer(removedPeer.Eth62ProtocolHandler);
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Removing Active Peer on disconnect {peer.RemoteNodeId.ToString(false)}");
                }
            }

            if (_newPeers.TryRemove(peer.RemoteNodeId, out removedPeer))
            {
                removedPeer.NodeStats.AddNodeStatsDisconnectEvent(e.DisconnectType, e.DisconnectReason);
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Removing New Peer on disconnect {peer.RemoteNodeId.ToString(false)}");
                }
            }
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
    }
}