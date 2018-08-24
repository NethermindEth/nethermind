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
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Network.Config;
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
        private readonly INetworkConfig _networkConfig;
        private readonly ISynchronizationManager _synchronizationManager;
        private readonly INodeStatsProvider _nodeStatsProvider;
        private readonly IPeerStorage _peerStorage;
        private readonly INodeFactory _nodeFactory;
        private Timer _activePeersTimer;
        private Timer _peerPersistanceTimer;
        private Timer _pingTimer;      
        private int _logCounter = 1;
        private bool _isInitialized;
        private bool _isPeerUpdateInProgress;
        private readonly object _isPeerUpdateInProgressLock = new object();
        private readonly IPerfService _perfService;
        private bool _isDiscoveryEnabled;

        private readonly ConcurrentDictionary<NodeId, Peer> _activePeers = new ConcurrentDictionary<NodeId, Peer>();
        private readonly ConcurrentDictionary<NodeId, Peer> _candidatePeers = new ConcurrentDictionary<NodeId, Peer>();

        public PeerManager(IRlpxPeer localPeer,
            IDiscoveryManager discoveryManager,
            ISynchronizationManager synchronizationManager,
            INodeStatsProvider nodeStatsProvider,
            IPeerStorage peerStorage,
            INodeFactory nodeFactory,
            IConfigProvider configurationProvider,
            IPerfService perfService,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _localPeer = localPeer;
            _networkConfig = configurationProvider.GetConfig<INetworkConfig>();
            _synchronizationManager = synchronizationManager;
            _nodeStatsProvider = nodeStatsProvider;
            _discoveryManager = discoveryManager;
            _perfService = perfService;
            _nodeFactory = nodeFactory;
            _peerStorage = peerStorage;
            _peerStorage.StartBatch();
        }

        public IReadOnlyCollection<Peer> CandidatePeers => _candidatePeers.Values.ToArray();
        public IReadOnlyCollection<Peer> ActivePeers => _activePeers.Values.ToArray();

        public void Initialize(bool isDiscoveryEnabled)
        {
            _isDiscoveryEnabled = isDiscoveryEnabled;
            _discoveryManager.NodeDiscovered += async (s, e) => await OnNodeDiscovered(s, e);
            _localPeer.OutConnectionInitialized += OnRemoteOutConnectionInitialized;
            _localPeer.HandshakeInitialized += async (s, e) => await OnHandshakeInitialized(s, e);
            _synchronizationManager.SyncEvent += async (s, e) => await OnSyncEvent(s, e);
            //_synchronizationManager.SyncStarted += OnSyncStarted;

            //Step 1 - load configured trusted peers
            AddTrustedPeers();

            //Step 2 - read peers from db
            AddPersistedPeers();
        }

        public async Task Start()
        {
            //Step 3 - start active peers timer - timer is needed to support reconnecting, event based connection is also supported
            if (_networkConfig.IsActivePeerTimerEnabled)
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

        public async Task StopAsync()
        {
            await _synchronizationManager.StopAsync().ContinueWith(x =>
            {
                if (x.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Error during _synchronizationManager stop.", x.Exception);
                }
            });

            LogSessionStats();

            if (_networkConfig.IsActivePeerTimerEnabled)
            {
                StopActivePeersTimer();
            }

            StopPeerPersistanceTimer();
            StopPingTimer();
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

            var availableActiveCount = _networkConfig.ActivePeersMaxCount - _activePeers.Count;
            if (availableActiveCount <= 0)
            {
                return;
            }

            var candidates = _candidatePeers.Where(x => !_activePeers.ContainsKey(x.Key)).ToArray();
            var allNonActiveCandidates = candidates.Length;

            var nonZeroPortCandidates = candidates.Where(x => x.Value.Node.Port != 0).ToArray();
            var filteredByZeroPort = allNonActiveCandidates - nonZeroPortCandidates.Length;

            var lastDisconnCandidates = nonZeroPortCandidates.Where(x => CheckLastDisconnectTime(x.Value)).ToArray();
            var filteredByDisconnect = nonZeroPortCandidates.Length - lastDisconnCandidates.Length;

            var lastFailedConnCandidates = lastDisconnCandidates.Where(x => CheckLastFailedConnectionTime(x.Value)).ToArray();
            var filteredByFailedConnection = lastDisconnCandidates.Length - lastFailedConnCandidates.Length;

            var incompatibleNodes = lastFailedConnCandidates.Where(x => x.Value.NodeStats.FailedCompatibilityValidation.HasValue).ToArray();
            candidates = lastFailedConnCandidates.Where(x => !x.Value.NodeStats.FailedCompatibilityValidation.HasValue)
                .OrderBy(x => x.Value.NodeStats.IsTrustedPeer)
                .ThenByDescending(x => x.Value.NodeStats.CurrentNodeReputation).ToArray();

            var newActiveNodes = 0;
            var tryCount = 0;
            var failedInitialConnect = 0;
            for (var i = 0; i < candidates.Length; i++)
            {
                if (newActiveNodes >= availableActiveCount)
                {
                    break;
                }

                var candidate = candidates[i];
                tryCount++;

                //Can happen when In connection is received from the same peer and is initialized before we get here
                //In this case we do not initialze OUT connection
                if (!_activePeers.TryAdd(candidate.Key, candidate.Value))
                {
                    if (_logger.IsDebug)
                    {
                        _logger.Debug($"Active peer was already added to collection: {candidate.Key}");
                    }
                    continue;
                }

                var result = await InitializePeerConnection(candidate.Value);
                if (!result)
                {
                    candidate.Value.NodeStats.AddNodeStatsEvent(NodeStatsEventType.ConnectionFailed);
                    failedInitialConnect++;
                    _activePeers.TryRemove(candidate.Key, out _);
                    continue;
                }

                newActiveNodes++;                
            }

            if (_logger.IsInfo)
            {
                _logger.Info($"RunPeerUpdate | AllNonActive: {allNonActiveCandidates}, FilteredByZeroPort: {filteredByZeroPort}, FilteredByDisconnectDelay: {filteredByDisconnect}, FileteredByFailedConnDelay: {filteredByFailedConnection}, Incompatible: {GetIncompatibleDesc(incompatibleNodes)}, EligibleCandidates: {candidates.Length}, " +
                             $"Tried: {tryCount}, Failed initial connect: {failedInitialConnect}, Established initial connect: {newActiveNodes}, Current candidate peers: {_candidatePeers.Count}, Current active peers: {_activePeers.Count}");
            }

            if (_logger.IsTrace)
            {
                if (_logCounter % 5 == 0)
                {
                    string nl = Environment.NewLine;
                    _logger.Trace($"{nl}{nl}All active peers: {nl}{string.Join(nl, ActivePeers.Select(x => $"{x.Node.ToString()} | P2PInitialized: {x.NodeStats.DidEventHappen(NodeStatsEventType.P2PInitialized)} | Eth62Initialized: {x.NodeStats.DidEventHappen(NodeStatsEventType.Eth62Initialized)} | ClientId: {x.NodeStats.P2PNodeDetails?.ClientId}"))} {nl}{nl}");
                }

                _logCounter++;
            }

            _perfService.EndPerfCalc(key, "RunPeerUpdate");
            _isPeerUpdateInProgress = false;
        }

        public bool IsPeerConnected(NodeId peerId)
        {
            return _activePeers.ContainsKey(peerId);
        }

        private void LogSessionStats()
        {
            if (_logger.IsInfo)
            {
                var peers = _activePeers.Values.Concat(_candidatePeers.Values).GroupBy(x => x.Node.Id).Select(x => x.First()).ToArray();

                var eventTypes = Enum.GetValues(typeof(NodeStatsEventType)).OfType<NodeStatsEventType>().Where(x => !x.ToString().Contains("Discovery"))
                    .OrderBy(x => x).ToArray();
                var eventStats = eventTypes.Select(x => new
                {
                    EventType = x.ToString(),
                    Count = peers.Count(y => y.NodeStats.DidEventHappen(x))
                }).ToArray();

                var chains = peers.Where(x => x.NodeStats.Eth62NodeDetails != null).GroupBy(x => x.NodeStats.Eth62NodeDetails.ChainId).Select(
                    x => new { ChainName = ChainId.GetChainName((int)x.Key), Count = x.Count() }).ToArray();
                var clients = peers.Where(x => x.NodeStats.P2PNodeDetails != null).Select(x => x.NodeStats.P2PNodeDetails.ClientId).GroupBy(x => x).Select(
                    x => new { ClientId = x.Key, Count = x.Count() }).ToArray();
                var remoteDisconnect = peers.Count(x => x.NodeStats.EventHistory.Any(y => y.DisconnectDetails != null && y.DisconnectDetails.DisconnectType == DisconnectType.Remote));

                _logger.Info($"Session stats: peers count with each EVENT:{Environment.NewLine}" +
                             $"{string.Join(Environment.NewLine, eventStats.Select(x => $"{x.EventType.ToString()}:{x.Count}"))}{Environment.NewLine}" +
                             $"Remote disconnect: {remoteDisconnect}{Environment.NewLine}{Environment.NewLine}" +
                             $"CHAINS: {Environment.NewLine}" +
                             $"{string.Join(Environment.NewLine, chains.Select(x => $"{x.ChainName}:{x.Count}"))}{Environment.NewLine}{Environment.NewLine}" +
                             $"CLIENTS:{Environment.NewLine}" +
                             $"{string.Join(Environment.NewLine, clients.Select(x => $"{x.ClientId}:{x.Count}"))}{Environment.NewLine}");

                if (_networkConfig.CaptureNodeStatsEventHistory)
                {
                    _logger.Info($"Logging {peers.Length} peers log event histories");

                    foreach (var peer in peers)
                    {
                        LogEventHistory(peer.NodeStats);
                    }

                    _logger.Info("Logging event histories finished");
                }
            }
        }

        private string GetIncompatibleDesc(KeyValuePair<NodeId, Peer>[] incompatibleNodes)
        {
            if (!incompatibleNodes.Any())
            {
                return "0";
            }
            var validationGroups = incompatibleNodes.GroupBy(x => x.Value.NodeStats.FailedCompatibilityValidation).ToArray();
            return $"[{string.Join(", ", validationGroups.Select(x => $"{x.Key.ToString()}:{x.Count()}"))}]";
        }

        private bool CheckLastDisconnectTime(Peer peer)
        {
            var time = peer.NodeStats.LastDisconnectTime;
            if (!time.HasValue)
            {
                return true;
            }

            var timePassed = DateTime.Now.Subtract(time.Value).TotalMilliseconds;
            var result = timePassed > _networkConfig.DisconnectDelay;
            if (!result && _logger.IsTrace)
            {
                _logger.Trace($"Skipping connection to peer, due to disconnect delay, time from last disconnect: {timePassed}, delay: {_networkConfig.DisconnectDelay}, peer: {peer.Node.Id}");
            }

            return result;
        }

        private bool CheckLastFailedConnectionTime(Peer peer)
        {
            var time = peer.NodeStats.LastFailedConnectionTime;
            if (!time.HasValue)
            {
                return true;
            }

            var timePassed = DateTime.Now.Subtract(time.Value).TotalMilliseconds;
            var result = timePassed > _networkConfig.FailedConnectionDelay;
            if (!result && _logger.IsDebug)
            {
                _logger.Debug($"Skipping connection to peer, due to failed connection delay, time from last failed connection: {timePassed}, delay: {_networkConfig.FailedConnectionDelay}, peer: {peer.Node.Id}");
            }

            return result;
        }

        private async Task<bool> InitializePeerConnection(Peer candidate)
        {
            try
            {
                await _localPeer.ConnectAsync(candidate.Node.Id, candidate.Node.Host, candidate.Node.Port, candidate.NodeStats);
                return true;
            }
            catch (NetworkingException ex)
            {
                if (_logger.IsDebug) _logger.Debug($"Cannot connect to Peer [{ex.NetwokExceptionType.ToString()}]: {candidate.Node.Id}");
                return false;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Error trying to initiate connetion with peer: {candidate.Node.Id}", e);
                return false;
            }
        }

        private void AddPersistedPeers()
        {
            if (!_networkConfig.IsPeersPersistenceOn)
            {
                return;
            }

            var peers = _peerStorage.GetPersistedNodes();

            if (_logger.IsInfo)
            {
                _logger.Info($"Initializing persisted peers: {peers.Length}.");
            }

            foreach (var persistedPeer in peers)
            {
                if (_candidatePeers.ContainsKey(persistedPeer.NodeId))
                {
                    continue;
                }

                var node = _nodeFactory.CreateNode(persistedPeer.NodeId, persistedPeer.Host, persistedPeer.Port);
                var nodeStats = _nodeStatsProvider.GetOrAddNodeStats(node);
                nodeStats.CurrentPersistedNodeReputation = persistedPeer.Reputation;

                var peer = new Peer(node, nodeStats);
                if (!_candidatePeers.TryAdd(node.Id, peer))
                {
                    continue;
                }

                if (_logger.IsTrace)
                {
                    _logger.Trace($"Adding persisted peer to New collection {node.Id}@{node.Host}:{node.Port}");
                }
            }
        }

        private void AddTrustedPeers()
        {
            var trustedPeers = _networkConfig.TrustedPeers;
            if (trustedPeers == null || !trustedPeers.Any())
            {
                return;
            }

            if (_logger.IsInfo)
            {
                _logger.Info($"Initializing trusted peers: {trustedPeers.Length}.");
            }

            foreach (var trustedPeer in trustedPeers)
            {
                var node = _nodeFactory.CreateNode(new NodeId(new PublicKey(Bytes.FromHexString(trustedPeer.NodeId))), trustedPeer.Host, trustedPeer.Port);
                node.Description = trustedPeer.Description;

                var nodeStats = _nodeStatsProvider.GetOrAddNodeStats(node);
                nodeStats.IsTrustedPeer = true;

                var peer = new Peer(node, nodeStats);
                if (!_candidatePeers.TryAdd(node.Id, peer))
                {
                    continue;
                }

                if (_logger.IsDebug)
                {
                    _logger.Debug($"Adding trusted peer to New collection {node.Id}@{node.Host}:{node.Port}");
                }
            }
        }

        private async Task OnHandshakeInitialized(object sender, ConnectionInitializedEventArgs eventArgs)
        {
            if (eventArgs.ClientConnectionType == ClientConnectionType.In)
            {
                //If connection was initiated by remote peer we allow handshake to take place before potencially disconnecting
                eventArgs.Session.ProtocolInitialized += async (s, e) => await OnProtocolInitialized(s, e);
                eventArgs.Session.PeerDisconnected += async (s, e) => await OnPeerDisconnected(s, e);
                if (_logger.IsDebug)
                {
                    _logger.Debug($"Initiated IN connection (PeerManager)(handshake completed) for peer: {eventArgs.Session.RemoteNodeId}");
                }

                await ProcessIncomingConnection(eventArgs.Session);
            }
            else
            {
                if (!_activePeers.TryGetValue(eventArgs.Session.RemoteNodeId, out Peer peer))
                {
                    if (_logger.IsError)
                    {
                        _logger.Error($"Initiated Handshake (out) with Peer without adding it to Active collection: {eventArgs.Session.RemoteNodeId}");
                    }
                    return;
                }
                peer.NodeStats.AddNodeStatsHandshakeEvent(eventArgs.ClientConnectionType);
            }
            if (_logger.IsDebug)
            {
                _logger.Debug($"Handshake initialized for peer: {eventArgs.Session.RemoteNodeId}");
            }
        }

        private void OnRemoteOutConnectionInitialized(object sender, ConnectionInitializedEventArgs eventArgs)
        {
            //happens only for out connections
            var id = eventArgs.Session.RemoteNodeId;

            if (!_activePeers.TryGetValue(id, out Peer peer))
            {
                if (_logger.IsError)
                {
                    _logger.Error($"Initiated rlpx connection (out) with Peer without adding it to Active collection: {id}");
                }
                return;
            }

            peer.NodeStats.AddNodeStatsEvent(NodeStatsEventType.ConnectionEstablished);
            peer.ClientConnectionType = eventArgs.ClientConnectionType;
            peer.Session = eventArgs.Session;       
            peer.Session.PeerDisconnected += async (s, e) => await OnPeerDisconnected(s, e);
            peer.Session.ProtocolInitialized += async (s, e) => await OnProtocolInitialized(s, e);

            if (_logger.IsDebug)
            {
                _logger.Debug($"Initializing OUT connection (PeerManager) for peer: {eventArgs.Session.RemoteNodeId}");
            }

            AddNodeToDiscovery(peer);
        }

        private async Task OnProtocolInitialized(object sender, ProtocolInitializedEventArgs e)
        {
            if(_logger.IsDebug) _logger.Debug($"Protocol initialized {e.ProtocolHandler.ProtocolCode} {e.ProtocolHandler.ProtocolVersion}.");
            
            var session = (IP2PSession)sender;

            if (!_activePeers.TryGetValue(session.RemoteNodeId, out var peer))
            {
                if (_candidatePeers.TryGetValue(session.RemoteNodeId, out var candidatePeer))
                {
                    if (e.ProtocolHandler is P2PProtocolHandler)
                    {
                        AddNodeToDiscovery(candidatePeer, (P2PProtocolInitializedEventArgs)e);
                    }
                    if (_logger.IsWarn)
                    {
                        var timeFromLastDisconnect = candidatePeer.NodeStats.LastDisconnectTime.HasValue
                            ? DateTime.Now.Subtract(candidatePeer.NodeStats.LastDisconnectTime.Value).TotalMilliseconds.ToString()
                            : "no disconnect";

                        _logger.Warn($"Protocol initialized for peer not present in active collection, id: {session.RemoteNodeId}, time from last disconnect: {timeFromLastDisconnect}.");
                    }
                }
                else
                {
                    if (_logger.IsError)
                    {
                        _logger.Error($"Protocol initialized for peer not present in active collection, id: {session.RemoteNodeId}, peer not in candidate collection.");
                    }
                }

                //Initializing disconnect if it hasnt been done already - in case of e.g. timeout earier and unexcepted further connection
                await session.InitiateDisconnectAsync(DisconnectReason.Other);

                return;
            }

            switch (e.ProtocolHandler)
            {
                case P2PProtocolHandler p2PProtocolHandler:
                    var p2PEventArgs = (P2PProtocolInitializedEventArgs) e;
                    AddNodeToDiscovery(peer, p2PEventArgs);
                    peer.NodeStats.AddNodeStatsP2PInitializedEvent(new P2PNodeDetails
                    {
                        ClientId = p2PEventArgs.ClientId,
                        Capabilities = p2PEventArgs.Capabilities.ToArray(),
                        P2PVersion = p2PEventArgs.P2PVersion,
                        ListenPort = p2PEventArgs.ListenPort
                    });
                    var result = await ValidateProtocol(Protocol.P2P, peer, e);
                    if (!result)
                    {
                        return;
                    }
                    peer.P2PMessageSender = p2PProtocolHandler;
                    break;
                case Eth62ProtocolHandler ethProtocolhandler:
                    var eth62EventArgs = (Eth62ProtocolInitializedEventArgs)e;
                    peer.NodeStats.AddNodeStatsEth62InitializedEvent(new Eth62NodeDetails
                    {
                        ChainId = eth62EventArgs.ChainId,
                        BestHash = eth62EventArgs.BestHash,
                        GenesisHash = eth62EventArgs.GenesisHash,
                        Protocol = eth62EventArgs.Protocol,
                        ProtocolVersion = eth62EventArgs.ProtocolVersion,
                        TotalDifficulty = eth62EventArgs.TotalDifficulty
                    });
                    result = await ValidateProtocol(Protocol.Eth, peer, e);
                    if (!result)
                    {
                        return;
                    }
                    //TODO move this outside, so syncManager have access to NodeStats and NodeDetails
                    ethProtocolhandler.ClientId = peer.NodeStats.P2PNodeDetails.ClientId;
                    peer.SynchronizationPeer = ethProtocolhandler;

                    if (_logger.IsDebug)
                    {
                        _logger.Debug($"Eth62 initialized, adding sync peer: {peer.Node.Id}");
                    }
                    //Add/Update peer to the storage and to sync manager
                    _peerStorage.UpdateNodes(new []{new NetworkNode(peer.Node.Id.PublicKey, peer.Node.Host, peer.Node.Port, peer.Node.Description, peer.NodeStats.NewPersistedNodeReputation) });
                    await _synchronizationManager.AddPeer(ethProtocolhandler);

                    break;
            }
            
            if (_logger.IsDebug)
            {
                _logger.Debug($"Protocol Initialized: {session.RemoteNodeId}, {e.ProtocolHandler.GetType().Name}");
            }            
        }

        /// <summary>
        /// In case of IN connection we dont know what is the port node is listening on until we receive the Hello message
        /// </summary>
        private void AddNodeToDiscovery(Peer peer, P2PProtocolInitializedEventArgs eventArgs)
        {
            if (eventArgs.ListenPort == 0)
            {
                if (_logger.IsTrace) _logger.Trace($"Listen port is 0, node is not listening: {peer.Node.Id}, ConnectionType: {peer.Session.ClientConnectionType}, nodePort: {peer.Node.Port}");
                return;
            }

            if (peer.Node.Port != eventArgs.ListenPort)
            { 
                if (_logger.IsDebug) _logger.Debug($"Updating listen port for node: {peer.Node.Id}, ConnectionType: {peer.Session.ClientConnectionType}, from: {peer.Node.Port} to: {eventArgs.ListenPort}");
                peer.Node.Port = eventArgs.ListenPort;

                if (peer.NodeLifecycleManager != null)
                {
                    if (_logger.IsError) _logger.Error($"Discovery note already initiialized with wrong port, nodeId: {peer.Node.Port}, port: {peer.NodeLifecycleManager.ManagedNode.Port}, listen port: {eventArgs.ListenPort}");
                    peer.NodeLifecycleManager.ManagedNode.Port = eventArgs.ListenPort;
                }
            }

            AddNodeToDiscovery(peer);
        }

        private void AddNodeToDiscovery(Peer peer)
        {
            if (!_isDiscoveryEnabled || peer.NodeLifecycleManager != null)
            {
                return;
            }

            //In case peer was initiated outside of discovery and discovery is enabled, we are adding it to discovery for future use (e.g. trusted peer)
            var manager = _discoveryManager.GetNodeLifecycleManager(peer.Node);
            if (manager != null)
            {
                peer.NodeLifecycleManager = manager;
            }
        }

        private async Task ProcessIncomingConnection(IP2PSession session)
        {
            //if we have already initiated connection before
            if (_activePeers.ContainsKey(session.RemoteNodeId))
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"Initiating disconnect, node is already connected: {session.RemoteNodeId}");
                }
                
                await session.InitiateDisconnectAsync(DisconnectReason.AlreadyConnected);
                return;
            }

            //if we have too many acive peers
            if (_activePeers.Count >= _networkConfig.ActivePeersMaxCount)
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"Initiating disconnect, we have too many peers: {session.RemoteNodeId}");
                }
                
                await session.InitiateDisconnectAsync(DisconnectReason.TooManyPeers);
                return;
            }

            //it is possible we already have this node as a candidate
            if (_candidatePeers.TryGetValue(session.RemoteNodeId, out var peer))
            {
                peer.Session = session;
                peer.ClientConnectionType = session.ClientConnectionType;
            }
            else
            {
                var node = _nodeFactory.CreateNode(session.RemoteNodeId, session.RemoteHost, session.RemotePort ?? 0);
                peer = new Peer(node, _nodeStatsProvider.GetOrAddNodeStats(node))
                {
                    ClientConnectionType = session.ClientConnectionType,
                    Session = session
                };
            }

            if (_activePeers.TryAdd(session.RemoteNodeId, peer))
            {
                peer.NodeStats.AddNodeStatsHandshakeEvent(ClientConnectionType.In);

                //we also add this node to candidates for future connection (if we dont have it yet)
                _candidatePeers.TryAdd(session.RemoteNodeId, peer);

                return;
            }

            //if we have already initiated connection before (threding safeguard - it means another thread added this node to active collection after our contains key key check above)
            if (_logger.IsDebug)
            {
                _logger.Debug($"Initiating disconnect, node is already connected: {session.RemoteNodeId}");
            }
            
            await session.InitiateDisconnectAsync(DisconnectReason.AlreadyConnected);
        }

        private async Task<bool> ValidateProtocol(string protocol, Peer peer, ProtocolInitializedEventArgs eventArgs)
        {
            //TODO add validation for clientId - e.g. get only ethereumJ clients
            switch (protocol)
            {
                case Protocol.P2P:
                    var args = (P2PProtocolInitializedEventArgs)eventArgs;
                    if (!ValidateP2PVersion(args.P2PVersion))
                    {
                        if (_logger.IsDebug)
                        {
                            _logger.Debug($"Initiating disconnect with peer: {peer.Node.Id}, incorrect P2PVersion: {args.P2PVersion}");
                        }

                        peer.NodeStats.FailedCompatibilityValidation = CompatibilityValidationType.P2PVersion;
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.IncompatibleP2PVersion);
                        return false;
                    }

                    if (!ValidateCapabilities(args.Capabilities))
                    {
                        if (_logger.IsDebug)
                        {
                            _logger.Debug($"Initiating disconnect with peer: {peer.Node.Id}, no Eth62 capability, supported capabilities: [{string.Join(",", args.Capabilities.Select(x => $"{x.ProtocolCode}v{x.Version}"))}]");
                        }
                        peer.NodeStats.FailedCompatibilityValidation = CompatibilityValidationType.Capabilities;
                        //TODO confirm disconnect reason
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.Other);
                        return false;
                    }
                    break;
                case Protocol.Eth:
                    var ethArgs = (Eth62ProtocolInitializedEventArgs)eventArgs;
                    if (!ValidateChainId(ethArgs.ChainId))
                    {
                        if (_logger.IsDebug)
                        {
                            _logger.Debug($"Initiating disconnect with peer: {peer.Node.Id}, different chainId: {ChainId.GetChainName((int)ethArgs.ChainId)}, our chainId: {ChainId.GetChainName(_synchronizationManager.ChainId)}");
                        }
                        peer.NodeStats.FailedCompatibilityValidation = CompatibilityValidationType.ChainId;
                        //TODO confirm disconnect reason
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.Other);
                        return false;
                    }
                    if (ethArgs.GenesisHash != _synchronizationManager.Genesis?.Hash)
                    {
                        if (_logger.IsDebug)
                        {
                            _logger.Debug($"Initiating disconnect with peer: {peer.Node.Id}, different genesis hash: {ethArgs.GenesisHash}, our: {_synchronizationManager.Genesis?.Hash}");
                        }
                        peer.NodeStats.FailedCompatibilityValidation = CompatibilityValidationType.DifferentGenesis;
                        //TODO confirm disconnect reason
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.Other);
                        return false;
                    }
                    break;
            }
            return true;
        }

        private bool ValidateP2PVersion(byte p2PVersion)
        {
            return p2PVersion == 4 || p2PVersion == 5;
        }

        private bool ValidateCapabilities(IEnumerable<Capability> capabilities)
        {
            return capabilities.Any(x => x.ProtocolCode == Protocol.Eth && x.Version == 62);
        }

        private bool ValidateChainId(long chainId)
        {
            return chainId == _synchronizationManager.ChainId;
        }

        private async Task OnPeerDisconnected(object sender, DisconnectEventArgs e)
        {
            var peer = (IP2PSession) sender;
            if (_logger.IsDebug)
            {
                _logger.Debug($"Peer disconnected event in PeerManager: {peer.RemoteNodeId}, disconnectReason: {e.DisconnectReason}, disconnectType: {e.DisconnectType}");
            }

            if (peer.RemoteNodeId == null)
            {
                if (_logger.IsWarn) _logger.Warn($"Disconnect on session with no RemoteNodeId, sessionId: {peer.SessionId}");
                return;
            }

            if (_activePeers.TryGetValue(peer.RemoteNodeId, out var activePeer))
            {
                if (activePeer.Session?.SessionId != e.SessionId)
                {
                    if (_logger.IsDebug)
                    {
                        _logger.Debug($"Received disconnect on a different session than the active peer runs. Ignoring. Id: {activePeer.Node.Id}");
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

                if (_logger.IsDebug)
                {
                    _logger.Debug($"Removing Active Peer on disconnect {peer.RemoteNodeId}");
                }

                if (_logger.IsDebug && _networkConfig.CaptureNodeStatsEventHistory)
                {
                    LogEventHistory(activePeer.NodeStats);
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
            
            peer.NodeStats.AddNodeStatsEvent(NodeStatsEventType.NodeDiscovered);

            if (_logger.IsTrace)
            {
                _logger.Trace($"Adding newly discovered node to Candidates collection {id}@{nodeEventArgs.Manager.ManagedNode.Host}:{nodeEventArgs.Manager.ManagedNode.Port}");
            }

            if (_isInitialized)
            {
                await RunPeerUpdate();
            }
        }

        private void StartActivePeersTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting active peers timer");
            _activePeersTimer = new Timer(_networkConfig.ActivePeerUpdateInterval) {AutoReset = false};
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
                if (_logger.IsDebug) _logger.Debug("Stopping active peers timer");
                _activePeersTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during active peers timer stop", e);
            }
        }

        private void StartPeerPersistanceTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting peer persistance timer");

            _peerPersistanceTimer = new Timer(_networkConfig.PeersPersistanceInterval) {AutoReset = false};
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
                if (_logger.IsInfo) _logger.Info("Stopping peer persistance timer");
                _peerPersistanceTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during peer persistance timer stop", e);
            }
        }

        private void StartPingTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting ping timer");

            _pingTimer = new Timer(_networkConfig.P2PPingInterval) { AutoReset = false };
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
                if (_logger.IsDebug) _logger.Debug("Stopping ping timer");
                _pingTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during ping timer stop", e);
            }
        }

        private void RunPeerCommit()
        {
            UpdateCurrentlyStoredPeersReputation();

            if (!_peerStorage.AnyPendingChange())
            {
                if (_logger.IsDebug) _logger.Debug("No changes in peer storage, skipping commit.");
                return;
            }

            _peerStorage.Commit();
            _peerStorage.StartBatch();
        }

        private void UpdateCurrentlyStoredPeersReputation()
        {
            var storedNodes = _peerStorage.GetPersistedNodes();
            var peers = _activePeers.Values.Concat(_candidatePeers.Values).GroupBy(x => x.Node.Id).Select(x => x.First()).ToDictionary(x => x.Node.Id);
            var nodesForUpdate = new List<NetworkNode>();
            foreach (var node in storedNodes)
            {
                if (!peers.ContainsKey(node.NodeId))
                {
                    continue;
                }

                var peer = peers[node.NodeId];
                var newRep = peer.NodeStats.NewPersistedNodeReputation;
                if (newRep != node.Reputation)
                {
                    node.Reputation = newRep;
                    nodesForUpdate.Add(node);
                }
            }

            if (nodesForUpdate.Any())
            {
                //we need to update all stored notes to update reputation
                _peerStorage.UpdateNodes(nodesForUpdate.ToArray());
            }
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
                if (_logger.IsTrace) _logger.Trace($"Sent ping messages to {tasks.Length} peers. Disconnected: {tasks.Count(x => x == false)}");
            }
            else if (_logger.IsTrace) _logger.Trace("Sent no ping messages.");
        }

        private async Task<bool> SendPingMessage(Peer peer)
        {
            for (var i = 0; i < _networkConfig.P2PPingRetryCount; i++)
            {
                var result = await peer.P2PMessageSender.SendPing();
                if (result)
                {
                    return true;
                }
            }
            
            if (_logger.IsDebug) _logger.Debug($"Disconnecting due to missed ping messages: {peer.Session.RemoteNodeId}");
            await peer.Session.InitiateDisconnectAsync(DisconnectReason.ReceiveMessageTimeout);
            return false;
        }

        private async Task OnSyncEvent(object sender, SyncEventArgs e)
        {
            if (_activePeers.TryGetValue(e.Peer.NodeId, out var activePeer) && activePeer.Session != null)
            {
                var nodeStatsEvent = GetSyncEventType(e.SyncStatus);
                activePeer.NodeStats.AddNodeStatsSyncEvent(nodeStatsEvent, new SyncNodeDetails
                {
                    NodeBestBlockNumber = e.NodeBestBlockNumber,
                    OurBestBlockNumber = e.OurBestBlockNumber
                });

                if (e.SyncStatus == SyncStatus.Failed)
                {
                    if (_logger.IsDebug) _logger.Debug($"Initializing disconnect on sync failed with node: {e.Peer.NodeId}");
                    
                    await activePeer.Session.InitiateDisconnectAsync(DisconnectReason.BreachOfProtocol);
                }
            }
            else if (_logger.IsDebug) _logger.Debug($"Sync failed, peer not in active collection: {e.Peer.NodeId}");
        }

        private NodeStatsEventType GetSyncEventType(SyncStatus syncStatus)
        {
            switch (syncStatus)
            {
                case SyncStatus.InitCompleted:
                    return NodeStatsEventType.SyncInitCompleted;
                case SyncStatus.InitCancelled:
                    return NodeStatsEventType.SyncInitCancelled;
                case SyncStatus.InitFailed:
                    return NodeStatsEventType.SyncInitFailed;
                case SyncStatus.Started:
                    return NodeStatsEventType.SyncStarted;
                case SyncStatus.Completed:
                    return NodeStatsEventType.SyncCompleted;
                case SyncStatus.Failed:
                    return NodeStatsEventType.SyncFailed;
                case SyncStatus.Cancelled:
                    return NodeStatsEventType.SyncCancelled;
            }
            throw new Exception($"SyncStatus not supported: {syncStatus.ToString()}");
        }

        private void LogEventHistory(INodeStats nodeStats)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"NodeEventHistory, Node: {nodeStats.Node.Id}, Address: {nodeStats.Node.Host}:{nodeStats.Node.Port}, Desc: {nodeStats.Node.Description}");

            if (nodeStats.P2PNodeDetails != null)
            {
                sb.AppendLine($"P2P details: ClientId: {nodeStats.P2PNodeDetails.ClientId}, P2PVersion: {nodeStats.P2PNodeDetails.P2PVersion}, Capabilities: {GetCapabilities(nodeStats.P2PNodeDetails)}");
            }

            if (nodeStats.Eth62NodeDetails != null)
            {
                sb.AppendLine($"Eth62 details: ChainId: {ChainId.GetChainName((int)nodeStats.Eth62NodeDetails.ChainId)}, TotalDifficulty: {nodeStats.Eth62NodeDetails.TotalDifficulty}");
            }

            foreach (var statsEvent in nodeStats.EventHistory.OrderBy(x => x.EventDate).ToArray())
            {
                sb.Append($"{statsEvent.EventDate.ToString(_networkConfig.DetailedTimeDateFormat)} | {statsEvent.EventType}");
                if (statsEvent.ClientConnectionType.HasValue)
                {
                    sb.Append($" | {statsEvent.ClientConnectionType.Value.ToString()}");
                }
                if (statsEvent.P2PNodeDetails != null)
                {
                    sb.Append($" | {statsEvent.P2PNodeDetails.ClientId} | v{statsEvent.P2PNodeDetails.P2PVersion} | {GetCapabilities(statsEvent.P2PNodeDetails)}");
                }
                if (statsEvent.Eth62NodeDetails != null)
                {
                    sb.Append($" | {ChainId.GetChainName((int)statsEvent.Eth62NodeDetails.ChainId)} | TotalDifficulty:{statsEvent.Eth62NodeDetails.TotalDifficulty}");
                }
                if (statsEvent.DisconnectDetails != null)
                {
                    sb.Append($" | {statsEvent.DisconnectDetails.DisconnectReason.ToString()} | {statsEvent.DisconnectDetails.DisconnectType.ToString()}");
                }
                if (statsEvent.SyncNodeDetails != null && (statsEvent.SyncNodeDetails.NodeBestBlockNumber.HasValue || statsEvent.SyncNodeDetails.OurBestBlockNumber.HasValue))
                {
                    sb.Append($" | NodeBestBlockNumber: {statsEvent.SyncNodeDetails.NodeBestBlockNumber} | OurBestBlockNumber: {statsEvent.SyncNodeDetails.OurBestBlockNumber}");
                }

                sb.AppendLine();
            }
            _logger.Info(sb.ToString());
        }

        private string GetCapabilities(P2PNodeDetails nodeDetails)
        {
            if (nodeDetails.Capabilities == null || !nodeDetails.Capabilities.Any())
            {
                return "none";
            }

            return string.Join("|", nodeDetails.Capabilities.Select(x => $"{x.ProtocolCode}v{x.Version}"));
        }
    }
}