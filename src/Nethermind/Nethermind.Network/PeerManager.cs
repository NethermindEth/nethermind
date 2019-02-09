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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core.Crypto;
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
        private readonly INodeStatsManager _nodeStats;
        private readonly INetworkStorage _peerStorage;
        private System.Timers.Timer _peerPersistenceTimer;
        
        private int _logCounter = 1;
        private bool _isStarted;
        private readonly IPerfService _perfService;
        private Task _storageCommitTask;
        private long _prevActivePeersCount;
        private readonly ManualResetEventSlim _peerUpdateRequested = new ManualResetEventSlim(false);
        private Task _peerUpdateLoopTask;

        private readonly ConcurrentDictionary<PublicKey, Peer> _activePeers = new ConcurrentDictionary<PublicKey, Peer>();
        private readonly ConcurrentDictionary<PublicKey, Peer> _candidatePeers = new ConcurrentDictionary<PublicKey, Peer>();

        public PeerManager(
            IRlpxPeer rlpxPeer,
            IDiscoveryApp discoveryApp,
            ISynchronizationManager synchronizationManager,
            INodeStatsManager nodeStats,
            INetworkStorage peerStorage,
            INetworkConfig networkConfig,
            IPerfService perfService,
            ITransactionPool transactionPool,
            ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            
            _rlpxPeer = rlpxPeer ?? throw new ArgumentNullException(nameof(rlpxPeer));
            _synchronizationManager = synchronizationManager ?? throw new ArgumentNullException(nameof(synchronizationManager));
            _nodeStats = nodeStats ?? throw new ArgumentNullException(nameof(nodeStats));
            _discoveryApp = discoveryApp ?? throw new ArgumentNullException(nameof(discoveryApp));
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _peerStorage.StartBatch();

            _nodeStats = nodeStats;
            _logger = logManager.GetClassLogger();
        }

        internal IReadOnlyCollection<Peer> CandidatePeers => _candidatePeers.Values.ToArray();
        internal IReadOnlyCollection<Peer> ActivePeers => _activePeers.Values.ToArray();

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public void Init()
        {
            _discoveryApp.NodeDiscovered += OnNodeDiscovered;
            _synchronizationManager.SyncEvent += OnSyncEvent;

            LoadConfiguredTrustedPeers();
            LoadPeersFromDb();
            LoadConfiguredBootnodes();

            _rlpxPeer.SessionCreated += (sender, args) =>
            {
                var session = args.Session;
                session.Disconnected += OnDisconnected;
                session.HandshakeComplete += OnHandshakeComplete;

                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Session created: {session.RemoteNodeId}, {session.ConnectionDirection.ToString()}");
                if (session.ConnectionDirection == ConnectionDirection.Out)
                {
                    ProcessOutgoingConnection(session);
                }
            };
        }

        public void Start()
        {
            StartPeerPersistenceTimer();
            

            _peerUpdateLoopTask = Task.Factory.StartNew(
                RunPeerUpdateLoop,
                _cancellationTokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Peer update loop encountered an exception.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsDebug) _logger.Debug("Peer update loop stopped.");
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsDebug) _logger.Debug("Peer update loop complete.");
                }
            });

            _isStarted = true;
        }

        public async Task StopAsync(ExitType exitType)
        {
            var key = _perfService.StartPerfCalc();
            _cancellationTokenSource.Cancel();

            StopPeerPersistenceTimer();

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
            _nodeStats.DumpStats(logEventDetails);
        }

        internal void RunPeerUpdate()
        {
            _peerUpdateRequested.Set();
        }

        private async Task RunPeerUpdateLoop()
        {
            while (true)
            {
                try
                {
                    CleanupCandidatePeers();
                }
                catch (Exception e)
                {
                    if (_logger.IsDebug) _logger.Error("Candidate peers clanup failed", e);
                }

                _peerUpdateRequested.Wait(_cancellationTokenSource.Token);
                _peerUpdateRequested.Reset();

                int availableActiveCount = _networkConfig.ActivePeersMaxCount - _activePeers.Count;
                if (availableActiveCount == 0)
                {
                    continue;
                }

                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    break;
                }

                int tryCount = 0;
                int newActiveNodes = 0;
                int failedInitialConnect = 0;
                int connectionRounds = 0;

                var candidateSelection = SelectAndRankCandidates();
                IReadOnlyCollection<Peer> remainingCandidates = candidateSelection.Candidates;
                if (!remainingCandidates.Any())
                {
                    continue;
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
                            _nodeStats.ReportEvent(peer.Node, NodeStatsEventType.ConnectionFailed);
                            Interlocked.Increment(ref failedInitialConnect);
                            if (peer.OutSession != null)
                            {
                                if (_logger.IsTrace) _logger.Trace($"Timeout, doing additional disconnect: {peer.Node.Id}");
                                peer.OutSession?.DisconnectAsync(DisconnectReason.ReceiveMessageTimeout, DisconnectType.Local);
                            }
                            else if (peer.InSession != null)
                            {
                                RemovePeerIfDisconnected(peer, "Failed to initialize connections");
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
                                      $"[InOut: {_activePeers.Count(x => x.Value.OutSession != null && x.Value.InSession != null)} | " +
                                      $"[Out: {_activePeers.Count(x => x.Value.OutSession != null)} | " +
                                      $"In: {_activePeers.Count(x => x.Value.InSession != null)}]");
                    }

                    _prevActivePeersCount = activePeersCount;
                }

                if (_logger.IsTrace)
                {
                    if (_logCounter % 5 == 0)
                    {
                        string nl = Environment.NewLine;
                        
                        _logger.Trace($"{nl}{nl}All active peers: {nl}{string.Join(nl, _activePeers.Values.Select(x => $"{_nodeStats.GetOrAdd(x.Node).ToString()} | P2PInitialized: {_nodeStats.GetOrAdd(x.Node).DidEventHappen(NodeStatsEventType.P2PInitialized)} | Eth62Initialized: {_nodeStats.GetOrAdd(x.Node).DidEventHappen(NodeStatsEventType.Eth62Initialized)} | ClientId: {_nodeStats.GetOrAdd(x.Node).P2PNodeDetails?.ClientId}"))} {nl}{nl}");
                    }

                    _logCounter++;
                }
            }

            if (_logger.IsWarn) _logger.Warn("Exiting peer update loop");
            await Task.CompletedTask;
        }

        private bool AddActivePeer(PublicKey nodeId, Peer peer, string reason)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| ADDING {nodeId} {reason}");
            return _activePeers.TryAdd(nodeId, peer);
        }

        private void RemovePeerIfDisconnected(Peer peer, string reason)
        {
            if (IsPeerDisconnected(peer))
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| REMOVING {peer.Node.Id} {reason}");
                bool success = _activePeers.TryRemove(peer.Node.Id, out Peer removedPeer);
                if (success)
                {
                    removedPeer.InSession = null;
                    removedPeer.OutSession = null;
                }
            }
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

                var delayResult = _nodeStats.IsConnectionDelayed(candidate.Value.Node);
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

                if (_nodeStats.FindCompatibilityValidationResult(candidate.Value.Node).HasValue)
                {
                    incompatiblePeers.Add(candidate.Value);
                    continue;
                }

                if (candidate.Value.OutSession != null || candidate.Value.InSession != null)
                {
                    // in transition
                    continue;
                }

                candidates.Add(candidate.Value);
            }
            
            return (candidates.OrderBy(x => x.Node.IsTrusted).ThenByDescending(x => _nodeStats.GetCurrentReputation(x.Node)).ToList(), counters, incompatiblePeers);
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

            var validationGroups = incompatibleNodes.GroupBy(x => _nodeStats.FindCompatibilityValidationResult(x.Node)).ToArray();
            return $"[{string.Join(", ", validationGroups.Select(x => $"{x.Key.ToString()}:{x.Count()}"))}]";
        }

        private async Task<bool> InitializePeerConnection(Peer candidate)
        {
            try
            {
                await _rlpxPeer.ConnectAsync(candidate.Node);
                return true;
            }
            catch (NetworkingException ex)
            {
                if (_logger.IsTrace) _logger.Trace($"Cannot connect to Peer [{ex.NetworkExceptionType.ToString()}]: {candidate.Node.Id}");
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

                var node = new Node(persistedPeer.NodeId, persistedPeer.Host, persistedPeer.Port);
                var nodeStats = _nodeStats.GetOrAdd(node);
                nodeStats.CurrentPersistedNodeReputation = persistedPeer.Reputation;

                var peer = new Peer(node);
                if (!_candidatePeers.TryAdd(node.Id, peer))
                {
                    continue;
                }

                if (_logger.IsTrace) _logger.Trace($"Adding a new peer candidate {node.Id}@{node.Host}:{node.Port}");
            }
        }

        private void LoadConfiguredTrustedPeers()
        {
            var trustedPeers = _networkConfig.TrustedPeers;

            if (_logger.IsInfo) _logger.Info($"Initializing trusted peers: {trustedPeers?.Length ?? 0}.");

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
            var node = new Node(networkNode.NodeId, networkNode.Host, networkNode.Port);
            node.IsTrusted = isTrustedPeer;

            var peer = new Peer(node);
            if (_candidatePeers.TryAdd(node.Id, peer))
            {
                if (_logger.IsDebug) _logger.Debug($"Adding config peer ({(isTrustedPeer ? "trusted" : "bootnode")}) to New collection {node.Id}@{node.Host}:{node.Port}");
            }
        }

        private void ProcessOutgoingConnection(IP2PSession session)
        {
            var id = session.RemoteNodeId;

            if (!_activePeers.TryGetValue(id, out Peer peer))
            {
                if (_logger.IsError) _logger.Error($"Initiated rlpx connection (out) with Peer without adding it to Active collection: {id}");

                return;
            }

            _nodeStats.ReportEvent(peer.Node, NodeStatsEventType.ConnectionEstablished);
            peer.OutSession = session;

            if (_logger.IsTrace) _logger.Trace($"Initializing OUT connection (PeerManager) for peer: {session.RemoteNodeId}");
        }

        private ConnectionDirection SessionDirectionToKeep(PublicKey remoteNode)
        {
            byte[] localKey = _rlpxPeer.LocalNodeId.Bytes;
            byte[] remoteKey = remoteNode.Bytes;
            for (int i = 0; i < remoteNode.Bytes.Length; i++)
            {
                if (localKey[i] > remoteKey[i])
                {
                    return ConnectionDirection.Out;
                }

                if (localKey[i] < remoteKey[i])
                {
                    return ConnectionDirection.In;
                }
            }

            return ConnectionDirection.In;
        }

        private async Task ProcessIncomingConnection(IP2PSession session)
        {
            // if we have already initiated connection before
            if (_activePeers.TryGetValue(session.RemoteNodeId, out Peer existingPeer))
            {
                if (SessionDirectionToKeep(session.RemoteNodeId) == ConnectionDirection.In)
                {
                    existingPeer.InSession = session;
                    _nodeStats.ReportHandshakeEvent(existingPeer.Node, ConnectionDirection.In);
                    if (_logger.IsTrace) _logger.Trace($"Disconnecting OUT session, IN session already connected: {session.RemoteNodeId}");
                    await existingPeer.OutSession.InitiateDisconnectAsync(DisconnectReason.AlreadyConnected);
                    return;
                }
                else
                {
                    existingPeer.InSession = session;
                    if (_logger.IsTrace) _logger.Trace($"Disconnecting IN session, OUT session already connected: {session.RemoteNodeId}");
                    await existingPeer.InSession.InitiateDisconnectAsync(DisconnectReason.AlreadyConnected);
                    return;
                }
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
                peer.InSession = session;
            }
            else
            {
                var node = new Node(session.RemoteNodeId, session.RemoteHost, session.RemotePort ?? 0);
                peer = new Peer(node)
                {
                    InSession = session
                };
            }

            if (AddActivePeer(session.RemoteNodeId, peer, "incoming connection"))
            {
                _nodeStats.ReportHandshakeEvent(peer.Node, ConnectionDirection.In);
                // we also add this node to candidates for future connection (if we dont have it yet)
                _candidatePeers.TryAdd(session.RemoteNodeId, peer);
            }
            else
            {
                await ProcessIncomingConnection(session);
            }
        }

        private bool IsPeerDisconnected(Peer peer)
        {
            return (peer.InSession?.SessionState ?? SessionState.Disconnected) == SessionState.Disconnected
                   && (peer.OutSession?.SessionState ?? SessionState.Disconnected) == SessionState.Disconnected;
        }

        private void OnDisconnected(object sender, DisconnectEventArgs e)
        {
            var session = (IP2PSession) sender;
            session.HandshakeComplete -= OnHandshakeComplete;
            session.Disconnected -= OnDisconnected;
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Session closing: {session.RemoteNodeId}, {session.ConnectionDirection.ToString()}");

            if (session.SessionState != SessionState.Disconnected)
            {
                throw new InvalidOperationException($"Invalid session state in {nameof(OnDisconnected)} - {session.SessionState}");
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Peer disconnected event in PeerManager: {session.RemoteNodeId}, disconnectReason: {e.DisconnectReason}, disconnectType: {e.DisconnectType}");

            if (session.RemoteNodeId == null)
            {
                if (_logger.IsTrace) _logger.Trace($"Disconnect on session with no RemoteNodeId, sessionId: {session.SessionId}");
                return;
            }

            if (_activePeers.TryGetValue(session.RemoteNodeId, out var activePeer))
            {
                //we want to update reputation always
                _nodeStats.ReportDisconnect(session.Node, e.DisconnectType, e.DisconnectReason);

                if (activePeer.InSession?.SessionId != session.SessionId && activePeer.OutSession?.SessionId != session.SessionId)
                {
                    if (_logger.IsTrace) _logger.Trace($"Received disconnect on a different session than the active peer runs. Ignoring. Id: {activePeer.Node.Id}");
                    return;
                }

                RemovePeerIfDisconnected(activePeer, "session disconnected");

                if (_logger.IsTrace) _nodeStats.DumpNodeStats(activePeer.Node);

                if (_isStarted)
                {
                    _peerUpdateRequested.Set();
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

            if (_candidatePeers.TryGetValue(session.RemoteNodeId, out Peer newPeer))
            {
                if (_logger.IsTrace) _logger.Trace($"RemoteNodeId was updated due to handshake difference, old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}, new peer present in candidate collection");
                _activePeers.TryRemove(session.ObsoleteRemoteNodeId, out _);
                _activePeers.TryAdd(newPeer.Node.Id, newPeer);
                return;
            }

            var node = new Node(session.RemoteNodeId, session.RemoteHost, session.RemotePort ?? 0);
            newPeer = new Peer(node);
            if (session.ConnectionDirection == ConnectionDirection.In)
            {
                newPeer.InSession = session;
            }
            else
            {
                newPeer.OutSession = session;
            }

            _activePeers.TryRemove(session.ObsoleteRemoteNodeId, out _);
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

                _nodeStats.ReportHandshakeEvent(peer.Node, ConnectionDirection.Out);
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

            var peer = new Peer(nodeEventArgs.Node);
            if (!_candidatePeers.TryAdd(id, peer))
            {
                return;
            }

            _nodeStats.ReportEvent(peer.Node, NodeStatsEventType.NodeDiscovered);

            if (_logger.IsTrace) _logger.Trace($"Adding newly discovered node to Candidates collection {id}@{nodeEventArgs.Node.Host}:{nodeEventArgs.Node.Port}");

            if (_isStarted)
            {
                _peerUpdateRequested.Set();
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
                long newRep = _nodeStats.GetNewPersistedReputation(peer.Node);
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
            var activeNodeIds = new HashSet<PublicKey>(activePeers.Select(x => x.Node.Id));
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
            var failedValidationCandidates = candidates.Where(x => _nodeStats.HasFailedValidation(x.Node))
                .OrderBy(x => _nodeStats.GetCurrentReputation(x.Node)).ToArray();
            var otherCandidates = candidates.Except(failedValidationCandidates).OrderBy(x => _nodeStats.GetCurrentReputation(x.Node)).ToArray();
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
    }
}