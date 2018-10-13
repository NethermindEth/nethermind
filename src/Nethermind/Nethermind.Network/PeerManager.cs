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

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    /// <summary>
    /// </summary>
    public class PeerManager : IPeerManager
    {
        private readonly ILogger _logger;

        private readonly IDiscoveryApp _discoveryApp;
        private readonly INetworkConfig _networkConfig;
        private readonly IRlpxPeer _rlpxPeer;
        private readonly ISynchronizationManager _synchronizationManager;
        private readonly INodeStatsProvider _nodeStatsProvider;
        private readonly INetworkStorage _peerStorage;
        private readonly INodeFactory _nodeFactory;
        private System.Timers.Timer _activePeersTimer;
        private System.Timers.Timer _peerPersistanceTimer;
        private System.Timers.Timer _pingTimer;
        private int _logCounter = 1;
        private bool _isStarted;
        private bool _isPeerUpdateInProgress;
        private readonly object _isPeerUpdateInProgressLock = new object();
        private readonly IPerfService _perfService;
        private bool _isDiscoveryEnabled;
        private Task _storageCommitTask;
        private long _prevActivePeersCount = 0;

        private readonly ConcurrentDictionary<NodeId, Peer> _activePeers = new ConcurrentDictionary<NodeId, Peer>();
        private readonly ConcurrentDictionary<NodeId, Peer> _candidatePeers = new ConcurrentDictionary<NodeId, Peer>();

        public PeerManager(
            IRlpxPeer rlpxPeer,
            IDiscoveryApp discoveryApp,
            ISynchronizationManager synchronizationManager,
            INodeStatsProvider nodeStatsProvider,
            INetworkStorage peerStorage,
            INodeFactory nodeFactory,
            IConfigProvider configurationProvider,
            IPerfService perfService,
            ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _networkConfig = configurationProvider.GetConfig<INetworkConfig>();
            _rlpxPeer = rlpxPeer ?? throw new ArgumentNullException(nameof(rlpxPeer));
            _synchronizationManager = synchronizationManager ?? throw new ArgumentNullException(nameof(synchronizationManager));
            _nodeStatsProvider = nodeStatsProvider ?? throw new ArgumentNullException(nameof(nodeStatsProvider));
            _discoveryApp = discoveryApp ?? throw new ArgumentNullException(nameof(discoveryApp));
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _nodeFactory = nodeFactory ?? throw new ArgumentNullException(nameof(nodeFactory));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _peerStorage.StartBatch();

            _nodeStatsProvider = nodeStatsProvider;
            _logger = logManager.GetClassLogger();
        }

        internal IReadOnlyCollection<Peer> CandidatePeers => _candidatePeers.Values.ToArray();
        internal IReadOnlyCollection<Peer> ActivePeers => _activePeers.Values.ToArray();

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public void Init(bool isDiscoveryEnabled)
        {
            _isDiscoveryEnabled = isDiscoveryEnabled;
            _discoveryApp.NodeDiscovered += OnNodeDiscovered;
            _synchronizationManager.SyncEvent += OnSyncEvent;

            LoadConfiguredTrustedPeers();
            LoadPeersFromDb();

            _rlpxPeer.SessionCreated += (sender, args) =>
            {
                var session = args.Session;
                session.PeerDisconnected += OnPeerDisconnected;
                session.ProtocolInitialized += OnProtocolInitialized;
                session.HandshakeComplete += (s, e) => OnHandshakeComplete((IP2PSession)s);

                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Session created: {session.RemoteNodeId}, {session.ConnectionDirection.ToString()}");
                if (session.ConnectionDirection == ConnectionDirection.Out)
                {
                    ProcessOutgoingConnection(session);
                }
            };
        }

        public async Task Start()
        {
            // timer is needed to support reconnecting, event based connection is also supported
            if (_networkConfig.IsActivePeerTimerEnabled)
            {
                StartActivePeersTimer();
            }

            StartPeerPersistanceTimer();
            StartPingTimer();

            _isStarted = true;
            await RunPeerUpdate(); // initial peer update
        }

        public async Task StopAsync()
        {
            var key = _perfService.StartPerfCalc();
            _cancellationTokenSource.Cancel();

            if (_networkConfig.IsActivePeerTimerEnabled)
            {
                StopActivePeersTimer();
            }

            StopPeerPersistanceTimer();
            StopPingTimer();

            var closingTasks = new List<Task>();

            if (_storageCommitTask != null)
            {
                var storageCloseTask = _storageCommitTask.ContinueWith(x =>
                {
                    if (x.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error("Error during peer persistance stop.", x.Exception);
                    }
                });
                
                closingTasks.Add(storageCloseTask);
            }

            await Task.WhenAll(closingTasks);

            LogSessionStats();
            if(_logger.IsInfo) _logger.Info("Peer Manager shutdown complete.. please wait for all components to close");
            _perfService.EndPerfCalc(key, "Close: PeerManager");
        }

        public async Task RunPeerUpdate()
        {
            await Task.Run(() => RunPeerUpdateSync()).ContinueWith(x =>
            {
                if (x.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Error during Peer update", x.Exception);
                }
            });
        }

        private void RunPeerUpdateSync()
        {
            lock (_isPeerUpdateInProgressLock)
            {
                if (_isPeerUpdateInProgress)
                {
                    return;
                }

                _isPeerUpdateInProgress = true;
            }

            try
            {
                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                var tryCount = 0;
                var newActiveNodes = 0;
                var failedInitialConnect = 0;
                var connectionRounds = 0;

                var candidateSelection = SelectCandidates();
                var remainingCandidates = candidateSelection.Candidates;
                if (!remainingCandidates.Any())
                {
                    return;
                }
                
                while (true)
                {
                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        break;
                    }
                    
                    var key = _perfService.StartPerfCalc();

                    var availableActiveCount = _networkConfig.ActivePeersMaxCount - _activePeers.Count;
                    int nodesToTry = Math.Min(remainingCandidates.Count(), availableActiveCount);
                    if (nodesToTry == 0)
                    {
                        break;
                    }
                    
                    var candidatesToTry = remainingCandidates.Take(nodesToTry).ToArray();
                    remainingCandidates = remainingCandidates.Skip(nodesToTry).ToArray();
                    Parallel.ForEach(candidatesToTry, async (peer, loopState) =>
                    {
                        if (loopState.ShouldExitCurrentIteration || _cancellationTokenSource.IsCancellationRequested)
                        {
                            return;
                        }

                        Interlocked.Increment(ref tryCount);

                        // Can happen when In connection is received from the same peer and is initialized before we get here
                        // In this case we do not initialze OUT connection
                        if (!AddActivePeer(peer.Node.Id, peer, "upgrading candidate"))
                        {
                            if (_logger.IsTrace) _logger.Trace($"Active peer was already added to collection: {peer.Node.Id}");
                            return;
                        }

                        var result = await InitializePeerConnection(peer);
                        if (!result)
                        {
                            peer.NodeStats.AddNodeStatsEvent(NodeStatsEventType.ConnectionFailed);
                            Interlocked.Increment(ref failedInitialConnect);
                            RemoveActivePeer(peer.Node.Id, "Failed to initialize connections");
                            if (peer.Session != null)
                            {
                                if (_logger.IsTrace) _logger.Trace($"Timeout, doing additional disconnect: {peer.Node.Id}");
                                peer.Session?.DisconnectAsync(DisconnectReason.ReceiveMessageTimeout, DisconnectType.Local);
                            }
                            return;
                        }

                        Interlocked.Increment(ref newActiveNodes);
                    });

                    _perfService.EndPerfCalc(key, "RunPeerUpdate");
                    connectionRounds++;
                }
                
                if (_logger.IsDebug)
                {
                    var activePeersCount = _activePeers.Count;
                    if (activePeersCount != _prevActivePeersCount)
                    {
                        var countersLog = string.Join(", ", candidateSelection.Counters.Select(x => $"{x.Key.ToString()}: {x.Value}"));
                        _logger.Debug($"RunPeerUpdate | {countersLog}, Incompatible: {GetIncompatibleDesc(candidateSelection.IncompatiblePeers)}, EligibleCandidates: {candidateSelection.Candidates.Count()}, " +
                                      $"Tried: {tryCount}, Rounds: {connectionRounds}, Failed initial connect: {failedInitialConnect}, Established initial connect: {newActiveNodes}, Current candidate peers: {_candidatePeers.Count}, Current active peers: {_activePeers.Count}");
                    }
                    _prevActivePeersCount = activePeersCount;
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
            }
            finally
            {
                _isPeerUpdateInProgress = false;
            }
        }

        private bool AddActivePeer(NodeId nodeId, Peer peer, string reason)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| ADDING {nodeId} {reason}");
            return _activePeers.TryAdd(nodeId, peer);
        }

        private void RemoveActivePeer(NodeId nodeId, string reason)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| REMOVING {nodeId} {reason}");
            _activePeers.TryRemove(nodeId, out _);
        }

        private (IEnumerable<Peer> Candidates, IDictionary<ActivePeerSelectionCounter, int> Counters, IEnumerable<Peer> IncompatiblePeers) SelectCandidates()
        {
            var counters = Enum.GetValues(typeof(ActivePeerSelectionCounter)).OfType<ActivePeerSelectionCounter>().ToDictionary(x => x, y => 0);
            var candidates = new List<Peer>();
            var incompatiblePeers = new List<Peer>();
            var availableActiveCount = _networkConfig.ActivePeersMaxCount - _activePeers.Count;
            if (availableActiveCount <= 0)
            {
                return (candidates, counters, incompatiblePeers);
            }

            var candidatesSnapshot = _candidatePeers.Where(x => !_activePeers.ContainsKey(x.Key)).ToArray();
            if (!candidatesSnapshot.Any())
            {
                return (candidates, counters, incompatiblePeers);
            }

            counters[ActivePeerSelectionCounter.AllNonActiveCandidates] = candidatesSnapshot.Length;

            for (var i = 0; i < candidatesSnapshot.Length; i++)
            {
                var candidate = candidatesSnapshot[i];
                if (candidate.Value.Node.Port == 0)
                {
                    counters[ActivePeerSelectionCounter.FilteredByZeroPort] = counters[ActivePeerSelectionCounter.FilteredByZeroPort] + 1;
                    continue;
                }

                if (!CheckLastDisconnectTime(candidate.Value))
                {
                    counters[ActivePeerSelectionCounter.FilteredByDisconnect] = counters[ActivePeerSelectionCounter.FilteredByDisconnect] + 1;
                    continue;
                }

                if (!CheckLastFailedConnectionTime(candidate.Value))
                {
                    counters[ActivePeerSelectionCounter.FilteredByFailedConnection] = counters[ActivePeerSelectionCounter.FilteredByFailedConnection] + 1;
                    continue;
                }

                if (candidate.Value.NodeStats.FailedCompatibilityValidation.HasValue)
                {
                    incompatiblePeers.Add(candidate.Value);
                    continue;
                }

                candidates.Add(candidate.Value);
            }

            return (candidates.OrderBy(x => x.NodeStats.IsTrustedPeer).ThenByDescending(x => x.NodeStats.CurrentNodeReputation).ToArray(), counters, incompatiblePeers);
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
                    x => new {ChainName = ChainId.GetChainName((int) x.Key), Count = x.Count()}).ToArray();
                var clients = peers.Where(x => x.NodeStats.P2PNodeDetails != null).Select(x => x.NodeStats.P2PNodeDetails.ClientId).GroupBy(x => x).Select(
                    x => new {ClientId = x.Key, Count = x.Count()}).ToArray();
                var remoteDisconnect = peers.Count(x => x.NodeStats.EventHistory.Any(y => y.DisconnectDetails != null && y.DisconnectDetails.DisconnectType == DisconnectType.Remote));

                _logger.Info($"Session stats: peers count with each EVENT:{Environment.NewLine}" +
                             $"{string.Join(Environment.NewLine, eventStats.Select(x => $"{x.EventType.ToString()}:{x.Count}"))}{Environment.NewLine}" +
                             $"Remote disconnect: {remoteDisconnect}{Environment.NewLine}{Environment.NewLine}" +
                             $"CHAINS: {Environment.NewLine}" +
                             $"{string.Join(Environment.NewLine, chains.Select(x => $"{x.ChainName}:{x.Count}"))}{Environment.NewLine}{Environment.NewLine}" +
                             $"CLIENTS:{Environment.NewLine}" +
                             $"{string.Join(Environment.NewLine, clients.Select(x => $"{x.ClientId}:{x.Count}"))}{Environment.NewLine}");

                var peersWithLatencyStats = peers.Where(x => x.NodeStats.LatencyHistory.Any()).ToArray();
                if (peersWithLatencyStats.Any())
                {
                    LogLatencyComparison(peersWithLatencyStats);
                }

                if (_networkConfig.CaptureNodeStatsEventHistory)
                {
                    _logger.Debug($"Logging {peers.Length} peers log event histories");

                    foreach (var peer in peers)
                    {
                        LogEventHistory(peer.NodeStats);
                    }

                    _logger.Debug("Logging event histories finished");
                }
            }
        }

        private void LogLatencyComparison(Peer[] peers)
        {
            var latencyDict = peers.Select(x => new {x,  Av = GetAverageLatencies(x.NodeStats)}).OrderBy(x => x.Av.Select(y => new {y.Key, y.Value}).FirstOrDefault(y => y.Key == NodeLatencyStatType.BlockHeaders)?.Value ?? 10000);
            _logger.Info($"Overall latency stats: {Environment.NewLine}{string.Join(Environment.NewLine, latencyDict.Select(x => $"{x.x.Node.Id}: {string.Join(" | ", x.Av.Select(y => $"{y.Key.ToString()}: {y.Value?.ToString() ?? "-"}"))}"))}");
        }

        private string GetIncompatibleDesc(IEnumerable<Peer> incompatibleNodes)
        {
            if (!incompatibleNodes.Any())
            {
                return "0";
            }

            var validationGroups = incompatibleNodes.GroupBy(x => x.NodeStats.FailedCompatibilityValidation).ToArray();
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
            if (!result && _logger.IsTrace)
            {
                _logger.Trace($"Skipping connection to peer, due to failed connection delay, time from last failed connection: {timePassed}, delay: {_networkConfig.FailedConnectionDelay}, peer: {peer.Node.Id}");
            }

            return result;
        }

        private async Task<bool> InitializePeerConnection(Peer candidate)
        {
            try
            {
                await _rlpxPeer.ConnectAsync(candidate.Node.Id, candidate.Node.Host, candidate.Node.Port, candidate.NodeStats);
                return true;
            }
            catch (NetworkingException ex)
            {
                if (_logger.IsTrace) _logger.Trace($"Cannot connect to Peer [{ex.NetwokExceptionType.ToString()}]: {candidate.Node.Id}");
                return false;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Error trying to initiate connetion with peer: {candidate.Node.Id}", e);
                return false;
            }
        }

        private void LoadPeersFromDb()
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

        private void LoadConfiguredTrustedPeers()
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

        private void OnProtocolInitialized(object sender, ProtocolInitializedEventArgs e)
        {
            //Fire and forget
            Task.Run(async () => await OnProtocolInitializedAsync(sender, e));
        }

        private async Task OnProtocolInitializedAsync(object sender, ProtocolInitializedEventArgs e)
        {
            var session = (IP2PSession) sender;

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Protocol initialized {e.ProtocolHandler.ProtocolCode} {e.ProtocolHandler.ProtocolVersion}, Node: {session.RemoteNodeId}");

            if (!_activePeers.TryGetValue(session.RemoteNodeId, out var peer))
            {
                if (_candidatePeers.TryGetValue(session.RemoteNodeId, out var candidatePeer))
                {
                    if (e.ProtocolHandler is P2PProtocolHandler)
                    {
                        AddNodeToDiscovery(candidatePeer, (P2PProtocolInitializedEventArgs) e);
                    }

                    if (_logger.IsTrace)
                    {
                        var timeFromLastDisconnect = candidatePeer.NodeStats.LastDisconnectTime.HasValue
                            ? DateTime.Now.Subtract(candidatePeer.NodeStats.LastDisconnectTime.Value).TotalMilliseconds.ToString()
                            : "no disconnect";

                        _logger.Trace($"Protocol initialized for peer not present in active collection, id: {session.RemoteNodeId}, time from last disconnect: {timeFromLastDisconnect}.");
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
                    var eth62EventArgs = (Eth62ProtocolInitializedEventArgs) e;
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

                    if (_logger.IsTrace) _logger.Trace($"Eth62 initialized, adding sync peer: {peer.Node.Id}");

                    //Add/Update peer to the storage and to sync manager
                    _peerStorage.UpdateNodes(new[] {new NetworkNode(peer.Node.Id.PublicKey, peer.Node.Host, peer.Node.Port, peer.Node.Description, peer.NodeStats.NewPersistedNodeReputation)});
                    await _synchronizationManager.AddPeer(ethProtocolhandler);

                    break;
            }

            if (_logger.IsTrace) _logger.Trace($"Protocol Initialized: {session.RemoteNodeId}, {e.ProtocolHandler.GetType().Name}");
        }

        /// <summary>
        /// In case of IN connection we dont know what is the port node is listening on until we receive the Hello message
        /// </summary>
        private void AddNodeToDiscovery(Peer peer, P2PProtocolInitializedEventArgs eventArgs)
        {
            if (eventArgs.ListenPort == 0)
            {
                if (_logger.IsTrace) _logger.Trace($"Listen port is 0, node is not listening: {peer.Node.Id}, ConnectionType: {peer.Session.ConnectionDirection}, nodePort: {peer.Node.Port}");
                return;
            }

            if (peer.Node.Port != eventArgs.ListenPort)
            {
                if (_logger.IsDebug) _logger.Debug($"Updating listen port for node: {peer.Node.Id}, ConnectionType: {peer.Session.ConnectionDirection}, from: {peer.Node.Port} to: {eventArgs.ListenPort}");
                
                if (peer.AddedToDiscovery)
                {
                    if (_logger.IsError) _logger.Error($"Discovery note already initiialized with wrong port, nodeId: {peer.Node.Id}, port: {peer.Node.Port}, listen port: {eventArgs.ListenPort}");
                }
                peer.Node.Port = eventArgs.ListenPort;
            }

            AddNodeToDiscovery(peer);
        }

        private void AddNodeToDiscovery(Peer peer)
        {
            if (!_isDiscoveryEnabled || peer.AddedToDiscovery)
            {
                return;
            }

            //In case peer was initiated outside of discovery and discovery is enabled, we are adding it to discovery for future use (e.g. trusted peer)
            _discoveryApp.AddNodeToDiscovery(peer.Node);
            peer.AddedToDiscovery = true;
        }

        private void ProcessOutgoingConnection(IP2PSession session)
        {
            var id = session.RemoteNodeId;

            if (!_activePeers.TryGetValue(id, out Peer peer))
            {
                if (_logger.IsError)
                {
                    _logger.Error($"Initiated rlpx connection (out) with Peer without adding it to Active collection: {id}");
                }

                return;
            }

            peer.NodeStats.AddNodeStatsEvent(NodeStatsEventType.ConnectionEstablished);
            peer.ConnectionDirection = session.ConnectionDirection;
            peer.Session = session;

            if (_logger.IsTrace) _logger.Trace($"Initializing OUT connection (PeerManager) for peer: {session.RemoteNodeId}");
            AddNodeToDiscovery(peer);
        }

        private async Task ProcessIncomingConnection(IP2PSession session)
        {
            // if we have already initiated connection before
            if (_activePeers.ContainsKey(session.RemoteNodeId))
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Initiating disconnect, node is already connected: {session.RemoteNodeId}");
                }

                await session.InitiateDisconnectAsync(DisconnectReason.AlreadyConnected);
                return;
            }

            // if we have too many active peers
            if (_activePeers.Count >= _networkConfig.ActivePeersMaxCount)
            {
                if (_logger.IsTrace) _logger.Trace($"Initiating disconnect, we have too many peers: {session.RemoteNodeId}");
                await session.InitiateDisconnectAsync(DisconnectReason.TooManyPeers);
                return;
            }
            
            // it is possible we already have this node as a candidate
            if (_candidatePeers.TryGetValue(session.RemoteNodeId, out var peer))
            {
                peer.Session = session;
                peer.ConnectionDirection = session.ConnectionDirection;
            }
            else
            {
                var node = _nodeFactory.CreateNode(session.RemoteNodeId, session.RemoteHost, session.RemotePort ?? 0);
                peer = new Peer(node, _nodeStatsProvider.GetOrAddNodeStats(node))
                {
                    ConnectionDirection = session.ConnectionDirection,
                    Session = session
                };
            }

            if (AddActivePeer(session.RemoteNodeId, peer, "incoming connection"))
            {
                peer.NodeStats.AddNodeStatsHandshakeEvent(ConnectionDirection.In);

                // we also add this node to candidates for future connection (if we dont have it yet)
                _candidatePeers.TryAdd(session.RemoteNodeId, peer);

                return;
            }

            // if we have already initiated connection before (threding safeguard - it means another thread added this node to active collection after our contains key key check above)
            if (_logger.IsTrace) _logger.Trace($"Initiating disconnect, node is already connected: {session.RemoteNodeId}");

            await session.InitiateDisconnectAsync(DisconnectReason.AlreadyConnected);
        }

        private async Task<bool> ValidateProtocol(string protocol, Peer peer, ProtocolInitializedEventArgs eventArgs)
        {
            //TODO add validation for clientId - e.g. get only ethereumJ clients
            switch (protocol)
            {
                case Protocol.P2P:
                    var args = (P2PProtocolInitializedEventArgs) eventArgs;
                    if (!ValidateP2PVersion(args.P2PVersion))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {peer.Node.Id}, incorrect P2PVersion: {args.P2PVersion}");
                        peer.NodeStats.FailedCompatibilityValidation = CompatibilityValidationType.P2PVersion;
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.IncompatibleP2PVersion);
                        return false;
                    }

                    if (!ValidateCapabilities(args.Capabilities))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {peer.Node.Id}, no Eth62 capability, supported capabilities: [{string.Join(",", args.Capabilities.Select(x => $"{x.ProtocolCode}v{x.Version}"))}]");
                        peer.NodeStats.FailedCompatibilityValidation = CompatibilityValidationType.Capabilities;
                        //TODO confirm disconnect reason
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.Other);
                        return false;
                    }

                    break;
                case Protocol.Eth:
                    var ethArgs = (Eth62ProtocolInitializedEventArgs) eventArgs;
                    if (!ValidateChainId(ethArgs.ChainId))
                    {
                        if (_logger.IsTrace)
                        {
                            _logger.Trace($"Initiating disconnect with peer: {peer.Node.Id}, different chainId: {ChainId.GetChainName((int) ethArgs.ChainId)}, our chainId: {ChainId.GetChainName(_synchronizationManager.ChainId)}");
                        }

                        peer.NodeStats.FailedCompatibilityValidation = CompatibilityValidationType.ChainId;
                        //TODO confirm disconnect reason
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.Other);
                        return false;
                    }

                    if (ethArgs.GenesisHash != _synchronizationManager.Genesis?.Hash)
                    {
                        if (_logger.IsTrace)
                        {
                            _logger.Trace($"Initiating disconnect with peer: {peer.Node.Id}, different genesis hash: {ethArgs.GenesisHash}, our: {_synchronizationManager.Genesis?.Hash}");
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

        private void OnPeerDisconnected(object sender, DisconnectEventArgs e)
        {
            var session = (IP2PSession) sender;
            if (_logger.IsTrace)
            {
                _logger.Trace($"|NetworkTrace| Peer disconnected event in PeerManager: {session.RemoteNodeId}, disconnectReason: {e.DisconnectReason}, disconnectType: {e.DisconnectType}");
            }

            if (session.RemoteNodeId == null)
            {
                if (_logger.IsWarn) _logger.Warn($"Disconnect on session with no RemoteNodeId, sessionId: {session.SessionId}");
                return;
            }

            if (_activePeers.TryGetValue(session.RemoteNodeId, out var activePeer))
            {
                if (activePeer.Session?.SessionId != session.SessionId)
                {
                    if (_logger.IsTrace)
                    {
                        _logger.Trace($"Received disconnect on a different session than the active peer runs. Ignoring. Id: {activePeer.Node.Id}");
                    }

                    //TODO verify we do not want to change reputation here
                    return;
                }

                RemoveActivePeer(session.RemoteNodeId, "peer disconnected");
                activePeer.NodeStats.AddNodeStatsDisconnectEvent(e.DisconnectType, e.DisconnectReason);
                if (activePeer.SynchronizationPeer != null)
                {
                    _synchronizationManager.RemovePeer(activePeer.SynchronizationPeer);
                }

                if (_logger.IsTrace)
                {
                    _logger.Trace($"Removing Active Peer on disconnect {session.RemoteNodeId}");
                }

                if (_logger.IsTrace && _networkConfig.CaptureNodeStatsEventHistory)
                {
                    LogEventHistory(activePeer.NodeStats);
                }

                if (_isStarted)
                {
                    //Fire and forget
                    Task.Run(() => RunPeerUpdateSync());
                }
            }
        }

        private void OnHandshakeComplete(IP2PSession session)
        {
            //Fire and forget
            Task.Run(async () => await OnHandshakeCompleteAsync(session));
        }

        private async Task OnHandshakeCompleteAsync(IP2PSession session)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| OnHandshakeComplete: {session.RemoteNodeId}, {session.ConnectionDirection.ToString()}");

            //This is the first moment we get confirmed publicKey of remote node in case of incoming connections
            if (session.ConnectionDirection == ConnectionDirection.In)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Handshake initialized {session.ConnectionDirection.ToString().ToUpper()} channel {session.RemoteNodeId}@{session.RemoteHost}:{session.RemotePort}");
                }

                await ProcessIncomingConnection(session);
            }
            else
            {
                if (!_activePeers.TryGetValue(session.RemoteNodeId, out Peer peer))
                {
                    //Can happen when peer sent Disconnect message before handshake is done, it takes us a while to disconnect
                    if (_logger.IsTrace)
                    {
                        _logger.Trace($"Initiated Handshake (OUT) with Peer without adding it to Active collection: {session.RemoteNodeId}");
                    }

                    return;
                }

                peer.NodeStats.AddNodeStatsHandshakeEvent(ConnectionDirection.Out);
            }

            if (_logger.IsTrace)
            {
                _logger.Trace($"Handshake initialized for peer: {session.RemoteNodeId}");
            }
        }

        private void OnNodeDiscovered(object sender, NodeEventArgs nodeEventArgs)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| OnNodeDiscovered {nodeEventArgs.Node.Id}");

            var id = nodeEventArgs.Node.Id;
            if (_candidatePeers.ContainsKey(id))
            {
                return;
            }

            var peer = new Peer(nodeEventArgs.Node, nodeEventArgs.NodeStats)
            {
                AddedToDiscovery = true
            };
            if (!_candidatePeers.TryAdd(id, peer))
            {
                return;
            }

            peer.NodeStats.AddNodeStatsEvent(NodeStatsEventType.NodeDiscovered);

            if (_logger.IsTrace)
            {
                _logger.Trace($"Adding newly discovered node to Candidates collection {id}@{nodeEventArgs.Node.Host}:{nodeEventArgs.Node.Port}");
            }

            if (_isStarted)
            {
                //Fire and forget
                Task.Run(RunPeerUpdate);
            }
        }

        private void StartActivePeersTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting active peers timer");
            _activePeersTimer = new System.Timers.Timer(_networkConfig.ActivePeerUpdateInterval) {AutoReset = false};
            _activePeersTimer.Elapsed += (sender, e) =>
            {
                _activePeersTimer.Enabled = false;
                RunPeerUpdateSync();
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

            _peerPersistanceTimer = new System.Timers.Timer(_networkConfig.PeersPersistanceInterval) {AutoReset = false};
            _peerPersistanceTimer.Elapsed += (sender, e) =>
            {
                _peerPersistanceTimer.Enabled = false;
                RunPeerCommit();
                _peerPersistanceTimer.Enabled = true;
            };

            _peerPersistanceTimer.Start();
        }

        private void StopPeerPersistanceTimer()
        {
            try
            {
                if (_logger.IsDebug) _logger.Debug("Stopping peer persistance timer");
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

            _pingTimer = new System.Timers.Timer(_networkConfig.P2PPingInterval) {AutoReset = false};
            _pingTimer.Elapsed += (sender, e) =>
            {
                _pingTimer.Enabled = false;
                SendPingMessages();
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
            try
            {
                UpdateCurrentlyStoredPeersReputation();

                if (!_peerStorage.AnyPendingChange())
                {
                    if (_logger.IsTrace) _logger.Trace("No changes in peer storage, skipping commit.");
                    return;
                }

                _storageCommitTask = Task.Run(() =>
                {
                    _peerStorage.Commit();
                    _peerStorage.StartBatch();
                });


                var task = _storageCommitTask.ContinueWith(x =>
                {
                    if (x.IsFaulted && _logger.IsError)
                    {
                        _logger.Error($"Error during peer storage commit: {x.Exception}");
                    }
                });
                task.Wait();
                _storageCommitTask = null;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during peer storage commit: {ex}");
            }
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

        private void SendPingMessages()
        {
            var task = Task.Run(SendPingMessagesAsync).ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError)
                {
                    _logger.Error($"Error during send ping messages: {x.Exception}");
                }
            });
            task.Wait();
        }

        private async Task SendPingMessagesAsync()
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

            if (_logger.IsTrace) _logger.Trace($"Disconnecting due to missed ping messages: {peer.Session.RemoteNodeId}");
            await peer.Session.InitiateDisconnectAsync(DisconnectReason.ReceiveMessageTimeout);
            return false;
        }

        private void OnSyncEvent(object sender, SyncEventArgs e)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Sync Event: {e.SyncStatus.ToString()}, NodeId: {e.Peer.NodeId}");

            if (_activePeers.TryGetValue(e.Peer.NodeId, out var activePeer) && activePeer.Session != null)
            {
                var nodeStatsEvent = GetSyncEventType(e.SyncStatus);
                activePeer.NodeStats.AddNodeStatsSyncEvent(nodeStatsEvent, new SyncNodeDetails
                {
                    NodeBestBlockNumber = e.NodeBestBlockNumber,
                    OurBestBlockNumber = e.OurBestBlockNumber
                });

                if (new []{ SyncStatus.InitFailed, SyncStatus.InitCancelled, SyncStatus.Failed, SyncStatus.Cancelled }.Contains(e.SyncStatus))
                {
                    if (_logger.IsDebug) _logger.Debug($"Initializing disconnect on sync {e.SyncStatus.ToString()} with node: {e.Peer.NodeId}");
                    RemoveActivePeer(e.Peer.NodeId, $"Sync event: {e.SyncStatus.ToString()}");
                    //Fire and forget
                    Task.Run(() => activePeer.Session.InitiateDisconnectAsync(DisconnectReason.Other));
                }
            }
            else if (_logger.IsTrace) _logger.Trace($"Sync failed, peer not in active collection: {e.Peer.NodeId}");
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
                sb.AppendLine($"Eth62 details: ChainId: {ChainId.GetChainName((int) nodeStats.Eth62NodeDetails.ChainId)}, TotalDifficulty: {nodeStats.Eth62NodeDetails.TotalDifficulty}");
            }

            foreach (var statsEvent in nodeStats.EventHistory.OrderBy(x => x.EventDate).ToArray())
            {
                sb.Append($"{statsEvent.EventDate.ToString(_networkConfig.DetailedTimeDateFormat)} | {statsEvent.EventType}");
                if (statsEvent.ConnectionDirection.HasValue)
                {
                    sb.Append($" | {statsEvent.ConnectionDirection.Value.ToString()}");
                }

                if (statsEvent.P2PNodeDetails != null)
                {
                    sb.Append($" | {statsEvent.P2PNodeDetails.ClientId} | v{statsEvent.P2PNodeDetails.P2PVersion} | {GetCapabilities(statsEvent.P2PNodeDetails)}");
                }

                if (statsEvent.Eth62NodeDetails != null)
                {
                    sb.Append($" | {ChainId.GetChainName((int) statsEvent.Eth62NodeDetails.ChainId)} | TotalDifficulty:{statsEvent.Eth62NodeDetails.TotalDifficulty}");
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

            if (nodeStats.LatencyHistory.Any())
            {
                sb.AppendLine("Latency averages:");
                var averageLatencies = GetAverageLatencies(nodeStats);
                foreach (var latency in averageLatencies.Where(x => x.Value.HasValue))
                {
                    sb.AppendLine($"{latency.Key.ToString()} = {latency.Value}");
                }

                sb.AppendLine("Latency events:");
                foreach (var statsEvent in nodeStats.LatencyHistory.OrderBy(x => x.StatType).ThenBy(x => x.CaptureTime).ToArray())
                {
                    sb.AppendLine($"{statsEvent.StatType.ToString()} | {statsEvent.CaptureTime.ToString(_networkConfig.DetailedTimeDateFormat)} | {statsEvent.Latency}");
                }
            }

            _logger.Debug(sb.ToString());
        }

        private static Dictionary<NodeLatencyStatType, long?> GetAverageLatencies(INodeStats nodeStats)
        {
            return Enum.GetValues(typeof(NodeLatencyStatType)).OfType<NodeLatencyStatType>().ToDictionary(x => x, nodeStats.GetAverageLatency);
        }

        private string GetCapabilities(P2PNodeDetails nodeDetails)
        {
            if (nodeDetails.Capabilities == null || !nodeDetails.Capabilities.Any())
            {
                return "none";
            }

            return string.Join("|", nodeDetails.Capabilities.Select(x => $"{x.ProtocolCode}v{x.Version}"));
        }

        internal class Peer
        {
            public Peer(Node node, INodeStats nodeStats)
            {
                Node = node;
                //NodeLifecycleManager = manager;
                NodeStats = nodeStats;
            }

            public Node Node { get; }
            public bool AddedToDiscovery { get; set; }
            //public INodeLifecycleManager NodeLifecycleManager { get; set; }
            public INodeStats NodeStats { get; }
            public IP2PSession Session { get; set; }
            public ISynchronizationPeer SynchronizationPeer { get; set; }
            public IP2PMessageSender P2PMessageSender { get; set; }
            public ConnectionDirection ConnectionDirection { get; set; }
        }
    }
}