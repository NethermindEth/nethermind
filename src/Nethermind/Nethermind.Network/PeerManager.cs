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

using Nethermind.Core;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
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
        private readonly IStaticNodesManager _staticNodesManager;
        private readonly IRlpxPeer _rlpxPeer;
        private readonly INodeStatsManager _stats;
        private readonly INetworkStorage _peerStorage;
        private readonly IPeerLoader _peerLoader;
        private System.Timers.Timer _peerPersistenceTimer;
        private System.Timers.Timer _peerUpdateTimer;
        private int _logCounter = 1;
        private bool _isStarted;
        private Task _storageCommitTask;
        private long _prevActivePeersCount;
        private readonly ManualResetEventSlim _peerUpdateRequested = new ManualResetEventSlim(false);
        private Task _peerUpdateLoopTask;
        private readonly ConcurrentDictionary<PublicKey, Peer> _staticNodes =
            new ConcurrentDictionary<PublicKey, Peer>();
        private readonly ConcurrentDictionary<PublicKey, Peer> _activePeers = new ConcurrentDictionary<PublicKey, Peer>();
        private readonly ConcurrentDictionary<PublicKey, Peer> _candidatePeers = new ConcurrentDictionary<PublicKey, Peer>();

        public PeerManager(
            IRlpxPeer rlpxPeer,
            IDiscoveryApp discoveryApp,
            INodeStatsManager stats,
            INetworkStorage peerStorage,
            IPeerLoader peerLoader,
            INetworkConfig networkConfig,
            ILogManager logManager,
            IStaticNodesManager staticNodesManager)
        {
            _logger = logManager.GetClassLogger();
            _rlpxPeer = rlpxPeer ?? throw new ArgumentNullException(nameof(rlpxPeer));
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            _discoveryApp = discoveryApp ?? throw new ArgumentNullException(nameof(discoveryApp));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _staticNodesManager = staticNodesManager ?? throw new ArgumentNullException(nameof(staticNodesManager));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _peerLoader = peerLoader ?? throw new ArgumentNullException(nameof(peerLoader));
            _peerStorage.StartBatch();
            _peerComparer = new PeerComparer(_stats);
            _distinctPeerComparer = new DistinctPeerComparer();
        }

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public void Init()
        {
            LoadPeers();

            _discoveryApp.NodeDiscovered += OnNodeDiscovered;
            _staticNodesManager.NodeAdded += (sender, args) =>
            {
                _staticNodes.TryAdd(args.Node.NodeId, new Peer(new Node(args.Node.Host, args.Node.Port, true)));
                if (_candidatePeers.TryAdd(args.Node.NodeId,
                        new Peer(new Node(args.Node.Host, args.Node.Port, true))) && _logger.IsDebug)
                {
                    if (_logger.IsDebug) _logger.Debug($"Added the new static node to peers candidates: {args.Node}");
                }
            };
            _staticNodesManager.NodeRemoved += (sender, args) =>
            {
                _staticNodes.TryRemove(args.Node.NodeId, out _);
                if (_candidatePeers.TryRemove(args.Node.NodeId, out var peer) && _logger.IsDebug)
                {
                    if (_logger.IsDebug) _logger.Debug($"Removed the static node from peers candidates: {args.Node}");
                    _activePeers.TryRemove(peer.Node.Id, out _);
                }
            };
            _rlpxPeer.SessionCreated += (sender, args) =>
            {
                var session = args.Session;
                ToggleSessionEventListeners(session, true);

                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session} created in peer manager");
                if (session.Direction == ConnectionDirection.Out)
                {
                    ProcessOutgoingConnection(session);
                }
            };
        }

        private void LoadPeers()
        {
            foreach (Peer peer in _peerLoader.LoadPeers(_staticNodesManager.Nodes))
            {
                if (peer.Node.IsStatic)
                {
                    _staticNodes.TryAdd(peer.Node.Id, peer);
                }
                
                if (_candidatePeers.TryAdd(peer.Node.Id, peer))
                {
                    if (peer.Node.IsBootnode || peer.Node.IsStatic || peer.Node.IsTrusted)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Adding a {(peer.Node.IsTrusted ? "trusted" : peer.Node.IsBootnode ? "bootnode" : "stored")} candidate peer {peer.Node:s}");    
                    }
                }
            }
        }

        public void Start()
        {
            StartPeerPersistenceTimer();
            StartPeerUpdateLoop();

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
            _peerUpdateRequested.Set();
        }

        public async Task StopAsync()
        {
            _cancellationTokenSource.Cancel();

            StopTimers();

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

            if (_logger.IsTrace) LogSessionStats(true);

            if (_logger.IsInfo) _logger.Info("Peer Manager shutdown complete.. please wait for all components to close");
        }

        public void LogSessionStats(bool logEventDetails)
        {
            _stats.DumpStats(logEventDetails);
        }

        public IReadOnlyCollection<Peer> ActivePeers => _activePeers.Values.ToList().AsReadOnly();
        public IReadOnlyCollection<Peer> CandidatePeers => _candidatePeers.Values.ToList().AsReadOnly();

        private class CandidateSelection
        {
            public List<Peer> PreCandidates { get; } = new List<Peer>();
            public List<Peer> Candidates { get; } = new List<Peer>();
            public List<Peer> Incompatible { get; } = new List<Peer>();
            public Dictionary<ActivePeerSelectionCounter, int> Counters { get; } = new Dictionary<ActivePeerSelectionCounter, int>();
        }

        private CandidateSelection _currentSelection = new CandidateSelection();

        private int tryCount;
        private int newActiveNodes;
        private int failedInitialConnect;
        private int connectionRounds;

        private static int _parallelism = Environment.ProcessorCount;
        
        private async Task RunPeerUpdateLoop()
        {
            int loopCount = 0;
            while (true)
            {
                try
                {
                    if (loopCount++ % 100 == 0)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Running peer update loop {loopCount - 1} - active: {_activePeers.Count} | candidates : {_candidatePeers.Count}");
                    }

                    try
                    {
                        CleanupCandidatePeers();
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsDebug) _logger.Error("Candidate peers cleanup failed", e);
                    }

                    _peerUpdateRequested.Wait(_cancellationTokenSource.Token);
                    _peerUpdateRequested.Reset();

                    if (!_isStarted)
                    {
                        continue;
                    }

                    int availableActiveCount = _networkConfig.ActivePeersMaxCount + _staticNodes.Count -
                                               _activePeers.Count;
                    if (availableActiveCount == 0)
                    {
                        continue;
                    }

                    Interlocked.Exchange(ref tryCount, 0);
                    Interlocked.Exchange(ref newActiveNodes, 0);
                    Interlocked.Exchange(ref failedInitialConnect, 0);
                    Interlocked.Exchange(ref connectionRounds, 0);

                    SelectAndRankCandidates();
                    IReadOnlyCollection<Peer> remainingCandidates = _currentSelection.Candidates;
                    if (!remainingCandidates.Any())
                    {
                        continue;
                    }

                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        break;
                    }

                    while (true)
                    {
                        if (_cancellationTokenSource.IsCancellationRequested)
                        {
                            break;
                        }

                        availableActiveCount = _networkConfig.ActivePeersMaxCount + _staticNodes.Count -
                                               _activePeers.Count;
                        
                        int nodesToTry = Math.Min(remainingCandidates.Count, availableActiveCount);
                        if (nodesToTry == 0)
                        {
                            break;
                        }

                        IEnumerable<Peer> candidatesToTry = remainingCandidates.Take(nodesToTry)
                            .Distinct(_distinctPeerComparer);
                        remainingCandidates = remainingCandidates.Skip(nodesToTry).ToList();

                        var workerBlock = new ActionBlock<Peer>(
                            SetupPeerConnection,
                            new ExecutionDataflowBlockOptions
                            {
                                MaxDegreeOfParallelism = _parallelism,
                                CancellationToken = _cancellationTokenSource.Token
                            });

                        foreach (var candidateToTry in candidatesToTry)
                        {
                            await workerBlock.SendAsync(candidateToTry);
                        }

                        workerBlock.Complete();

                        // Wait for all messages to propagate through the network.
                        workerBlock.Completion.Wait();

                        Interlocked.Increment(ref connectionRounds);
                    }

                    if (_logger.IsDebug)
                    {
                        int activePeersCount = _activePeers.Count;
                        if (activePeersCount != _prevActivePeersCount)
                        {
                            string countersLog = string.Join(", ", _currentSelection.Counters.Select(x => $"{x.Key.ToString()}: {x.Value}"));
                            _logger.Debug($"RunPeerUpdate | {countersLog}, Incompatible: {GetIncompatibleDesc(_currentSelection.Incompatible)}, EligibleCandidates: {_currentSelection.Candidates.Count()}, " +
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
                            _logger.Trace($"{nl}{nl}All active peers: {nl} {string.Join(nl, _activePeers.Values.Select(x => $"{x.Node:s} | P2P: {_stats.GetOrAdd(x.Node).DidEventHappen(NodeStatsEventType.P2PInitialized)} | Eth62: {_stats.GetOrAdd(x.Node).DidEventHappen(NodeStatsEventType.Eth62Initialized)} | {_stats.GetOrAdd(x.Node).P2PNodeDetails?.ClientId} | {_stats.GetOrAdd(x.Node).ToString()}"))} {nl}{nl}");
                        }

                        _logCounter++;
                    }

                    if (_activePeers.Count != _networkConfig.ActivePeersMaxCount + _staticNodes.Count)
                    {
                        _peerUpdateRequested.Set();
                    }
                }
                catch (AggregateException e) when (e.InnerExceptions.Any(inner => inner is OperationCanceledException))
                {
                    if (_logger.IsInfo) _logger.Info("Peer update loop canceled.");
                    break;
                }
                catch (OperationCanceledException)
                {
                    if (_logger.IsInfo) _logger.Info("Peer update loop canceled");
                    break;
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error("Peer update loop failure", e);
                    break;
                }
            }
        }

        [Todo(Improve.MissingFunctionality, "Add cancellation support for the peer connection (so it does not wait for the 10sec timeout")]
        private async Task SetupPeerConnection(Peer peer)
        {
            // Can happen when In connection is received from the same peer and is initialized before we get here
            // In this case we do not initialize OUT connection
            if (!AddActivePeer(peer.Node.Id, peer, "upgrading candidate"))
            {
                if (_logger.IsTrace) _logger.Trace($"Active peer was already added to collection: {peer.Node.Id}");
                return;
            }

            Interlocked.Increment(ref tryCount);
            bool result = await InitializePeerConnection(peer);
            if (_logger.IsTrace) _logger.Trace($"Connecting to {_stats.GetCurrentReputation(peer.Node)} rep node - {result}, ACTIVE: {_activePeers.Count}, CAND: {_candidatePeers.Count}");

            if (!result)
            {
                _stats.ReportEvent(peer.Node, NodeStatsEventType.ConnectionFailed);
                Interlocked.Increment(ref failedInitialConnect);
                if (peer.OutSession != null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Timeout, doing additional disconnect: {peer.Node.Id}");
                    peer.OutSession?.Disconnect(DisconnectReason.ReceiveMessageTimeout, DisconnectType.Local, "timeout");
                }

                DeactivatePeerIfDisconnected(peer, "Failed to initialize connections");
                return;
            }

            Interlocked.Increment(ref newActiveNodes);
        }

        private bool AddActivePeer(PublicKey nodeId, Peer peer, string reason)
        {
            bool added = _activePeers.TryAdd(nodeId, peer);
            if (added)
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {peer.Node:s} added to active peers - {reason}");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {peer.Node:s} already in active peers");
            }

            return added;
        }

        private bool RemoveActivePeer(PublicKey nodeId, string reason)
        {
            bool removed = _activePeers.TryRemove(nodeId, out Peer removedPeer);
            if (removed && _logger.IsTrace) _logger.Trace($"|NetworkTrace| {removedPeer.Node:s} removed from active peers - {reason}");
            return removed;
        }

        private void DeactivatePeerIfDisconnected(Peer peer, string reason)
        {
            if (PeerIsDisconnected(peer))
            {
                peer.InSession = null;
                peer.OutSession = null;
                RemoveActivePeer(peer.Node.Id, reason);
            }
        }

        private void SelectAndRankCandidates()
        {
            _currentSelection.PreCandidates.Clear();
            _currentSelection.Candidates.Clear();
            _currentSelection.Incompatible.Clear();
            foreach (ActivePeerSelectionCounter value in Enum.GetValues(typeof(ActivePeerSelectionCounter)))
            {
                _currentSelection.Counters[value] = 0;
            }

            var availableActiveCount = _networkConfig.ActivePeersMaxCount + _staticNodes.Count - _activePeers.Count;
            if (availableActiveCount <= 0)
            {
                return;
            }

            foreach ((PublicKey key, Peer peer) in _candidatePeers)
            {
                if (_activePeers.ContainsKey(key))
                {
                    continue;
                }

                if (peer.Node.Port > 65535)
                {
                    continue;
                }

                _currentSelection.PreCandidates.Add(peer);
            }

            var hasOnlyStaticNodes = false;
            if (!_currentSelection.PreCandidates.Any() && _staticNodes.Values.Any())
            {
                _currentSelection.Candidates.AddRange(_staticNodes.Values);
                hasOnlyStaticNodes = true;
            }

            if (!_currentSelection.PreCandidates.Any() && !hasOnlyStaticNodes)
            {
                return;
            }

            _currentSelection.Counters[ActivePeerSelectionCounter.AllNonActiveCandidates] =
                _currentSelection.PreCandidates.Count;

            foreach (Peer preCandidate in _currentSelection.PreCandidates)
            {
                if (preCandidate.Node.Port == 0)
                {
                    _currentSelection.Counters[ActivePeerSelectionCounter.FilteredByZeroPort]++;
                    continue;
                }

                var delayResult = _stats.IsConnectionDelayed(preCandidate.Node);
                if (delayResult.Result)
                {
                    if (delayResult.DelayReason == NodeStatsEventType.Disconnect)
                    {
                        _currentSelection.Counters[ActivePeerSelectionCounter.FilteredByDisconnect]++;
                    }
                    else if (delayResult.DelayReason == NodeStatsEventType.ConnectionFailed)
                    {
                        _currentSelection.Counters[ActivePeerSelectionCounter.FilteredByFailedConnection]++;
                    }

                    continue;
                }

                if (_stats.FindCompatibilityValidationResult(preCandidate.Node).HasValue)
                {
                    _currentSelection.Incompatible.Add(preCandidate);
                    continue;
                }

                if (!PeerIsDisconnected(preCandidate))
                {
                    // in transition
                    continue;
                }

                _currentSelection.Candidates.Add(preCandidate);
            }

            if (!hasOnlyStaticNodes)
            {
                _currentSelection.Candidates.AddRange(_staticNodes.Values);
            }

            _currentSelection.Candidates.Sort(_peerComparer);
        }

        private readonly PeerComparer _peerComparer;
        private readonly DistinctPeerComparer _distinctPeerComparer;

        public class PeerComparer : IComparer<Peer>
        {
            private readonly INodeStatsManager _stats;

            public PeerComparer(INodeStatsManager stats)
            {
                _stats = stats;
            }

            public int Compare(Peer x, Peer y)
            {
                if (x == null)
                {
                    return y == null ? 0 : 1;
                }

                if (y == null)
                {
                    return -1;
                }
                
                int staticValue = -x.Node.IsStatic.CompareTo(y.Node.IsStatic);
                if (staticValue != 0)
                {
                    return staticValue;
                }

                int trust = -x.Node.IsTrusted.CompareTo(y.Node.IsTrusted);
                if (trust != 0)
                {
                    return trust;
                }

                int reputation = -_stats.GetCurrentReputation(x.Node).CompareTo(_stats.GetCurrentReputation(y.Node));
                return reputation;
            }
        }

        private class DistinctPeerComparer : IEqualityComparer<Peer>
        {
            public bool Equals(Peer x, Peer y)
            {
                if (x is null || y is null)
                {
                    return false;
                }
                
                return x.Node.Id.Equals(y.Node.Id);
            }

            public int GetHashCode(Peer obj) => obj?.Node is null ? 0 : obj.Node.GetHashCode();
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

            var validationGroups = incompatibleNodes.GroupBy(x => _stats.FindCompatibilityValidationResult(x.Node)).ToArray();
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
                if (_logger.IsTrace) _logger.Trace($"Cannot connect to peer [{ex.NetworkExceptionType.ToString()}]: {candidate.Node:s}");
                return false;
            }
            catch (Exception ex)
            {
                if (_logger.IsDebug) _logger.Error($"Error trying to initiate connection with peer: {candidate.Node:s}", ex);
                return false;
            }
        }

        private void ProcessOutgoingConnection(ISession session)
        {
            var id = session.RemoteNodeId;

            if (!_activePeers.TryGetValue(id, out Peer peer))
            {
                if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Initiated rlpx connection (out) with Peer without adding it to Active collection: {id}");
                return;
            }

            _stats.ReportEvent(peer.Node, NodeStatsEventType.ConnectionEstablished);

            AddSession(session, peer);
        }

        private ConnectionDirection ChooseDirectionToKeep(PublicKey remoteNode)
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

        private void ProcessIncomingConnection(ISession session)
        {
            // if we have already initiated connection before
            if (_activePeers.TryGetValue(session.RemoteNodeId, out Peer existingActivePeer))
            {
                AddSession(session, existingActivePeer);
                return;
            }

            if (_activePeers.Count >= _networkConfig.ActivePeersMaxCount + _staticNodes.Count)
            {
                int initCount = 0;
                foreach (KeyValuePair<PublicKey, Peer> pair in _activePeers)
                {
                    if (pair.Value.InSession?.State == SessionState.Initialized ||
                        pair.Value.OutSession?.State == SessionState.Initialized)
                    {
                        initCount++;
                    }
                }

                if (initCount >= _networkConfig.ActivePeersMaxCount + _staticNodes.Count)
                {
                    if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with {session} {DisconnectReason.TooManyPeers} {DisconnectType.Local}");
                    session.InitiateDisconnect(DisconnectReason.TooManyPeers, $"{initCount}");
                    return;
                }
            }

            // it is possible we already have this node as a candidate
            if (_candidatePeers.TryGetValue(session.RemoteNodeId, out Peer existingCandidatePeer))
            {
                AddSession(session, existingCandidatePeer);
            }
            else
            {
                Peer newPeer = new Peer(session.Node)
                {
                    InSession = session
                };

                if (AddActivePeer(session.RemoteNodeId, newPeer, "incoming connection"))
                {
                    _stats.ReportHandshakeEvent(newPeer.Node, ConnectionDirection.In);
                    // we also add this node to candidates for future connection (if we dont have it yet)
                    _candidatePeers.TryAdd(session.RemoteNodeId, newPeer);
                }
                else
                {
                    // we keep trying to make it active
                    ProcessIncomingConnection(session);
                }
            }
        }

        private void AddSession(ISession session, Peer peer)
        {
            bool newSessionIsIn = session.Direction == ConnectionDirection.In;
            bool newSessionIsOut = !newSessionIsIn;
            bool peerIsDisconnected = PeerIsDisconnected(peer);

            if (peerIsDisconnected)
            {
                if (newSessionIsIn)
                {
                    peer.InSession = session;
                }
                else
                {
                    peer.OutSession = session;
                }
            }
            else
            {
                bool peerHasAnOpenInSession = !peer.InSession?.IsClosing ?? false;
                bool peerHasAnOpenOutSession = !peer.OutSession?.IsClosing ?? false;

                if (newSessionIsIn && peerHasAnOpenInSession || newSessionIsOut && peerHasAnOpenOutSession)
                {
                    if (_logger.IsDebug) _logger.Debug($"Disconnecting a {session} - already connected");
                    session.InitiateDisconnect(DisconnectReason.AlreadyConnected, "same");
                }
                else if (newSessionIsIn && peerHasAnOpenOutSession || newSessionIsOut && peerHasAnOpenInSession)
                {
                    // disconnecting the new session as it lost to the existing one
                    ConnectionDirection directionToKeep = ChooseDirectionToKeep(session.RemoteNodeId);
                    if (session.Direction != directionToKeep)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Disconnecting a new {session} - {directionToKeep} session already connected");
                        session.InitiateDisconnect(DisconnectReason.AlreadyConnected, "same");
                    }
                    // replacing existing session with the new one as the new one won
                    else if (newSessionIsIn)
                    {
                        peer.InSession = session;
                        if (_logger.IsDebug) _logger.Debug($"Disconnecting an existing {session} - {directionToKeep} session to replace");
                        peer.OutSession?.InitiateDisconnect(DisconnectReason.AlreadyConnected, "same");
                    }
                    else
                    {
                        peer.OutSession = session;
                        if (_logger.IsDebug) _logger.Debug($"Disconnecting an existing {session} - {directionToKeep} session to replace");
                        peer.OutSession?.InitiateDisconnect(DisconnectReason.AlreadyConnected, "same");
                    }
                }
            }

            AddActivePeer(peer.Node.Id, peer, newSessionIsIn ? "new IN session" : "new OUT session");
        }

        private static bool PeerIsDisconnected(Peer peer)
        {
            return (peer.InSession?.IsClosing ?? true) && (peer.OutSession?.IsClosing ?? true);
        }

        private void OnDisconnected(object sender, DisconnectEventArgs e)
        {
            var session = (ISession) sender;
            ToggleSessionEventListeners(session, false);
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session} closing");

            if (session.State != SessionState.Disconnected)
            {
                throw new InvalidAsynchronousStateException($"Invalid session state in {nameof(OnDisconnected)} - {session.State}");
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| peer disconnected event in PeerManager - {session} {e.DisconnectReason} {e.DisconnectType}");

            if (session.RemoteNodeId == null)
            {
                // this happens when we have a disconnect on incoming connection before handshake
                if (_logger.IsTrace) _logger.Trace($"Disconnect on session with no RemoteNodeId - {session}");
                return;
            }

            if (_activePeers.TryGetValue(session.RemoteNodeId, out var activePeer))
            {
                //we want to update reputation always
                _stats.ReportDisconnect(session.Node, e.DisconnectType, e.DisconnectReason);

                if (activePeer.InSession?.SessionId != session.SessionId && activePeer.OutSession?.SessionId != session.SessionId)
                {
                    if (_logger.IsTrace) _logger.Trace($"Received disconnect on a different session than the active peer runs. Ignoring. Id: {activePeer.Node.Id}");
                    return;
                }

                DeactivatePeerIfDisconnected(activePeer, "session disconnected");

                if (_logger.IsTrace) _stats.DumpNodeStats(activePeer.Node);
                _peerUpdateRequested.Set();
            }
        }

        private void ToggleSessionEventListeners(ISession session, bool shouldListen)
        {
            if (shouldListen)
            {
                session.HandshakeComplete += OnHandshakeComplete;
                session.Disconnected += OnDisconnected;
            }
            else
            {
                session.HandshakeComplete -= OnHandshakeComplete;
                session.Disconnected -= OnDisconnected;
            }
        }

        private void OnHandshakeComplete(object sender, EventArgs args)
        {
            ISession session = (ISession) sender;
            _stats.GetOrAdd(session.Node);

            //In case of OUT connections and different RemoteNodeId we need to replace existing Active Peer with new peer 
            ManageNewRemoteNodeId(session);

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session} completed handshake - peer manager handling");

            //This is the first moment we get confirmed publicKey of remote node in case of incoming connections
            if (session.Direction == ConnectionDirection.In)
            {
                ProcessIncomingConnection(session);
            }
            else
            {
                if (!_activePeers.TryGetValue(session.RemoteNodeId, out Peer peer))
                {
                    //Can happen when peer sent Disconnect message before handshake is done, it takes us a while to disconnect
                    if (_logger.IsTrace) _logger.Trace($"Initiated handshake (OUT) with a peer without adding it to the Active collection : {session}");
                    return;
                }

                _stats.ReportHandshakeEvent(peer.Node, ConnectionDirection.Out);
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session} handshake initialized in peer manager");
        }

        private void ManageNewRemoteNodeId(ISession session)
        {
            if (session.ObsoleteRemoteNodeId == null)
            {
                return;
            }

            if (_candidatePeers.TryGetValue(session.RemoteNodeId, out Peer newPeer))
            {
                RemoveActivePeer(session.ObsoleteRemoteNodeId, $"handshake difference old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}");
                AddActivePeer(newPeer.Node.Id, newPeer, $"handshake difference old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}");
                return;
            }

            newPeer = new Peer(session.Node);
            if (session.Direction == ConnectionDirection.In)
            {
                newPeer.InSession = session;
            }
            else
            {
                newPeer.OutSession = session;
            }

            // check here - why do we assume that it has been in active?
            RemoveActivePeer(session.ObsoleteRemoteNodeId, $"handshake difference old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}");
            AddActivePeer(newPeer.Node.Id, newPeer, $"handshake difference old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}");
            _candidatePeers.TryRemove(session.ObsoleteRemoteNodeId, out _);
            _candidatePeers.TryAdd(newPeer.Node.Id, newPeer);
            if (_logger.IsTrace) _logger.Trace($"RemoteNodeId was updated due to handshake difference, old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}, new peer not present in candidate collection");
        }

        private void OnNodeDiscovered(object sender, NodeEventArgs nodeEventArgs)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {nodeEventArgs.Node:e} node discovered");

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

            _stats.ReportEvent(peer.Node, NodeStatsEventType.NodeDiscovered);

            if (_logger.IsTrace) _logger.Trace($"{nodeEventArgs.Node:s} added to candidate nodes");

            if (_isStarted)
            {
                _peerUpdateRequested.Set();
            }
        }

        private void StartPeerUpdateLoop()
        {
            if (_logger.IsDebug) _logger.Debug("Starting peer update timer");

            _peerUpdateTimer = new System.Timers.Timer(_networkConfig.PeersUpdateInterval);
            _peerUpdateTimer.Elapsed += (sender, e) => { _peerUpdateRequested.Set(); };

            _peerUpdateTimer.Start();
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

        private void StopTimers()
        {
            try
            {
                if (_logger.IsDebug) _logger.Debug("Stopping peer timers");
                _peerPersistenceTimer?.Stop();
                _peerPersistenceTimer?.Dispose();
                _peerUpdateTimer?.Stop();
                _peerUpdateTimer?.Dispose();
            }
            catch (Exception e)
            {
                _logger.Error("Error during peer timers stop", e);
            }
        }

        private void RunPeerCommit()
        {
            try
            {
                UpdateReputationAndMaxPeersCount();

                if (!_peerStorage.AnyPendingChange())
                {
//                    if (_logger.IsTrace) _logger.Trace("No changes in peer storage, skipping commit.");
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

        [Todo(Improve.Allocations, "Remove ToDictionary and ToArray here")]
        private void UpdateReputationAndMaxPeersCount()
        {
            var storedNodes = _peerStorage.GetPersistedNodes();
            foreach (var node in storedNodes)
            {
                _activePeers.TryGetValue(node.NodeId, out Peer peer);
                if (peer == null)
                {
                    _candidatePeers.TryGetValue(node.NodeId, out peer);    
                }

                if (peer == null)
                {
                    continue;
                }
                
                long newRep = _stats.GetNewPersistedReputation(peer.Node);
                if (newRep != node.Reputation)
                {
                    node.Reputation = newRep;
                    _peerStorage.UpdateNode(node);
                }
            }

            //if we have more persisted nodes then the threshold, we run cleanup process
            if (storedNodes.Length > _networkConfig.PersistedPeerCountCleanupThreshold)
            {
                var activePeers = _activePeers.Values;
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
            if (_candidatePeers.Count <= _networkConfig.CandidatePeerCountCleanupThreshold)
            {
                return;
            }

            // may further optimize allocations here
            var candidates = _candidatePeers.Values.Where(p => !p.Node.IsStatic).ToArray();
            var countToRemove = candidates.Length - _networkConfig.MaxCandidatePeerCount;
            var failedValidationCandidates = candidates.Where(x => _stats.HasFailedValidation(x.Node))
                .OrderBy(x => _stats.GetCurrentReputation(x.Node)).ToArray();
            var otherCandidates = candidates.Except(failedValidationCandidates).OrderBy(x => _stats.GetCurrentReputation(x.Node)).ToArray();
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

        private enum ActivePeerSelectionCounter
        {
            AllNonActiveCandidates,
            FilteredByZeroPort,
            FilteredByDisconnect,
            FilteredByFailedConnection
        }
    }
}