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
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    /// <summary>
    /// To be removed as soon as the new one is confirmed to work.
    /// You can safely remove this entire class if it is still here on 15th of Feb 2019 and is not used. 
    /// </summary>
    public class OldPeerManager : IPeerManager
    {
        private readonly ILogger _logger;

        private readonly IDiscoveryApp _discoveryApp;
        private readonly INetworkConfig _networkConfig;
        private readonly IRlpxPeer _rlpxPeer;
        private readonly ISynchronizationManager _synchronizationManager;
        private readonly INodeStatsProvider _nodeStatsProvider;
        private readonly INetworkStorage _peerStorage;
        private readonly INodeFactory _nodeFactory;
        private readonly IPeerSessionLogger _peerSessionLogger;
        private System.Timers.Timer _activePeersTimer;
        private System.Timers.Timer _peerPersistenceTimer;
        private System.Timers.Timer _pingTimer;
        private int _logCounter = 1;
        private bool _isStarted;
        private bool _isPeerUpdateInProgress;
        private readonly object _isPeerUpdateInProgressLock = new object();
        private readonly IPerfService _perfService;
        private readonly ITransactionPool _transactionPool;
        private bool _isDiscoveryEnabled;
        private Task _storageCommitTask;
        private long _prevActivePeersCount;

        private readonly ConcurrentDictionary<NodeId, Peer> _activePeers = new ConcurrentDictionary<NodeId, Peer>();
        private readonly ConcurrentDictionary<NodeId, Peer> _candidatePeers = new ConcurrentDictionary<NodeId, Peer>();

        public OldPeerManager(
            IRlpxPeer rlpxPeer,
            IDiscoveryApp discoveryApp,
            ISynchronizationManager synchronizationManager,
            INodeStatsProvider nodeStatsProvider,
            INetworkStorage peerStorage,
            INodeFactory nodeFactory,
            IConfigProvider configurationProvider,
            IPerfService perfService,
            ITransactionPool transactionPool,
            ILogManager logManager,
            IPeerSessionLogger peerSessionLogger)
        {
            _logger = logManager.GetClassLogger();
            _networkConfig = configurationProvider.GetConfig<INetworkConfig>();
            _rlpxPeer = rlpxPeer ?? throw new ArgumentNullException(nameof(rlpxPeer));
            _synchronizationManager =
                synchronizationManager ?? throw new ArgumentNullException(nameof(synchronizationManager));
            _nodeStatsProvider = nodeStatsProvider ?? throw new ArgumentNullException(nameof(nodeStatsProvider));
            _discoveryApp = discoveryApp ?? throw new ArgumentNullException(nameof(discoveryApp));
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _nodeFactory = nodeFactory ?? throw new ArgumentNullException(nameof(nodeFactory));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _peerSessionLogger = peerSessionLogger ?? throw new ArgumentNullException(nameof(peerSessionLogger));
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
            LoadConfiguredBootnodes();

            _rlpxPeer.SessionCreated += (sender, args) =>
            {
                var session = args.Session;
                session.PeerDisconnected += OnPeerDisconnected;
                session.ProtocolInitialized += OnProtocolInitialized;
                session.HandshakeComplete += OnHandshakeComplete;

                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Session created: {session.RemoteNodeId}, {session.ConnectionDirection.ToString()}");
                if (session.ConnectionDirection == ConnectionDirection.Out)
                {
                    ProcessOutgoingConnection(session);
                }
            };
            
            _rlpxPeer.SessionClosing += (sender, args) =>
            {
                var session = args.Session;
                session.PeerDisconnected -= OnPeerDisconnected;
                session.ProtocolInitialized -= OnProtocolInitialized;
                session.HandshakeComplete -= OnHandshakeComplete;
                session.Dispose();

                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Session closing: {session.RemoteNodeId}, {session.ConnectionDirection.ToString()}");
            };
        }

        public void Start()
        {
            // timer is needed to support reconnecting, event based connection is also supported
            if (_networkConfig.IsActivePeerTimerEnabled)
            {
                StartActivePeersTimer();
            }

            StartPeerPersistenceTimer();
            StartPingTimer();

            _isStarted = true;
            try
            {
                RunPeerUpdateSync();
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Error during initial Peer update", e);
            }
        }

        public async Task StopAsync(ExitType exitType)
        {
            var key = _perfService.StartPerfCalc();
            _cancellationTokenSource.Cancel();

            if (_networkConfig.IsActivePeerTimerEnabled)
            {
                StopActivePeersTimer();
            }

            StopPeerPersistenceTimer();
            StopPingTimer();

            var closingTasks = new List<Task>();

            if (_storageCommitTask != null)
            {
                var storageCloseTask = _storageCommitTask.ContinueWith(x =>
                {
                    if (x.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error("Error during peer persistence stop.", x.Exception);
                    }
                });

                closingTasks.Add(storageCloseTask);
            }

            await Task.WhenAll(closingTasks);

            if (_logger.IsInfo) LogSessionStats(exitType == ExitType.DetailLogExit);
            
            if (_logger.IsInfo) _logger.Info("Peer Manager shutdown complete.. please wait for all components to close");
            _perfService.EndPerfCalc(key, "Close: PeerManager");
        }

        public void LogSessionStats(bool logEventDetails)
        {
            _peerSessionLogger.LogSessionStats(ActivePeers, CandidatePeers, logEventDetails);
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
            try
            {
                lock (_isPeerUpdateInProgressLock)
                {
                    if (_isPeerUpdateInProgress)
                    {
                        return;
                    }

                    _isPeerUpdateInProgress = true;
                }

                //_logger.Info($"TESTTEST All active peers: {_activePeers.Count}, can: {_candidatePeers.Count}, peers: IN: {_activePeers.Values.Count(x => x.ConnectionDirection == ConnectionDirection.In)}, OUT: {_activePeers.Values.Count(x => x.ConnectionDirection == ConnectionDirection.Out)}");

                int availableActiveCount = _networkConfig.ActivePeersMaxCount - _activePeers.Count;
                if (availableActiveCount == 0)
                {
                    return;
                }

                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                int tryCount = 0;
                int newActiveNodes = 0;
                int failedInitialConnect = 0;
                int connectionRounds = 0;

                var candidateSelection = SelectAndRankCandidates();
                IReadOnlyCollection<Peer> remainingCandidates = candidateSelection.Candidates;
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

                    Guid perfCalcKey = _perfService.StartPerfCalc();

                    availableActiveCount = _networkConfig.ActivePeersMaxCount - _activePeers.Count;
                    int nodesToTry = Math.Min(remainingCandidates.Count, availableActiveCount);
                    if (nodesToTry == 0)
                    {
                        break;
                    }

                    IEnumerable<Peer> candidatesToTry = remainingCandidates.Take(nodesToTry);
                    remainingCandidates = remainingCandidates.Skip(nodesToTry).ToList();
                    Parallel.ForEach(candidatesToTry, async (peer, loopState) =>
                    {
                        if (loopState.ShouldExitCurrentIteration || _cancellationTokenSource.IsCancellationRequested)
                        {
                            return;
                        }

                        Interlocked.Increment(ref tryCount);

                        // Can happen when In connection is received from the same peer and is initialized before we get here
                        // In this case we do not initialize OUT connection
                        if (!AddActivePeer(peer.Node.Id, peer, "upgrading candidate"))
                        {
                            if (_logger.IsTrace) _logger.Trace($"Active peer was already added to collection: {peer.Node.Id}");
                            return;
                        }

                        bool result = await InitializePeerConnection(peer);
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

                    _perfService.EndPerfCalc(perfCalcKey, "RunPeerUpdate");
                    connectionRounds++;
                }

                if (_logger.IsDebug)
                {
                    int activePeersCount = _activePeers.Count;
                    if (activePeersCount != _prevActivePeersCount)
                    {
                        string countersLog = string.Join(", ", candidateSelection.Counters.Select(x => $"{x.Key.ToString()}: {x.Value}"));
                        _logger.Debug($"RunPeerUpdate | {countersLog}, Incompatible: {GetIncompatibleDesc(candidateSelection.IncompatiblePeers)}, EligibleCandidates: {candidateSelection.Candidates.Count()}, " +
                                      $"Tried: {tryCount}, Rounds: {connectionRounds}, Failed initial connect: {failedInitialConnect}, Established initial connect: {newActiveNodes}, " +
                                      $"Current candidate peers: {_candidatePeers.Count}, Current active peers: {_activePeers.Count} " +
                                      $"[Out: {_activePeers.Count(x => x.Value.ConnectionDirection == ConnectionDirection.Out)} | " +
                                      $"In: {_activePeers.Count(x => x.Value.ConnectionDirection == ConnectionDirection.In)}]");
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

        private (IReadOnlyCollection<Peer> Candidates, IDictionary<ActivePeerSelectionCounter, int> Counters, IReadOnlyCollection<Peer> IncompatiblePeers) SelectAndRankCandidates()
        {
            var counters = Enum.GetValues(typeof(ActivePeerSelectionCounter)).OfType<ActivePeerSelectionCounter>().ToDictionary(x => x, y => 0);
            var availableActiveCount = _networkConfig.ActivePeersMaxCount - _activePeers.Count;
            if (availableActiveCount <= 0)
            {
                return (Array.Empty<Peer>(), counters, Array.Empty<Peer>());
            }

            var candidatesSnapshot = _candidatePeers.Where(x => !_activePeers.ContainsKey(x.Key)).ToArray();
            if (!candidatesSnapshot.Any())
            {
                return (Array.Empty<Peer>(), counters, Array.Empty<Peer>());
            }

            counters[ActivePeerSelectionCounter.AllNonActiveCandidates] = candidatesSnapshot.Length;

            List<Peer> candidates = new List<Peer>();
            List<Peer> incompatiblePeers = new List<Peer>();
            for (int i = 0; i < candidatesSnapshot.Length; i++)
            {
                var candidate = candidatesSnapshot[i];
                if (candidate.Value.Node.Port == 0)
                {
                    counters[ActivePeerSelectionCounter.FilteredByZeroPort] = counters[ActivePeerSelectionCounter.FilteredByZeroPort] + 1;
                    continue;
                }

                var delayResult = candidate.Value.NodeStats.IsConnectionDelayed();
                if (delayResult.Result)
                {
                    if (delayResult.DelayReason == NodeStatsEventType.Disconnect)
                    {
                        counters[ActivePeerSelectionCounter.FilteredByDisconnect] = counters[ActivePeerSelectionCounter.FilteredByDisconnect] + 1;
                    }
                    else if (delayResult.DelayReason == NodeStatsEventType.ConnectionFailed)
                    {
                        counters[ActivePeerSelectionCounter.FilteredByFailedConnection] = counters[ActivePeerSelectionCounter.FilteredByFailedConnection] + 1;
                    }

                    continue;
                }

                if (candidate.Value.NodeStats.FailedCompatibilityValidation.HasValue)
                {
                    incompatiblePeers.Add(candidate.Value);
                    continue;
                }

                candidates.Add(candidate.Value);
            }

            return (candidates.OrderBy(x => x.NodeStats.IsTrustedPeer).ThenByDescending(x => x.NodeStats.CurrentNodeReputation).ToList(), counters, incompatiblePeers);
        }

//        private void LogPeerEventHistory(Peer peer)
//        {
//            var log = GetEventHistoryLog(peer.NodeStats);
//            var fileName = Path.Combine(_eventLogsDirectoryPath, peer.Node.Id.PublicKey.ToString(), ".log");
//            File.AppendAllText(fileName, log);
//        }
//
//        private void LogLatencyComparison(Peer[] peers)
//        {
//            if(_logger.IsInfo)
//            {
//                var latencyDict = peers.Select(x => new {x, Av = GetAverageLatencies(x.NodeStats)}).OrderBy(x => x.Av.Select(y => new {y.Key, y.Value}).FirstOrDefault(y => y.Key == NodeLatencyStatType.BlockHeaders)?.Value ?? 10000);
//                _logger.Info($"Overall latency stats: {Environment.NewLine}{string.Join(Environment.NewLine, latencyDict.Select(x => $"{x.x.Node.Id}: {string.Join(" | ", x.Av.Select(y => $"{y.Key.ToString()}: {y.Value?.ToString() ?? "-"}"))}"))}");
//            }
//        }

        private string GetIncompatibleDesc(IReadOnlyCollection<Peer> incompatibleNodes)
        {
            if (!incompatibleNodes.Any())
            {
                return "0";
            }

            var validationGroups = incompatibleNodes.GroupBy(x => x.NodeStats.FailedCompatibilityValidation).ToArray();
            return $"[{string.Join(", ", validationGroups.Select(x => $"{x.Key.ToString()}:{x.Count()}"))}]";
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
                if (_logger.IsDebug) _logger.Error($"Error trying to initiate connection with peer: {candidate.Node.Id}", e);
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

            if (_logger.IsInfo) _logger.Info($"Initializing persisted peers: {peers.Length}.");

            foreach (var persistedPeer in peers)
            {
                if (_candidatePeers.ContainsKey(persistedPeer.NodeId))
                {
                    continue;
                }

                var node = _nodeFactory.CreateNode(persistedPeer.NodeId, persistedPeer.Host, persistedPeer.Port);
                var nodeStats = _nodeStatsProvider.GetOrAddNodeStats(node);
                nodeStats.CurrentPersistedNodeReputation = persistedPeer.Reputation;

                var peer = new Peer(node, nodeStats, ConnectionDirection.Out);
                if (!_candidatePeers.TryAdd(node.Id, peer))
                {
                    continue;
                }

                if (_logger.IsTrace) _logger.Trace($"Adding persisted peer to New collection {node.Id}@{node.Host}:{node.Port}");
            }
        }

        private void LoadConfiguredTrustedPeers()
        {
            var trustedPeers = _networkConfig.TrustedPeers;

            if (_logger.IsInfo)  _logger.Info($"Initializing trusted peers: {trustedPeers?.Length ?? 0}.");

            if (trustedPeers == null || !trustedPeers.Any())
            {
                return;
            }

            foreach (var trustedPeer in NetworkNode.ParseNodes(trustedPeers))
            {
                AddConfigNode(trustedPeer, true);
            }
        }

        private void LoadConfiguredBootnodes()
        {
            var bootnodes = _networkConfig.Bootnodes;

            if (_logger.IsInfo) _logger.Info($"Initializing bootnode peers: {bootnodes?.Length ?? 0}.");

            if (bootnodes == null || !bootnodes.Any())
            {
                return;
            }

            foreach (var node in NetworkNode.ParseNodes(bootnodes))
            {
                AddConfigNode(node);
            }
        }

        private void AddConfigNode(NetworkNode networkNode, bool isTrustedPeer = false)
        {
            var node = _nodeFactory.CreateNode(networkNode.NodeId, networkNode.Host, networkNode.Port);
            node.Description = networkNode.Description;

            var nodeStats = _nodeStatsProvider.GetOrAddNodeStats(node);
            nodeStats.IsTrustedPeer = isTrustedPeer;

            var peer = new Peer(node, nodeStats, ConnectionDirection.Out);
            if (_candidatePeers.TryAdd(node.Id, peer))
            {
                if (_logger.IsDebug) _logger.Debug($"Adding config peer ({(isTrustedPeer ? "trusted" : "bootnode")}) to New collection {node.Id}@{node.Host}:{node.Port}");
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

                    if (_logger.IsTrace) _logger.Trace($"Protocol initialized for peer not present in active collection, id: {session.RemoteNodeId}.");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Protocol initialized for peer not present in active collection, id: {session.RemoteNodeId}, peer not in candidate collection.");
                }

                //Initializing disconnect if it hasn't been done already - in case of e.g. timeout earlier and unexpected further connection
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
                case Eth62ProtocolHandler ethProtocolHandler: // note that this covers eth63 as well
                    var ethEventArgs = (EthProtocolInitializedEventArgs) e;
                    peer.NodeStats.AddNodeStatsEth62InitializedEvent(new EthNodeDetails
                    {
                        ChainId = ethEventArgs.ChainId,
                        BestHash = ethEventArgs.BestHash,
                        GenesisHash = ethEventArgs.GenesisHash,
                        Protocol = ethEventArgs.Protocol,
                        ProtocolVersion = ethEventArgs.ProtocolVersion,
                        TotalDifficulty = ethEventArgs.TotalDifficulty
                    });
                    result = await ValidateProtocol(Protocol.Eth, peer, e);
                    if (!result)
                    {
                        return;
                    }

                    //TODO move this outside, so syncManager have access to NodeStats and NodeDetails
                    ethProtocolHandler.ClientId = peer.NodeStats.P2PNodeDetails.ClientId;
                    peer.SynchronizationPeer = ethProtocolHandler;

                    if (_logger.IsTrace) _logger.Trace($"Eth version {ethProtocolHandler.ProtocolVersion} initialized, adding sync peer: {peer.Node.Id}");

                    //Add/Update peer to the storage and to sync manager
                    _peerStorage.UpdateNodes(new[] {new NetworkNode(peer.Node.Id.PublicKey, peer.Node.Host, peer.Node.Port, peer.Node.Description, peer.NodeStats.NewPersistedNodeReputation)});
                    await _synchronizationManager.AddPeer(ethProtocolHandler);
                    _transactionPool.AddPeer(ethProtocolHandler);

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
                    if (_logger.IsDebug) _logger.Debug($"Discovery node already initialized with wrong port, nodeId: {peer.Node.Id}, port: {peer.Node.Port}, listen port: {eventArgs.ListenPort}");
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
                if (_logger.IsError) _logger.Error($"Initiated rlpx connection (out) with Peer without adding it to Active collection: {id}");

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
                if (_logger.IsTrace) _logger.Trace($"Initiating disconnect, node is already connected: {session.RemoteNodeId}");

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
                peer = new Peer(node, _nodeStatsProvider.GetOrAddNodeStats(node), session.ConnectionDirection)
                {
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
                    var ethArgs = (EthProtocolInitializedEventArgs) eventArgs;
                    if (!ValidateChainId(ethArgs.ChainId))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {peer.Node.Id}, different chainId: {ChainId.GetChainName((int) ethArgs.ChainId)}, our chainId: {ChainId.GetChainName(_synchronizationManager.ChainId)}");

                        peer.NodeStats.FailedCompatibilityValidation = CompatibilityValidationType.ChainId;
                        //TODO confirm disconnect reason
                        await peer.Session.InitiateDisconnectAsync(DisconnectReason.Other);
                        return false;
                    }

                    if (ethArgs.GenesisHash != _synchronizationManager.Genesis?.Hash)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {peer.Node.Id}, different genesis hash: {ethArgs.GenesisHash}, our: {_synchronizationManager.Genesis?.Hash}");

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
            return capabilities.Any(x => x.ProtocolCode == Protocol.Eth && (x.Version == 62 || x.Version == 63));
        }

        private bool ValidateChainId(long chainId)
        {
            return chainId == _synchronizationManager.ChainId;
        }

        private void OnPeerDisconnected(object sender, DisconnectEventArgs e)
        {
            var session = (IP2PSession) sender;
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Peer disconnected event in PeerManager: {session.RemoteNodeId}, disconnectReason: {e.DisconnectReason}, disconnectType: {e.DisconnectType}");

            if (session.RemoteNodeId == null)
            {
                if (_logger.IsTrace) _logger.Trace($"Disconnect on session with no RemoteNodeId, sessionId: {session.SessionId}");
                return;
            }

            if (_activePeers.TryGetValue(session.RemoteNodeId, out var activePeer))
            {
                //we want to update reputation always
                activePeer.NodeStats.AddNodeStatsDisconnectEvent(e.DisconnectType, e.DisconnectReason);

                if (activePeer.Session?.SessionId != session.SessionId)
                {
                    if (_logger.IsTrace) _logger.Trace($"Received disconnect on a different session than the active peer runs. Ignoring. Id: {activePeer.Node.Id}");

                    return;
                }

                RemoveActivePeer(session.RemoteNodeId, "peer disconnected");
                if (activePeer.SynchronizationPeer != null)
                {
                    _synchronizationManager.RemovePeer(activePeer.SynchronizationPeer);
                    _transactionPool.RemovePeer(activePeer.Node.Id);
                }

                if (_logger.IsTrace) _logger.Trace($"Removing Active Peer on disconnect {session.RemoteNodeId}");

                if (_logger.IsTrace)
                {
                    var log = _peerSessionLogger.GetEventHistoryLog(activePeer.NodeStats);
                    //var log = GetEventHistoryLog(activePeer.NodeStats);
                    _logger.Trace(log);
                }

                if (_isStarted)
                {
                    //Fire and forget
                    Task.Run(() => RunPeerUpdateSync());
                }
            }
        }

        private void OnHandshakeComplete(object sender, EventArgs args)
        {
            IP2PSession session = sender as IP2PSession;
            //In case of OUT connections and different RemoteNodeId we need to replace existing Active Peer with new peer 
            ManageNewRemoteNodeId(session);

            //Fire and forget
            Task.Run(async () => await OnHandshakeCompleteAsync(session));
        }

        private void ManageNewRemoteNodeId(IP2PSession session)
        {
            if (session.ObsoleteRemoteNodeId == null)
            {
                return;
            }

            //if remote id changed we remove active peer with old remote id and add new remote id peer to active peers
            _activePeers.TryRemove(session.ObsoleteRemoteNodeId, out _);

            if (_candidatePeers.TryGetValue(session.RemoteNodeId, out Peer newPeer))
            {
                if (_logger.IsTrace) _logger.Trace($"RemoteNodeId was updated due to handshake difference, old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}, new peer present in candidate collection");
                _activePeers.TryAdd(newPeer.Node.Id, newPeer);
                return;
            }

            var node = _nodeFactory.CreateNode(session.RemoteNodeId, session.RemoteHost, session.RemotePort ?? 0);
            newPeer = new Peer(node, _nodeStatsProvider.GetOrAddNodeStats(node), session.ConnectionDirection)
            {
                Session = session,
            };
            _activePeers.TryAdd(newPeer.Node.Id, newPeer);
            _candidatePeers.TryAdd(newPeer.Node.Id, newPeer);
            if (_logger.IsTrace) _logger.Trace($"RemoteNodeId was updated due to handshake difference, old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}, new peer not present in candidate collection");
        }

        private async Task OnHandshakeCompleteAsync(IP2PSession session)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| OnHandshakeComplete: {session.RemoteNodeId}, {session.ConnectionDirection.ToString()}");

            //This is the first moment we get confirmed publicKey of remote node in case of incoming connections
            if (session.ConnectionDirection == ConnectionDirection.In)
            {
                if (_logger.IsTrace) _logger.Trace($"Handshake initialized {session.ConnectionDirection.ToString().ToUpper()} channel {session.RemoteNodeId}@{session.RemoteHost}:{session.RemotePort}");

                await ProcessIncomingConnection(session);
            }
            else
            {
                if (!_activePeers.TryGetValue(session.RemoteNodeId, out Peer peer))
                {
                    //Can happen when peer sent Disconnect message before handshake is done, it takes us a while to disconnect
                    if (_logger.IsTrace) _logger.Trace($"Initiated Handshake (OUT) with Peer without adding it to Active collection: {session.RemoteNodeId}");

                    return;
                }

                peer.NodeStats.AddNodeStatsHandshakeEvent(ConnectionDirection.Out);
            }

            if (_logger.IsTrace) _logger.Trace($"Handshake initialized for peer: {session.RemoteNodeId}");
        }

        private void OnNodeDiscovered(object sender, NodeEventArgs nodeEventArgs)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| OnNodeDiscovered {nodeEventArgs.Node.Id}");

            var id = nodeEventArgs.Node.Id;
            if (_candidatePeers.ContainsKey(id))
            {
                return;
            }

            var peer = new Peer(nodeEventArgs.Node, nodeEventArgs.NodeStats, ConnectionDirection.Out)
            {
                AddedToDiscovery = true
            };
            if (!_candidatePeers.TryAdd(id, peer))
            {
                return;
            }

            peer.NodeStats.AddNodeStatsEvent(NodeStatsEventType.NodeDiscovered);

            if (_logger.IsTrace) _logger.Trace($"Adding newly discovered node to Candidates collection {id}@{nodeEventArgs.Node.Host}:{nodeEventArgs.Node.Port}");

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
                try
                {
                    _activePeersTimer.Enabled = false;
                    RunPeerUpdateSync();
                    CleanupCandidatePeers();
                }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Error("Active peers timer failed", exception);
                }
                finally
                {
                    _activePeersTimer.Enabled = true;
                }
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

        private void StartPeerPersistenceTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting peer persistence timer");

            _peerPersistenceTimer = new System.Timers.Timer(_networkConfig.PeersPersistenceInterval) {AutoReset = false};
            _peerPersistenceTimer.Elapsed += (sender, e) =>
            {
                try
                {
                    _peerPersistenceTimer.Enabled = false;
                    RunPeerCommit();
                    //_logger.Info($"TESTTEST Candidate Count: {_candidatePeers.Count}, Active Count: {_activePeers.Count}, Persisted Peer count: {_peerStorage.GetPersistedNodes().Length}");
                }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Error("Peer persistence timer failed", exception);
                }
                finally
                {
                    _peerPersistenceTimer.Enabled = true;
                }
            };

            _peerPersistenceTimer.Start();
        }

        private void StopPeerPersistenceTimer()
        {
            try
            {
                if (_logger.IsDebug) _logger.Debug("Stopping peer persistence timer");
                _peerPersistenceTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during peer persistence timer stop", e);
            }
        }

        private void StartPingTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting ping timer");

            _pingTimer = new System.Timers.Timer(_networkConfig.P2PPingInterval) {AutoReset = false};
            _pingTimer.Elapsed += (sender, e) =>
            {
                try
                {
                    _pingTimer.Enabled = false;
                    SendPingMessages();
                }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Error("Ping timer failed", exception);
                }
                finally
                {
                    _pingTimer.Enabled = true;
                }
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
                UpdateReputationAndMaxPeersCount();

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

        private void UpdateReputationAndMaxPeersCount()
        {
            var storedNodes = _peerStorage.GetPersistedNodes();
            var activePeers = _activePeers.Values;
            var peers = activePeers.Concat(_candidatePeers.Values).GroupBy(x => x.Node.Id).Select(x => x.First()).ToDictionary(x => x.Node.Id);
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

            //if we have more persisted nodes then the threshold, we run cleanup process
            if (storedNodes.Length > _networkConfig.PersistedPeerCountCleanupThreshold)
            {
                CleanupPersistedPeers(activePeers, storedNodes);
            }
        }

        private void CleanupPersistedPeers(ICollection<Peer> activePeers, NetworkNode[] storedNodes)
        {
            var activeNodeIds = new HashSet<NodeId>(activePeers.Select(x => x.Node.Id));
            var nonActiveNodes = storedNodes.Where(x => !activeNodeIds.Contains(x.NodeId))
                .OrderBy(x => x.Reputation).ToArray();
            var countToRemove = storedNodes.Length - _networkConfig.MaxPersistedPeerCount;
            var nodesToRemove = nonActiveNodes.Take(countToRemove).ToArray();
            if (nodesToRemove.Length > 0)
            {                
                _peerStorage.RemoveNodes(nodesToRemove);
                if (_logger.IsDebug) _logger.Debug($"Removing persisted peers: {nodesToRemove.Length}, prevPersistedCount: {storedNodes.Length}, newPersistedCount: {_peerStorage.GetPersistedNodes().Length}, PersistedPeerCountCleanupThreshold: {_networkConfig.PersistedPeerCountCleanupThreshold}, MaxPersistedPeerCount: {_networkConfig.MaxPersistedPeerCount}");
            }
            //_logger.Info($"Active Nodes: \n{string.Join("\n", activePeers.Select(x => $"{x.Node.Id}: {x.NodeStats.NewPersistedNodeReputation}"))}");
            //_logger.Info($"NonActiveNodes Nodes: \n{string.Join("\n", nonActiveNodes.Select(x => $"{x.NodeId}: {x.Reputation}"))}");
            //_logger.Info($"NodesToRemove: \n{string.Join("\n", nodesToRemove.Select(x => $"{x.NodeId}: {x.Reputation}"))}");
        }

        private void CleanupCandidatePeers()
        {
            var candidates = _candidatePeers.Values.ToArray();
            if (candidates.Length <= _networkConfig.CandidatePeerCountCleanupThreshold)
            {
                return;
            }

            var countToRemove = candidates.Length - _networkConfig.MaxCandidatePeerCount;
            var failedValidationCandidates = candidates.Where(x => x.NodeStats?.FailedCompatibilityValidation.HasValue ?? false)
                .OrderBy(x => x.NodeStats.CurrentNodeReputation).ToArray();
            var otherCandidates = candidates.Except(failedValidationCandidates).OrderBy(x => x.NodeStats?.CurrentNodeReputation ?? -200).ToArray();
            var nodesToRemove = failedValidationCandidates.Take(countToRemove).ToArray();
            var failedValidationRemovedCount = nodesToRemove.Length;
            var remainingCount = countToRemove - failedValidationRemovedCount;
            if (remainingCount > 0)
            {
                nodesToRemove = nodesToRemove.Concat(otherCandidates.Take(remainingCount).ToArray()).ToArray();
            }
           
            if (nodesToRemove.Length > 0)
            {
                foreach (var peer in nodesToRemove)
                {
                    _candidatePeers.TryRemove(peer.Node.Id, out _);
                }
                if (_logger.IsDebug) _logger.Debug($"Removing candidate peers: {nodesToRemove.Length}, failedValidationRemovedCount: {failedValidationRemovedCount}, otherRemovedCount: {remainingCount}, prevCount: {candidates.Length}, newCount: {_candidatePeers.Count}, CandidatePeerCountCleanupThreshold: {_networkConfig.CandidatePeerCountCleanupThreshold}, MaxCandidatePeerCount: {_networkConfig.MaxCandidatePeerCount}");
            }
            //_logger.Info($"candidates: \n{string.Join("\n", candidates.Select(x => $"{x.Node.Id}: {x.NodeStats.CurrentNodeReputation}"))}");
            //_logger.Info($"nodesToRemove: \n{string.Join("\n", nodesToRemove.Select(x => $"{x.Node.Id}: {x.NodeStats.CurrentNodeReputation}"))}");
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

                if (new[] {SyncStatus.InitFailed, SyncStatus.InitCancelled, SyncStatus.Failed, SyncStatus.Cancelled}.Contains(e.SyncStatus))
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
    }
}