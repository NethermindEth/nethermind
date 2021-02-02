//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Nethermind.Config;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Timer = System.Timers.Timer;

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
        private readonly ManualResetEventSlim _peerUpdateRequested = new ManualResetEventSlim(false);
        private readonly PeerComparer _peerComparer = new PeerComparer();
        private readonly LocalPeerPool _peerPool;
        
        private int _pending;
        private int _tryCount;
        private int _newActiveNodes;
        private int _failedInitialConnect;
        private int _connectionRounds;

        private Timer _peerPersistenceTimer;
        private Timer _peerUpdateTimer;
        
        private bool _isStarted;
        private int _logCounter = 1;
        private Task _storageCommitTask;
        private Task _peerUpdateLoopTask;
        
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private static int _parallelism = Environment.ProcessorCount;
        
        private readonly ConcurrentDictionary<PublicKey, Peer> _activePeers = new ConcurrentDictionary<PublicKey, Peer>();

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
            _peerPool = new LocalPeerPool(_logger);
            _peerComparer = new PeerComparer();
        }

        public IReadOnlyCollection<Peer> ActivePeers => _activePeers.Values.ToList().AsReadOnly();
        public IReadOnlyCollection<Peer> CandidatePeers => _peerPool.CandidatePeers.ToList();
        public IReadOnlyCollection<Peer> ConnectedPeers => _activePeers.Values.Where(IsConnected).ToList().AsReadOnly();
        private int AvailableActivePeersCount => MaxActivePeers - _activePeers.Count;
        public int MaxActivePeers => _networkConfig.ActivePeersMaxCount + _peerPool.StaticPeerCount;

        public void Init()
        {
            LoadPersistedPeers();

            _discoveryApp.NodeDiscovered += OnNodeDiscovered;
            _staticNodesManager.NodeAdded += (sender, args) => { _peerPool.GetOrAdd(args.Node, true); };
            _staticNodesManager.NodeRemoved += (sender, args) => { _peerPool.TryRemove(args.Node.NodeId, out _); };

            _rlpxPeer.SessionCreated += (sender, args) =>
            {
                ISession session = args.Session;
                ToggleSessionEventListeners(session, true);

                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session} created in peer manager");
                if (session.Direction == ConnectionDirection.Out)
                {
                    ProcessOutgoingConnection(session);
                }
            };
        }

        public void AddPeer(NetworkNode node)
        {
            _peerPool.GetOrAdd(node, false);
        }
        
        public bool RemovePeer(NetworkNode node)
        {
            bool removed =_peerPool.TryRemove(node.NodeId, out Peer peer);
            if (removed)
            {
                peer.IsAwaitingConnection = false;
                _activePeers.TryRemove(peer.Node.Id, out Peer _);
            }

            return removed;
        }

        private void LoadPersistedPeers()
        {
            foreach (Peer peer in _peerLoader.LoadPeers(_staticNodesManager.Nodes))
            {
                if (peer.Node.Id == _rlpxPeer.LocalNodeId)
                {
                    if (_logger.IsWarn) _logger.Warn("Skipping a static peer with same ID as this node");
                    continue;
                }

                _peerPool.GetOrAdd(peer.Node);
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

            Task storageCloseTask = Task.CompletedTask;
            if (_storageCommitTask != null)
            {
                storageCloseTask = _storageCommitTask.ContinueWith(x =>
                {
                    if (x.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error("Error during peer persistence stop.", x.Exception);
                    }
                });
            }

            await storageCloseTask;
            if (_logger.IsInfo) _logger.Info("Peer Manager shutdown complete.. please wait for all components to close");
        }

        private class CandidateSelection
        {
            public List<Peer> PreCandidates { get; } = new List<Peer>();
            public List<Peer> Candidates { get; } = new List<Peer>();
            public List<Peer> Incompatible { get; } = new List<Peer>();
            public Dictionary<ActivePeerSelectionCounter, int> Counters { get; } = new Dictionary<ActivePeerSelectionCounter, int>();
        }

        private CandidateSelection _currentSelection = new CandidateSelection();

        private async Task RunPeerUpdateLoop()
        {
            int loopCount = 0;
            long previousActivePeersCount = 0;
            int failCount = 0;
            while (true)
            {
                try
                {
                    if (loopCount++ % 100 == 0)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Running peer update loop {loopCount - 1} - active: {_activePeers.Count} | candidates : {_peerPool.CandidatePeerCount}");
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

                    if (AvailableActivePeersCount == 0)
                    {
                        continue;
                    }

                    Interlocked.Exchange(ref _tryCount, 0);
                    Interlocked.Exchange(ref _newActiveNodes, 0);
                    Interlocked.Exchange(ref _failedInitialConnect, 0);
                    Interlocked.Exchange(ref _connectionRounds, 0);

                    SelectAndRankCandidates();
                    List<Peer> remainingCandidates = _currentSelection.Candidates;
                    if (!remainingCandidates.Any())
                    {
                        continue;
                    }

                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        break;
                    }

                    int currentPosition = 0;                    
                    while (true)
                    {
                        if (_cancellationTokenSource.IsCancellationRequested)
                        {
                            break;
                        }

                        int nodesToTry = Math.Min(remainingCandidates.Count - currentPosition, AvailableActivePeersCount);
                        if (nodesToTry <= 0)
                        {
                            break;
                        }
                        
                        ActionBlock<Peer> workerBlock = new ActionBlock<Peer>(
                            SetupPeerConnection,
                            new ExecutionDataflowBlockOptions
                            {
                                MaxDegreeOfParallelism = _parallelism,
                                CancellationToken = _cancellationTokenSource.Token
                            });

                        for (int i = 0; i < nodesToTry; i++)
                        {
                            await workerBlock.SendAsync(remainingCandidates[currentPosition + i]);
                        }

                        currentPosition += nodesToTry;

                        workerBlock.Complete();

                        // Wait for all messages to propagate through the network.
                        workerBlock.Completion.Wait();

                        Interlocked.Increment(ref _connectionRounds);
                    }

                    if (_logger.IsTrace)
                    {
                        int activePeersCount = _activePeers.Count;
                        if (activePeersCount != previousActivePeersCount)
                        {
                            string countersLog = string.Join(", ", _currentSelection.Counters.Select(x => $"{x.Key.ToString()}: {x.Value}"));
                            _logger.Trace($"RunPeerUpdate | {countersLog}, Incompatible: {GetIncompatibleDesc(_currentSelection.Incompatible)}, EligibleCandidates: {_currentSelection.Candidates.Count()}, " +
                                          $"Tried: {_tryCount}, Rounds: {_connectionRounds}, Failed initial connect: {_failedInitialConnect}, Established initial connect: {_newActiveNodes}, " +
                                          $"Current candidate peers: {_peerPool.CandidatePeerCount}, Current active peers: {_activePeers.Count} " +
                                          $"[InOut: {_activePeers.Count(x => x.Value.OutSession != null && x.Value.InSession != null)} | " +
                                          $"[Out: {_activePeers.Count(x => x.Value.OutSession != null)} | " +
                                          $"In: {_activePeers.Count(x => x.Value.InSession != null)}]");
                        }

                        previousActivePeersCount = activePeersCount;
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

                    if (_activePeers.Count < MaxActivePeers)
                    {
                        _peerUpdateRequested.Set();
                    }

                    failCount = 0;
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
                    ++failCount;
                    if (failCount >= 10)
                    {
                        break;
                    }
                    else
                    {
                        await Task.Delay(1000);
                    }
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

            Interlocked.Increment(ref _tryCount);
            Interlocked.Increment(ref _pending);
            bool result = await InitializePeerConnection(peer);
            // for some time we will have a peer in active that has no session assigned - analyze this?
            
            Interlocked.Decrement(ref _pending);
            if (_logger.IsTrace) _logger.Trace($"Connecting to {_stats.GetCurrentReputation(peer.Node)} rep node - {result}, ACTIVE: {_activePeers.Count}, CAND: {_peerPool.CandidatePeerCount}");

            if (!result)
            {
                _stats.ReportEvent(peer.Node, NodeStatsEventType.ConnectionFailed);
                Interlocked.Increment(ref _failedInitialConnect);
                if (peer.OutSession != null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Timeout, doing additional disconnect: {peer.Node.Id}");
                    peer.OutSession?.MarkDisconnected(DisconnectReason.ReceiveMessageTimeout, DisconnectType.Local, "timeout");
                }

                peer.IsAwaitingConnection = false;
                DeactivatePeerIfDisconnected(peer, "Failed to initialize connections");
                return;
            }

            Interlocked.Increment(ref _newActiveNodes);
        }

        private bool AddActivePeer(PublicKey nodeId, Peer peer, string reason)
        {
            peer.IsAwaitingConnection = false;
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

        private void RemoveActivePeer(PublicKey nodeId, string reason)
        {
            bool removed = _activePeers.TryRemove(nodeId, out Peer removedPeer);
            // if (removed && _logger.IsDebug) _logger.Debug($"{removedPeer.Node:s} removed from active peers - {reason}");
        }

        private void DeactivatePeerIfDisconnected(Peer peer, string reason)
        {
            if(_logger.IsTrace) _logger.Trace($"DEACTIVATING IF DISCONNECTED {peer}");
            if (!IsConnected(peer) && !peer.IsAwaitingConnection)
            {
                // dropping references to sessions so they can be garbage collected
                peer.InSession = null;
                peer.OutSession = null;
                RemoveActivePeer(peer.Node.Id, reason);
            }
        }

        private static ActivePeerSelectionCounter[] _enumValues = InitEnumValues();

        private static ActivePeerSelectionCounter[] InitEnumValues()
        {
            Array values = Enum.GetValues(typeof(ActivePeerSelectionCounter));
            ActivePeerSelectionCounter[] result = new ActivePeerSelectionCounter[values.Length];

            int index = 0;
            foreach (ActivePeerSelectionCounter value in values)
            {
                result[index++] = value;
            }

            return result;
        }
        
        private void SelectAndRankCandidates()
        {
            if (AvailableActivePeersCount <= 0)
            {
                return;
            }
            
            _currentSelection.PreCandidates.Clear();
            _currentSelection.Candidates.Clear();
            _currentSelection.Incompatible.Clear();

            for (int i = 0; i < _enumValues.Length; i++)
            {
                _currentSelection.Counters[_enumValues[i]] = 0;
            }

            foreach ((_, Peer peer) in _peerPool.AllPeers)
            {
                // node can be connected but a candidate (for some short times)
                // [describe when]
                
                // node can be active but not connected (for some short times between sending connection request and
                // establishing a session)
                if(peer.IsAwaitingConnection || IsConnected(peer) || _activePeers.TryGetValue(peer.Node.Id, out _))
                {
                    continue;
                }

                if (peer.Node.Port > 65535)
                {
                    continue;
                }

                _currentSelection.PreCandidates.Add(peer);
            }

            bool hasOnlyStaticNodes = false;
            List<Peer> staticPeers = _peerPool.StaticPeers;
            if (!_currentSelection.PreCandidates.Any() && staticPeers.Any())
            {
                _currentSelection.Candidates.AddRange(staticPeers.Where(sn => !_activePeers.ContainsKey(sn.Node.Id)));
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

                (bool Result, NodeStatsEventType? DelayReason) delayResult = _stats.IsConnectionDelayed(preCandidate.Node);
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

                if (IsConnected(preCandidate))
                {
                    // in transition
                    continue;
                }

                _currentSelection.Candidates.Add(preCandidate);
            }

            if (!hasOnlyStaticNodes)
            {
                _currentSelection.Candidates.AddRange(staticPeers.Where(sn => !_activePeers.ContainsKey(sn.Node.Id)));
            }

            _stats.UpdateCurrentReputation(_currentSelection.Candidates);
            _currentSelection.Candidates.Sort(_peerComparer);
        }

        private string GetIncompatibleDesc(IReadOnlyCollection<Peer> incompatibleNodes)
        {
            if (!incompatibleNodes.Any())
            {
                return "0";
            }

            IGrouping<CompatibilityValidationType?, Peer>[] validationGroups = incompatibleNodes.GroupBy(x => _stats.FindCompatibilityValidationResult(x.Node)).ToArray();
            return $"[{string.Join(", ", validationGroups.Select(x => $"{x.Key.ToString()}:{x.Count()}"))}]";
        }

        private async Task<bool> InitializePeerConnection(Peer candidate)
        {
            try
            {
                if(_logger.IsTrace) _logger.Trace($"CONNECTING TO {candidate}");
                candidate.IsAwaitingConnection = true;
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
            PublicKey id = session.RemoteNodeId;
            if(_logger.IsTrace) _logger.Trace($"PROCESS OUTGOING {id}");

            if (!_activePeers.TryGetValue(id, out Peer peer))
            {
                session.MarkDisconnected(DisconnectReason.DisconnectRequested, DisconnectType.Local, "peer removed");
                return;
            }

            _stats.ReportEvent(peer.Node, NodeStatsEventType.ConnectionEstablished);

            AddSession(session, peer);
        }

        private ConnectionDirection ChooseDirectionToKeep(PublicKey remoteNode)
        {
            if(_logger.IsTrace) _logger.Trace($"CHOOSING DIRECTION {remoteNode}");
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
            void CheckIfNodeIsStatic(Node node)
            {
                if (_staticNodesManager.IsStatic(node.ToString("e")))
                {
                    node.IsStatic = true;
                }
            }
            
            CheckIfNodeIsStatic(session.Node);
            
            if(_logger.IsTrace) _logger.Trace($"INCOMING {session}");
            
            // if we have already initiated connection before
            if (_activePeers.TryGetValue(session.RemoteNodeId, out Peer existingActivePeer))
            {
                AddSession(session, existingActivePeer);
                return;
            }

            if (!session.Node.IsStatic && _activePeers.Count >= MaxActivePeers)
            {
                int initCount = 0;
                foreach (KeyValuePair<PublicKey, Peer> pair in _activePeers)
                {
                    // we need to count initialized as we may have a list of active peers that is just being initialized
                    // and we do not know yet whether they are fine or not
                    if (pair.Value.InSession?.State == SessionState.Initialized ||
                        pair.Value.OutSession?.State == SessionState.Initialized)
                    {
                        initCount++;
                    }
                }

                if (initCount >= MaxActivePeers)
                {
                    if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with {session} {DisconnectReason.TooManyPeers} {DisconnectType.Local}");
                    session.InitiateDisconnect(DisconnectReason.TooManyPeers, $"{initCount}");
                    return;
                }
            }

            Peer peer = _peerPool.GetOrAdd(session.Node);
            AddSession(session, peer);
        }

        private void AddSession(ISession session, Peer peer)
        {
            if(_logger.IsTrace) _logger.Trace($"ADDING {session} {peer}");
            bool newSessionIsIn = session.Direction == ConnectionDirection.In;
            bool newSessionIsOut = !newSessionIsIn;
            bool peerIsDisconnected = !IsConnected(peer);

            if (peerIsDisconnected || (peer.IsAwaitingConnection && session.Direction == ConnectionDirection.Out))
            {
                if (newSessionIsIn)
                {
                    _stats.ReportHandshakeEvent(peer.Node, ConnectionDirection.In);
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
                        peer.InSession?.InitiateDisconnect(DisconnectReason.AlreadyConnected, "same");
                    }
                }
            }

            AddActivePeer(peer.Node.Id, peer, newSessionIsIn ? "new IN session" : "new OUT session");
        }

        private static bool IsConnected(Peer peer)
        {
            return !(peer.InSession?.IsClosing ?? true) || !(peer.OutSession?.IsClosing ?? true);
        }

        private void OnDisconnected(object sender, DisconnectEventArgs e)
        {
            ISession session = (ISession) sender;
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

            Peer peer = _peerPool.GetOrAdd(session.Node);
            if (session.Direction == ConnectionDirection.Out)
            {
                peer.IsAwaitingConnection = false;
            }

            if (_activePeers.TryGetValue(session.RemoteNodeId, out Peer activePeer))
            {
                //we want to update reputation always
                _stats.ReportDisconnect(session.Node, e.DisconnectType, e.DisconnectReason);
                if (activePeer.InSession?.SessionId != session.SessionId && activePeer.OutSession?.SessionId != session.SessionId)
                {
                    if (_logger.IsTrace) _logger.Trace($"Received disconnect on a different session than the active peer runs. Ignoring. Id: {activePeer.Node.Id}");
                    return;
                }

                DeactivatePeerIfDisconnected(activePeer, "session disconnected");
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

            Peer newPeer = _peerPool.Replace(session);
            
            RemoveActivePeer(session.ObsoleteRemoteNodeId, $"handshake difference old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}");
            AddActivePeer(session.RemoteNodeId, newPeer, $"handshake difference old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}");
            if (_logger.IsTrace) _logger.Trace($"RemoteNodeId was updated due to handshake difference, old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}, new peer not present in candidate collection");
        }

        private int _maxPeerPoolLength;
        private int _lastPeerPoolLength;
        
        private void OnNodeDiscovered(object sender, NodeEventArgs nodeEventArgs)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {nodeEventArgs.Node:e} node discovered");
            Peer peer = _peerPool.GetOrAdd(nodeEventArgs.Node);

            lock (_peerPool)
            {
                int newPeerPoolLength = _peerPool.CandidatePeerCount;
                _lastPeerPoolLength = newPeerPoolLength;

                if (_lastPeerPoolLength > _maxPeerPoolLength + 100)
                {
                    _maxPeerPoolLength = _lastPeerPoolLength;
                    if(_logger.IsDebug) _logger.Debug($"Peer pool size is: {_lastPeerPoolLength}");
                }
            }

            _stats.ReportEvent(nodeEventArgs.Node, NodeStatsEventType.NodeDiscovered);
            if (_pending < AvailableActivePeersCount)
            {
#pragma warning disable 4014
                // fire and forget - all the surrounding logic will be executed
                // exceptions can be lost here without issues
                // this for rapid connections to newly discovered peers without having to go through the UpdatePeerLoop
                SetupPeerConnection(peer);
#pragma warning restore 4014
            }

            if (_isStarted)
            {
                _peerUpdateRequested.Set();
            }
        }

        private void StartPeerUpdateLoop()
        {
            if (_logger.IsDebug) _logger.Debug("Starting peer update timer");

            _peerUpdateTimer = new Timer(_networkConfig.PeersUpdateInterval);
            _peerUpdateTimer.Elapsed += (sender, e) => { _peerUpdateRequested.Set(); };

            _peerUpdateTimer.Start();
        }

        private void StartPeerPersistenceTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting peer persistence timer");

            _peerPersistenceTimer = new Timer(_networkConfig.PeersPersistenceInterval)
            {
                AutoReset = false
            };
            
            _peerPersistenceTimer.Elapsed += (sender, e) =>
            {
                try
                {
                    _peerPersistenceTimer.Enabled = false;
                    RunPeerCommit();
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
                    if (_logger.IsTrace) _logger.Trace("No changes in peer storage, skipping commit.");
                    return;
                }

                _storageCommitTask = Task.Run(() =>
                {
                    _peerStorage.Commit();
                    _peerStorage.StartBatch();
                });


                Task task = _storageCommitTask.ContinueWith(x =>
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
            NetworkNode[] storedNodes = _peerStorage.GetPersistedNodes();
            foreach (NetworkNode node in storedNodes)
            {
                if (node.Port < 0 || node.Port > ushort.MaxValue)
                {
                    continue;
                }
                
                Peer peer = _peerPool.GetOrAdd(node, false);
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
                ICollection<Peer> activePeers = _activePeers.Values;
                CleanupPersistedPeers(activePeers, storedNodes);
            }
        }

        private void CleanupPersistedPeers(ICollection<Peer> activePeers, NetworkNode[] storedNodes)
        {
            HashSet<PublicKey> activeNodeIds = new HashSet<PublicKey>(activePeers.Select(x => x.Node.Id));
            NetworkNode[] nonActiveNodes = storedNodes.Where(x => !activeNodeIds.Contains(x.NodeId))
                .OrderBy(x => x.Reputation).ToArray();
            int countToRemove = storedNodes.Length - _networkConfig.MaxPersistedPeerCount;
            var nodesToRemove = nonActiveNodes.Take(countToRemove);

            int removedNodes = 0;
            foreach (var item in nodesToRemove)
            {
                _peerStorage.RemoveNode(item.NodeId);
                removedNodes++;
            }

            if (_logger.IsDebug) _logger.Debug($"Removing persisted peers: {removedNodes}, prevPersistedCount: {storedNodes.Length}, newPersistedCount: {_peerStorage.PersistedNodesCount}, PersistedPeerCountCleanupThreshold: {_networkConfig.PersistedPeerCountCleanupThreshold}, MaxPersistedPeerCount: {_networkConfig.MaxPersistedPeerCount}");
        }

        private void CleanupCandidatePeers()
        {
            if (_peerPool.CandidatePeerCount <= _networkConfig.CandidatePeerCountCleanupThreshold)
            {
                return;
            }

            // may further optimize allocations here
            List<Peer> candidates = _peerPool.NonStaticCandidatePeers;
            int countToRemove = candidates.Count - _networkConfig.MaxCandidatePeerCount;
            Peer[] failedValidationCandidates = candidates.Where(x => _stats.HasFailedValidation(x.Node))
                .OrderBy(x => _stats.GetCurrentReputation(x.Node)).ToArray();
            Peer[] otherCandidates = candidates.Except(failedValidationCandidates).Except(_activePeers.Values).OrderBy(x => _stats.GetCurrentReputation(x.Node)).ToArray();
            Peer[] nodesToRemove = failedValidationCandidates.Length <= countToRemove
                ? failedValidationCandidates
                : failedValidationCandidates.Take(countToRemove).ToArray();
            int failedValidationRemovedCount = nodesToRemove.Length;
            int remainingCount = countToRemove - failedValidationRemovedCount;
            if (remainingCount > 0)
            {
                Peer[] otherToRemove = otherCandidates.Take(remainingCount).ToArray();
                nodesToRemove = nodesToRemove.Length == 0
                    ? otherToRemove :
                    nodesToRemove.Concat(otherToRemove).ToArray();
            }

            if (nodesToRemove.Length > 0)
            {
                _logger.Info($"Removing {nodesToRemove.Length} out of {candidates.Count} peer candidates (candidates cleanup).");
                foreach (Peer peer in nodesToRemove)
                {
                    _peerPool.TryRemove(peer.Node.Id, out _);
                }

                if (_logger.IsDebug) _logger.Debug($"Removing candidate peers: {nodesToRemove.Length}, failedValidationRemovedCount: {failedValidationRemovedCount}, otherRemovedCount: {remainingCount}, prevCount: {candidates.Count}, newCount: {_peerPool.CandidatePeerCount}, CandidatePeerCountCleanupThreshold: {_networkConfig.CandidatePeerCountCleanupThreshold}, MaxCandidatePeerCount: {_networkConfig.MaxCandidatePeerCount}");
            }
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
