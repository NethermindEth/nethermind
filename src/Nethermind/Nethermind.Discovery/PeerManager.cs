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
    /// </summary>
    public class PeerManager : IPeerManager
    {
        private readonly IRlpxPeer _localPeer;
        private readonly ILogger _logger;
        private readonly IDiscoveryManager _discoveryManager;
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly ISynchronizationManager _synchronizationManager;
        private readonly INodeStatsProvider _nodeStatsProvider;
        private readonly IPeerStorage _peerStorage;
        private Timer _activePeersTimer;
        private Timer _peerPersistanceTimer;
        private readonly bool _isDiscoveryEnabled;
        private int _logCounter = 0;

        private readonly ConcurrentDictionary<PublicKey, Peer> _activePeers = new ConcurrentDictionary<PublicKey, Peer>();
        private readonly ConcurrentDictionary<PublicKey, Peer> _newPeers = new ConcurrentDictionary<PublicKey, Peer>();

        //TODO Timer to periodically check active peers and move new to active based on max size and compatibility - stats and capabilities + update peers in synchronization manager
        //TODO Remove active and synch on disconnect
        //TODO Update Stats on disconnect, other events
        //TODO Move Discover to Network
        //TODO update runner to run discovery

        public PeerManager(IRlpxPeer localPeer, IDiscoveryManager discoveryManager, ILogger logger, IDiscoveryConfigurationProvider configurationProvider, ISynchronizationManager synchronizationManager, INodeStatsProvider nodeStatsProvider, IPeerStorage peerStorage)
        {
            _localPeer = localPeer;
            _logger = logger;
            _configurationProvider = configurationProvider;
            _synchronizationManager = synchronizationManager;
            _nodeStatsProvider = nodeStatsProvider;
            _discoveryManager = discoveryManager;
            _isDiscoveryEnabled = _discoveryManager != null;

            if (_isDiscoveryEnabled)
            {
                discoveryManager.NodeDiscovered += OnNodeDiscovered;
            }
            localPeer.ConnectionInitialized += OnRemoteConnectionInitialized;
            _peerStorage = peerStorage;
            _peerStorage.StartBatch();
        }

        public void Start()
        {
            //Step 1 - load configured trusted peers
            AddTrustedPeers();

            //Step 2 - read peers from db
            AddPersistedPeers();

            //Step 3 - start active peers timer
            StartActivePeersTimer();

            //Step 4 - start peer persistance timer
            StartPeerPersistanceTimer();
        }

        public void Stop()
        {
            StopActivePeersTimer();
            StopPeerPersistanceTimer();
        }
        
        public async Task RunPeerUpdate()
        {
            var availibleActiveCount = _configurationProvider.ActivePeersMaxCount - _activePeers.Count;
            if (availibleActiveCount <= 0)
            {
                return;
            }

            var candidates = _newPeers.OrderBy(x => x.Value.NodeStats.IsTrustedPeer)
                .ThenByDescending(x => x.Value.NodeStats.CurrentNodeReputation)
                .Take(availibleActiveCount).ToArray();

            var newActiveNodes = 0;
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];

                _newPeers.TryRemove(candidate.Key, out _);
                if (!_activePeers.TryAdd(candidate.Key, candidate.Value))
                {
                    if (_logger.IsErrorEnabled)
                    {
                        _logger.Error($"Active peer was already added to collection: {candidate.Key.ToString(false)}");
                        continue;
                    }
                }

                var result = await InitializePeerConnection(candidate.Value);
                if (result)
                {
                    newActiveNodes++;
                }
            }

            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"RunPeerUpdate | Tried: {candidates.Length}, Added {newActiveNodes} active peers, current new peers: {_newPeers.Count}");

                if (_logCounter % 120 == 0)
                {
                    //TODO add info about cababilities, clientId, etc to NodeStats and print it here
                    _logger.Info($"All active peers: \n{string.Join('\n', ActivePeers.Select(x => $"{x.Node.ToString()} | P2PInitialized: {x.NodeStats.DidEventHappen(NodeStatsEvent.P2PInitialized)} | Eth62Initialized: {x.NodeStats.DidEventHappen(NodeStatsEvent.Eth62Initialized)}"))}");
                }

                _logCounter++;
            }
        }

        private async Task<bool> InitializePeerConnection(Peer candidate)
        {
            try
            {
                await _localPeer.ConnectAsync(candidate.Node.Id, candidate.Node.Host, candidate.Node.Port);
                return true;
            }
            catch (NetworkingException)
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Cannot connect to peer, removing from active collection: {candidate.Node.Id.ToString(false)}");
                }

                //TODO think about reconnection logic, e.g. change stats and try again after some time
                //Removing from active peers
                _activePeers.TryRemove(candidate.Node.Id, out _);
                return false;
            }
            catch (Exception e)
            {
                if (_logger.IsErrorEnabled)
                {
                    _logger.Error($"Error trying to initiate connetion with peer: {candidate.Node.Id.ToString(false)}", e);
                }
                return false;
            }
        }

        public IReadOnlyCollection<Peer> NewPeers => _newPeers.Values.ToArray();
        public IReadOnlyCollection<Peer> ActivePeers => _activePeers.Values.ToArray();

        private void AddPersistedPeers()
        {
            if (!_configurationProvider.IsPeersPersistenceOn)
            {
                return;
            }

            var peers = _peerStorage.GetPersistedPeers();

            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Initializing persisted peers: {peers.Length}.");
            }

            foreach (var persistedPeer in peers)
            {
                if (_newPeers.ContainsKey(persistedPeer.Node.Id) || _activePeers.ContainsKey(persistedPeer.Node.Id))
                {
                    //Peer already added by discovery
                    continue;
                }

                var nodeStats = _nodeStatsProvider.GetNodeStats(persistedPeer.Node.Id);
                nodeStats.CurrentPersistedNodeReputation = persistedPeer.PersistedReputation;

                var peer = new Peer(persistedPeer.Node, nodeStats);
                if (!_newPeers.TryAdd(persistedPeer.Node.Id, peer))
                {
                    continue;
                }

                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Adding persisted peer to New collection {persistedPeer.Node.Id.ToString(false)}@{persistedPeer.Node.Host}:{persistedPeer.Node.Port}");
                }
            }
        }

        private void AddTrustedPeers()
        {
            var trustedPeers = _configurationProvider.TrustedPeers;
            if (trustedPeers == null || !trustedPeers.Any())
            {
                return;
            }

            if (_logger.IsInfoEnabled)
            {
                _logger.Info("Initializing trusted peers.");
            }

            foreach (var trustedPeer in trustedPeers)
            {
                var nodeStats = _nodeStatsProvider.GetNodeStats(trustedPeer.Id);
                nodeStats.IsTrustedPeer = true;

                var peer = new Peer(trustedPeer, nodeStats);
                if (!_newPeers.TryAdd(trustedPeer.Id, peer))
                {
                    continue;
                }

                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Adding trusted peer to New collection {trustedPeer.Id.ToString(false)}@{trustedPeer.Host}:{trustedPeer.Port}");
                }
            }
        }

        private void OnRemoteConnectionInitialized(object sender, ConnectionInitializedEventArgs eventArgs)
        {
            var id = eventArgs.Session.RemoteNodeId;
            if (!_activePeers.TryGetValue(id, out Peer peer))
            {
                if (_logger.IsErrorEnabled)
                {
                    _logger.Error($"Initiated rlpx connection with Peer without adding it to Active collection: {id.ToString(false)}");
                }
                return;
            }

            peer.Session = eventArgs.Session;
            peer.Session.PeerDisconnected += OnPeerDisconnected;
            peer.Session.ProtocolInitialized += async (s, e) => await OnProtocolInitialized(s, e);

            if (!_isDiscoveryEnabled || peer.NodeLifecycleManager != null)
            {
                return;
            }
            
            //In case peer was initiated outside of discovery and discovery is enabled, we are adding it to discovery for future use (e.g. trusted peer)
            var manager =_discoveryManager.GetNodeLifecycleManager(peer.Node);
            peer.NodeLifecycleManager = manager;
        }

        private async Task OnProtocolInitialized(object sender, ProtocolInitializedEventArgs e)
        {
            var session = (IP2PSession)sender;
            if (_activePeers.TryGetValue(session.RemoteNodeId, out var peer))
            {
                switch (e.ProtocolHandler)
                {
                    case P2PProtocolHandler _:
                        //TODO test log
                        _logger.Info($"ETH62TESTCLIENTID: {((P2PProtocolInitializedEventArgs)e).ClientId}");
                        var result = await ValidateProtocol(Protocol.P2P, peer, e);
                        if (!result)
                        {
                            return;
                        }
                        peer.NodeStats.AddNodeStatsEvent(NodeStatsEvent.P2PInitialized);
                        break;
                    case Eth62ProtocolHandler ethProtocolhandler:
                        result = await ValidateProtocol(Protocol.Eth, peer, e);
                        if (!result)
                        {
                            return;
                        }
                        peer.NodeStats.AddNodeStatsEvent(NodeStatsEvent.Eth62Initialized);
                        peer.SynchronizationPeer = ethProtocolhandler;

                        if (_logger.IsInfoEnabled)
                        {
                            _logger.Info($"Eth62 initialized, adding sync peer: {peer.Node.Id.ToString(false)}");
                        }
                        //Add peer to the storage and to sync manager
                        _peerStorage.UpdatePeers(new []{peer});
                        await _synchronizationManager.AddPeer(ethProtocolhandler);

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
                    _logger.Error($"Protocol initialized for peer not present in active collection, id: {session.RemoteNodeId.ToString(false)}.");
                }
            }
        }

        private async Task<bool> ValidateProtocol(string protocol, Peer peer, ProtocolInitializedEventArgs eventArgs)
        {
            //TODO add validation for clientId - e.g. get only ethereumJ clients
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

                    //if (args.ClientId.Contains("Geth") || args.ClientId.Contains("Parity") || args.ClientId.Contains("Gnekonium"))
                    //{
                    //    if (_logger.IsInfoEnabled)
                    //    {
                    //        _logger.Info($"Initiating disconnect, rejecting client: {args.ClientId}, id: {peer.Node.Id.ToString(false)}");
                    //    }
                    //    await peer.Session.InitiateDisconnectAsync(DisconnectReason.Other);
                    //    return false;
                    //}
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
                if (removedPeer.SynchronizationPeer != null)
                {
                    _synchronizationManager.RemovePeer(removedPeer.SynchronizationPeer);
                }
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
            if (!_newPeers.TryAdd(id, peer))
            {
                return;
            }
            
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Adding newly discovered node to New collection {id.ToString(false)}@{nodeEventArgs.Manager.ManagedNode.Host}:{nodeEventArgs.Manager.ManagedNode.Port}");
            }
        }

        private void StartActivePeersTimer()
        {
            if (_logger.IsInfoEnabled)
            {
                _logger.Info("Starting active peers timer");
            }

            _activePeersTimer = new Timer(_configurationProvider.ActivePeerUpdateInterval) {AutoReset = false};
            _activePeersTimer.Elapsed += async (sender, e) =>
            {
                _activePeersTimer.Enabled = false;
                await RunPeerUpdate();
                _activePeersTimer.Enabled = true;
            };

            _activePeersTimer.Start();
        }

        private void StopActivePeersTimer()
        {
            try
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info("Stopping active peers timer");
                }
                _activePeersTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during active peers timer stop", e);
            }
        }

        private void StartPeerPersistanceTimer()
        {
            if (_logger.IsInfoEnabled)
            {
                _logger.Info("Starting peer persistance timer");
            }

            _peerPersistanceTimer = new Timer(_configurationProvider.PeersPersistanceInterval) {AutoReset = false};
            _peerPersistanceTimer.Elapsed += async (sender, e) =>
            {
                _peerPersistanceTimer.Enabled = false;
                await Task.Run(() => RunPeerCommit());
                _peerPersistanceTimer.Enabled = true;
            };

            _peerPersistanceTimer.Start();
        }

        private void StopPeerPersistanceTimer()
        {
            try
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info("Stopping peer persistance timer");
                }
                _peerPersistanceTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during peer persistance timer stop", e);
            }
        }

        private void RunPeerCommit()
        {
            if (_logger.IsInfoEnabled)
            {
                _logger.Info("Running peers commit process.");
            }
            
            _peerStorage.Commit();
            _peerStorage.StartBatch();
        }
    }
}