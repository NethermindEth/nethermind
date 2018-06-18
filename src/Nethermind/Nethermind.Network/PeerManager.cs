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
using Nethermind.Core.Model;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Stats;

namespace Nethermind.Network
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
        private readonly INodeFactory _nodeFactory;
        private Timer _activePeersTimer;
        private Timer _peerPersistanceTimer;
        private Timer _pingTimer;
        private readonly bool _isDiscoveryEnabled;
        private int _logCounter = 1;
        private bool _isInitialized = false;
        private bool _isPeerUpdateInProgress = false;
        private readonly object _isPeerUpdateInProgressLock = new object();
        private readonly IPerfService _perfService;

        private readonly ConcurrentDictionary<NodeId, Peer> _activePeers = new ConcurrentDictionary<NodeId, Peer>();
        private readonly ConcurrentDictionary<NodeId, Peer> _candidatePeers = new ConcurrentDictionary<NodeId, Peer>();

        //TODO Timer to periodically check active peers and move new to active based on max size and compatibility - stats and capabilities + update peers in synchronization manager
        //TODO Remove active and synch on disconnect
        //TODO Update Stats on disconnect, other events
        //TODO Move Discover to Network
        //TODO update runner to run discovery

        public PeerManager(IRlpxPeer localPeer, IDiscoveryManager discoveryManager, ILogger logger, IDiscoveryConfigurationProvider configurationProvider, ISynchronizationManager synchronizationManager, INodeStatsProvider nodeStatsProvider, IPeerStorage peerStorage, IPerfService perfService, INodeFactory nodeFactory)
        {
            _localPeer = localPeer;
            _logger = logger;
            _configurationProvider = configurationProvider;
            _synchronizationManager = synchronizationManager;
            _nodeStatsProvider = nodeStatsProvider;
            _discoveryManager = discoveryManager;
            _perfService = perfService;
            _nodeFactory = nodeFactory;
            _isDiscoveryEnabled = _discoveryManager != null;

            if (_isDiscoveryEnabled)
            {
                discoveryManager.NodeDiscovered += async (s, e) => await OnNodeDiscovered(s, e);
            }
            localPeer.ConnectionInitialized += OnRemoteConnectionInitialized;
            _peerStorage = peerStorage;
            _peerStorage.StartBatch();
        }

        public async Task Start()
        {
            //Step 1 - load configured trusted peers
            AddTrustedPeers();

            //Step 2 - read peers from db
            AddPersistedPeers();

            //Step 3 - start active peers timer - timer is needed to support reconnecting, event based connection is also supported
            if (_configurationProvider.IsActivePeerTimerEnabled)
            {
                StartActivePeersTimer();
            }
            
            //Step 4 - start peer persistance timer
            StartPeerPersistanceTimer();

            //Step 5 - start ping timer
            StartPingTimer();

            //Step 6 - Running initial peer update
            await RunPeerUpdate();

            _isInitialized = true;
        }

        public Task Stop()
        {
            if (_configurationProvider.IsActivePeerTimerEnabled)
            {
                StopActivePeersTimer();
            }

            StopPeerPersistanceTimer();
            StopPingTimer();

            return Task.CompletedTask;
        }
        
        public async Task RunPeerUpdate()
        {
            lock (_isPeerUpdateInProgressLock)
            {
                if (_isPeerUpdateInProgress)
                {
                    return;
                }

                _isPeerUpdateInProgress = true;
            }

            var key = _perfService.StartPerfCalc();

            var availibleActiveCount = _configurationProvider.ActivePeersMaxCount - _activePeers.Count;
            if (availibleActiveCount <= 0)
            {
                return;
            }

            var candidates = _candidatePeers.Where(x => !_activePeers.ContainsKey(x.Key) && CheckLastDisconnect(x.Value))
                .OrderBy(x => x.Value.NodeStats.IsTrustedPeer)
                .ThenByDescending(x => x.Value.NodeStats.CurrentNodeReputation).ToArray();

            var newActiveNodes = 0;
            var tryCount = 0;
            for (var i = 0; i < candidates.Length; i++)
            {
                if (newActiveNodes >= availibleActiveCount)
                {
                    break;
                }

                var candidate = candidates[i];
                tryCount++;

                if (!_activePeers.TryAdd(candidate.Key, candidate.Value))
                {
                    if (_logger.IsErrorEnabled)
                    {
                        _logger.Error($"Active peer was already added to collection: {candidate.Key}");
                    }
                }

                var result = await InitializePeerConnection(candidate.Value);
                if (!result)
                {
                    _activePeers.TryRemove(candidate.Key, out _);
                    continue;
                }

                newActiveNodes++;                
            }

            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"{nameof(RunPeerUpdate)} | Tried: {tryCount}, Added {newActiveNodes} active peers, current candidate peers: {_candidatePeers.Count}, current active peers: {_activePeers.Count}");

                if (_logCounter % 5 == 0)
                {
                    string nl = Environment.NewLine;
                    _logger.Debug($"{nl}{nl}All active peers: {nl}{string.Join(nl, ActivePeers.Select(x => $"{x.Node.ToString()} | P2PInitialized: {x.NodeStats.DidEventHappen(NodeStatsEvent.P2PInitialized)} | Eth62Initialized: {x.NodeStats.DidEventHappen(NodeStatsEvent.Eth62Initialized)} | ClientId: {x.NodeStats.NodeDetails.ClientId}"))} {nl}{nl}");
                }

                _logCounter++;
            }

            _perfService.EndPerfCalc(key, "RunPeerUpdate");
            _isPeerUpdateInProgress = false;
        }

        private bool CheckLastDisconnect(Peer peer)
        {
            if (!peer.NodeStats.LastDisconnectTime.HasValue)
            {
                return true;
            }
            var lastDisconnectTimePassed = DateTime.Now.Subtract(peer.NodeStats.LastDisconnectTime.Value).TotalMilliseconds;
            var result = lastDisconnectTimePassed > _configurationProvider.DisconnectDelay;
            if (!result && _logger.IsInfoEnabled)
            {
                _logger.Info($"Skipping connection to peer, due to disconnect delay, time from last disconnect: {lastDisconnectTimePassed}, delay: {_configurationProvider.DisconnectDelay}, peer: {peer.Node.Id}");
            }

            return result;
        }

        private async Task<bool> InitializePeerConnection(Peer candidate)
        {
            try
            {
                await _localPeer.ConnectAsync(candidate.Node.Id, candidate.Node.Host, candidate.Node.Port);
                return true;
            }
            catch (NetworkingException ex)
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Warn($"Cannot connect to Peer [{ex.NetwokExceptionType.ToString()}]: {candidate.Node.Id}");
                }
                return false;
            }
            catch (Exception e)
            {
                if (_logger.IsErrorEnabled)
                {
                    _logger.Error($"Error trying to initiate connetion with peer: {candidate.Node.Id}", e);
                }
                return false;
            }
        }

        public IReadOnlyCollection<Peer> CandidatePeers => _candidatePeers.Values.ToArray();
        public IReadOnlyCollection<Peer> ActivePeers => _activePeers.Values.ToArray();

        public bool IsPeerConnected(NodeId peerId)
        {
            return _activePeers.ContainsKey(peerId);
        }

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
                if (_candidatePeers.ContainsKey(persistedPeer.Node.Id))
                {
                    continue;
                }

                var nodeStats = _nodeStatsProvider.GetNodeStats(persistedPeer.Node.Id);
                nodeStats.CurrentPersistedNodeReputation = persistedPeer.PersistedReputation;

                var peer = new Peer(persistedPeer.Node, nodeStats);
                if (!_candidatePeers.TryAdd(persistedPeer.Node.Id, peer))
                {
                    continue;
                }

                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Adding persisted peer to New collection {persistedPeer.Node.Id}@{persistedPeer.Node.Host}:{persistedPeer.Node.Port}");
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
                if (!_candidatePeers.TryAdd(trustedPeer.Id, peer))
                {
                    continue;
                }

                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Adding trusted peer to New collection {trustedPeer.Id}@{trustedPeer.Host}:{trustedPeer.Port}");
                }
            }
        }

        private void OnRemoteConnectionInitialized(object sender, ConnectionInitializedEventArgs eventArgs)
        {
            if (eventArgs.ClientConnectionType == ClientConnectionType.In)
            {
                //If connection was initiated by remote peer we allow handshake to take place before potencially disconnecting
                eventArgs.Session.ProtocolInitialized += async (s, e) => await OnProtocolInitialized(s, e);
                eventArgs.Session.PeerDisconnected += async (s, e) => await OnPeerDisconnected(s, e);
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Initiated IN connection (PeerManager)(handshake completed) for peer: {eventArgs.Session.RemoteNodeId}");
                }
                return;
            }

            var id = eventArgs.Session.RemoteNodeId;

            if (!_activePeers.TryGetValue(id, out Peer peer))
            {
                if (_logger.IsErrorEnabled)
                {
                    _logger.Error($"Initiated rlpx connection (out) with Peer without adding it to Active collection: {id}");
                }
                return;
            }

            peer.ClientConnectionType = eventArgs.ClientConnectionType;
            peer.Session = eventArgs.Session;       
            peer.Session.PeerDisconnected += async (s, e) => await OnPeerDisconnected(s, e);
            peer.Session.ProtocolInitialized += async (s, e) => await OnProtocolInitialized(s, e);

            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Initializing OUT connection (PeerManager) for peer: {eventArgs.Session.RemoteNodeId}");
            }

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
            if (session.ClientConnectionType == ClientConnectionType.In && e.ProtocolHandler is P2PProtocolHandler)
            {
                if (!await ProcessIncomingConnection(session, (P2PProtocolHandler)e.ProtocolHandler))
                {
                    return;
                }
            }

            if (!_activePeers.TryGetValue(session.RemoteNodeId, out var peer))
            {
                if (_logger.IsErrorEnabled)
                {
                    _logger.Error($"Protocol initialized for peer not present in active collection, id: {session.RemoteNodeId}.");
                }
                return;
            }

            switch (e.ProtocolHandler)
            {
                case P2PProtocolHandler p2PProtocolHandler:
                    peer.NodeStats.NodeDetails.ClientId = ((P2PProtocolInitializedEventArgs)e).ClientId;
                    var result = await ValidateProtocol(Protocol.P2P, peer, e);
                    if (!result)
                    {
                        return;
                    }
                    peer.NodeStats.AddNodeStatsEvent(NodeStatsEvent.P2PInitialized);
                    peer.P2PMessageSender = p2PProtocolHandler;
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
                        _logger.Info($"Eth62 initialized, adding sync peer: {peer.Node.Id}");
                    }
                    //Add peer to the storage and to sync manager
                    _peerStorage.UpdatePeers(new []{peer});
                    await _synchronizationManager.AddPeer(ethProtocolhandler);

                    break;
            }
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Protocol Initialized: {session.RemoteNodeId}, {e.ProtocolHandler.GetType().Name}");
            }            
        }

        private async Task<bool> ProcessIncomingConnection(IP2PSession session, P2PProtocolHandler protocolHandler)
        {
            //if we have already initiated connection before
            if (_activePeers.ContainsKey(session.RemoteNodeId))
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Initiating disconnect, node is already connected: {session.RemoteNodeId}");
                }
                await session.InitiateDisconnectAsync(DisconnectReason.AlreadyConnected);
                return false;
            }

            //if we have too many acive peers
            if (_activePeers.Count >= _configurationProvider.ActivePeersMaxCount)
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Initiating disconnect, we have too many peers: {session.RemoteNodeId}");
                }
                await session.InitiateDisconnectAsync(DisconnectReason.TooManyPeers);
                return false;
            }
            
            //it is possible we already have this node as a candidate
            if (_candidatePeers.TryGetValue(session.RemoteNodeId, out var peer))
            {
                peer.Session = session;
                peer.P2PMessageSender = protocolHandler;
                peer.ClientConnectionType = session.ClientConnectionType;
            }
            else
            {
                peer = new Peer(_nodeFactory.CreateNode(session.RemoteNodeId, session.RemoteHost, session.RemotePort ?? 0), _nodeStatsProvider.GetNodeStats(session.RemoteNodeId))
                {
                    ClientConnectionType = session.ClientConnectionType,
                    Session = session,
                    P2PMessageSender = protocolHandler
            };
            }

            if (_activePeers.TryAdd(session.RemoteNodeId, peer))
            {
                //add subsripton for disconnect for new active peer
                //peer.Session.PeerDisconnected += async (s, e) => await OnPeerDisconnected(s, e);

                //we also add this node to candidates for future connection (if we dont have it yet)
                _candidatePeers.TryAdd(session.RemoteNodeId, peer);

                if (_isDiscoveryEnabled && peer.NodeLifecycleManager == null)
                {
                    //In case peer was initiated outside of discovery and discovery is enabled, we are adding it to discovery for future use
                    var manager = _discoveryManager.GetNodeLifecycleManager(peer.Node);
                    peer.NodeLifecycleManager = manager;
                }

                return true;
            }

            //if we have already initiated connection before (threding safeguard - it means another thread added this node to active collection after our contains key key check above)
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Initiating disconnect, node is already connected: {session.RemoteNodeId}");
            }
            await session.InitiateDisconnectAsync(DisconnectReason.AlreadyConnected);
            return false;
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
                            _logger.Info($"Initiating disconnect, incorrect P2PVersion: {args.P2PVersion}, id: {peer.Node.Id}");
                        }
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.IncompatibleP2PVersion);
                        return false;
                    }

                    if (!args.Capabilities.Any(x => x.ProtocolCode == Protocol.Eth && x.Version == 62))
                    {
                        if (_logger.IsInfoEnabled)
                        {
                            _logger.Info($"Initiating disconnect, no Eth62 capability, supported capabilities: [{string.Join(",", args.Capabilities.Select(x => $"{x.ProtocolCode}v{x.Version}"))}], id: {peer.Node.Id}");
                        }
                        //TODO confirm disconnect reason
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.Other);
                        return false;
                    }

                    //if (args.ClientId.Contains("Geth") || args.ClientId.Contains("Parity") || args.ClientId.Contains("Gnekonium"))
                    //{
                    //    if (_logger.IsInfoEnabled)
                    //    {
                    //        _logger.Info($"Initiating disconnect, rejecting client: {args.ClientId}, id: {peer.Node.Id}");
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
                            _logger.Info($"Initiating disconnect, different chainId: {ethArgs.ChainId}, our chainId: {_synchronizationManager.ChainId}, peer id: {peer.Node.Id}");
                        }
                        //TODO confirm disconnect reason
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.Other);
                        return false;
                    }
                    break;
            }
            return true;
        }

        private async Task OnPeerDisconnected(object sender, DisconnectEventArgs e)
        {
            var peer = (IP2PSession) sender;
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Peer disconnected event in PeerManager: {peer.RemoteNodeId}");
            }

            if (_activePeers.TryGetValue(peer.RemoteNodeId, out var activePeer))
            {
                if (activePeer.Session.SessionId != e.SessionId)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"Received disconnect on a different session than the active peer runs. Ignoring. Id: {activePeer.Node.Id}");
                    }
                    //TODO verify we do not want to change reputation here
                    return;
                }

                _activePeers.TryRemove(peer.RemoteNodeId, out _);
                activePeer.NodeStats.AddNodeStatsDisconnectEvent(e.DisconnectType, e.DisconnectReason);
                if (activePeer.SynchronizationPeer != null)
                {
                    _synchronizationManager.RemovePeer(activePeer.SynchronizationPeer);
                }

                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Removing Active Peer on disconnect {peer.RemoteNodeId}");
                }

                if (_isInitialized)
                {
                    await RunPeerUpdate();
                }
            }
        }

        private async Task OnNodeDiscovered(object sender, NodeEventArgs nodeEventArgs)
        {
            var id = nodeEventArgs.Manager.ManagedNode.Id;
            if (_candidatePeers.ContainsKey(id))
            {
                return;
            }

            var peer = new Peer(nodeEventArgs.Manager);
            if (!_candidatePeers.TryAdd(id, peer))
            {
                return;
            }
            
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Adding newly discovered node to Candidates collection {id}@{nodeEventArgs.Manager.ManagedNode.Host}:{nodeEventArgs.Manager.ManagedNode.Port}");
            }

            if (_isInitialized)
            {
                await RunPeerUpdate();
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

        private void StartPingTimer()
        {
            if (_logger.IsInfoEnabled)
            {
                _logger.Info("Starting ping timer");
            }

            _pingTimer = new Timer(_configurationProvider.P2PPingInterval) { AutoReset = false };
            _pingTimer.Elapsed += async (sender, e) =>
            {
                _pingTimer.Enabled = false;
                await SendPingMessages();
                _pingTimer.Enabled = true;
            };

            _pingTimer.Start();
        }

        private void StopPingTimer()
        {
            try
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info("Stopping ping timer");
                }
                _pingTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during ping timer stop", e);
            }
        }

        private void RunPeerCommit()
        {
            if (!_peerStorage.AnyPendingChange())
            {
                return;
            }

            _peerStorage.Commit();
            _peerStorage.StartBatch();
        }

        private async Task SendPingMessages()
        {
            var pingTasks = new List<(Peer peer, Task<bool> pingTask)>();
            foreach (var activePeer in ActivePeers)
            {
                if (activePeer.P2PMessageSender != null)
                {
                    var pingTask = SendPingMessage(activePeer);
                    pingTasks.Add((activePeer, pingTask));
                }
            }

            if (pingTasks.Any())
            {
                var tasks = await Task.WhenAll(pingTasks.Select(x => x.pingTask));

                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Sent ping messages to {tasks.Length} peers. Disconnected: {tasks.Count(x => x == false)}");
                }
                return;
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("Sent no ping messages.");
            }
        }

        private async Task<bool> SendPingMessage(Peer peer)
        {
            for (var i = 0; i < _configurationProvider.P2PPingRetryCount; i++)
            {
                var result = await peer.P2PMessageSender.SendPing();
                if (result)
                {
                    return true;
                }
            }
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Disconnecting due to missed ping messages: {peer.Session.RemoteNodeId}");
            }
            await peer.Session.InitiateDisconnectAsync(DisconnectReason.ReceiveMessageTimeout);

            return false;
        }
    }
}