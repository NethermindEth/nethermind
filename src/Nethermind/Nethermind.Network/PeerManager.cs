// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using FastEnumUtility;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Timer = System.Timers.Timer;

namespace Nethermind.Network
{
    /// <summary>
    /// </summary>
    public class PeerManager : IPeerManager
    {
        private readonly ILogger _logger;
        private readonly INetworkConfig _networkConfig;
        private readonly IRlpxHost _rlpxHost;
        private readonly INodeStatsManager _stats;
        private readonly ManualResetEventSlim _peerUpdateRequested = new(false);
        private readonly PeerComparer _peerComparer = new();
        private readonly IPeerPool _peerPool;
        private readonly List<PeerStats> _candidates;

        private int _pending;
        private int _tryCount;
        private int _newActiveNodes;
        private int _failedInitialConnect;
        private int _connectionRounds;

        private Timer? _peerUpdateTimer;

        private int _maxPeerPoolLength;
        private int _lastPeerPoolLength;

        private bool _isStarted;
        private int _logCounter = 1;
        private Task _peerUpdateLoopTask;

        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly int _parallelism;

        public PeerManager(
            IRlpxHost rlpxHost,
            IPeerPool peerPool,
            INodeStatsManager stats,
            INetworkConfig networkConfig,
            ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _rlpxHost = rlpxHost ?? throw new ArgumentNullException(nameof(rlpxHost));
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _parallelism = networkConfig.NumConcurrentOutgoingConnects;
            if (_parallelism == 0)
            {
                _parallelism = Environment.ProcessorCount;
            }

            _peerPool = peerPool;
            _candidates = new List<PeerStats>(networkConfig.MaxActivePeers * 2);
        }

        public IReadOnlyCollection<Peer> ActivePeers => _peerPool.ActivePeers.Values.ToList();
        public IReadOnlyCollection<Peer> CandidatePeers => _peerPool.Peers.Values.ToList();
        public IReadOnlyCollection<Peer> ConnectedPeers => _peerPool.ActivePeers.Values.Where(IsConnected).ToList();

        public int MaxActivePeers => _networkConfig.MaxActivePeers + _peerPool.StaticPeerCount;
        private int AvailableActivePeersCount => MaxActivePeers - _peerPool.ActivePeers.Count;

        /// <summary>
        /// The simplest hack for now until it is cleaned further.
        /// Peer manager / peer pool responsibilities still not clearly separated.
        /// </summary>
        private readonly ConcurrentDictionary<PublicKey, object> _nodesBeingAdded = new();

        /// <summary>
        /// New peer probably added via discovery app or some other form of discovery.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="nodeEventArgs"></param>
        private void PeerPoolOnPeerAdded(object sender, PeerEventArgs nodeEventArgs)
        {
            Peer peer = nodeEventArgs.Peer;

            lock (_peerPool)
            {
                int newPeerPoolLength = _peerPool.PeerCount;
                _lastPeerPoolLength = newPeerPoolLength;

                if (_lastPeerPoolLength > _maxPeerPoolLength + 100)
                {
                    _maxPeerPoolLength = _lastPeerPoolLength;
                    if (_logger.IsDebug) _logger.Debug($"Peer pool size is: {_lastPeerPoolLength}");
                }
            }

            _stats.ReportEvent(peer.Node, NodeStatsEventType.NodeDiscovered);
            if (_pending < AvailableActivePeersCount && CanConnectToPeer(peer))
            {
#pragma warning disable 4014

                // TODO: hack related to not clearly separated peer pool and peer manager
                if (!_nodesBeingAdded.ContainsKey(peer.Node.Id))
                {
                    // fire and forget - all the surrounding logic will be executed
                    // exceptions can be lost here without issues
                    // this for rapid connections to newly discovered peers without having to go through the UpdatePeerLoop
                    SetupOutgoingPeerConnection(peer);
                }
#pragma warning restore 4014
            }

            if (_isStarted)
            {
                _peerUpdateRequested.Set();
            }
        }

        private void PeerPoolOnPeerRemoved(object? sender, PeerEventArgs e)
        {
            e.Peer.IsAwaitingConnection = false;
            _peerPool.ActivePeers.TryRemove(e.Peer.Node.Id, out Peer _);
        }

        public void Start()
        {
            _peerPool.PeerAdded += PeerPoolOnPeerAdded;
            _peerPool.PeerRemoved += PeerPoolOnPeerRemoved;

            _rlpxHost.SessionCreated += (_, args) =>
            {
                ISession session = args.Session;
                ToggleSessionEventListeners(session, true);

                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session} created in peer manager");
                if (session.Direction == ConnectionDirection.Out)
                {
                    ProcessOutgoingConnection(session);
                }
            };

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

            await Task.CompletedTask;
            if (_logger.IsInfo) _logger.Info("Peer Manager shutdown complete.. please wait for all components to close");
        }

        #region Inactive peer loop handling. Peer may be discovered but inactive.

        private class CandidateSelection
        {
            public List<Peer> PreCandidates { get; } = new();
            public List<Peer> Candidates { get; } = new();
            public List<Peer> Incompatible { get; } = new();
            public Dictionary<ActivePeerSelectionCounter, int> Counters { get; } = new();
        }

        private readonly CandidateSelection _currentSelection = new();

        private async Task RunPeerUpdateLoop()
        {
            const int TIME_WAIT = 60_000;

            int loopCount = 0;
            long previousActivePeersCount = 0;
            int failCount = 0;
            while (true)
            {
                try
                {
                    if (loopCount++ % 100 == 0)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Running peer update loop {loopCount - 1} - active: {_peerPool.ActivePeerCount} | candidates : {_peerPool.PeerCount}");
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

                    Volatile.Write(ref _tryCount, 0);
                    Volatile.Write(ref _newActiveNodes, 0);
                    Volatile.Write(ref _failedInitialConnect, 0);
                    Volatile.Write(ref _connectionRounds, 0);

                    SelectAndRankCandidates();
                    List<Peer> remainingCandidates = _currentSelection.Candidates;
                    if (remainingCandidates.Count == 0)
                    {
                        continue;
                    }

                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        break;
                    }

                    int currentPosition = 0;
                    long lastMs = Environment.TickCount64;
                    int peersTried = 0;
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

                        peersTried += nodesToTry;
                        ActionBlock<Peer> workerBlock = new(
                            SetupOutgoingPeerConnection,
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
                        await workerBlock.Completion;

                        Interlocked.Increment(ref _connectionRounds);

                        long nowMs = Environment.TickCount64;
                        if (peersTried > 10_000)
                        {
                            peersTried = 0;
                            // Wait for sockets to clear
                            await Task.Delay(TIME_WAIT);
                        }
                        else
                        {
                            long diffMs = nowMs - lastMs;
                            if (diffMs < 50)
                            {
                                await Task.Delay(50 - (int)diffMs);
                            }
                        }
                        lastMs = nowMs;
                    }

                    if (_logger.IsTrace)
                    {
                        List<KeyValuePair<PublicKey, Peer>>? activePeers = _peerPool.ActivePeers.ToList();
                        int activePeersCount = activePeers.Count;
                        if (activePeersCount != previousActivePeersCount)
                        {
                            string countersLog = string.Join(", ", _currentSelection.Counters.Select(x => $"{x.Key.ToString()}: {x.Value}"));
                            _logger.Trace($"RunPeerUpdate | {countersLog}, Incompatible: {GetIncompatibleDesc(_currentSelection.Incompatible)}, EligibleCandidates: {_currentSelection.Candidates.Count}, " +
                                          $"Tried: {_tryCount}, Rounds: {_connectionRounds}, Failed initial connect: {_failedInitialConnect}, Established initial connect: {_newActiveNodes}, " +
                                          $"Current candidate peers: {_peerPool.PeerCount}, Current active peers: {activePeers.Count} " +
                                          $"[InOut: {activePeers.Count(x => x.Value.OutSession is not null && x.Value.InSession is not null)} | " +
                                          $"[Out: {activePeers.Count(x => x.Value.OutSession is not null)} | " +
                                          $"In: {activePeers.Count(x => x.Value.InSession is not null)}]");
                        }

                        previousActivePeersCount = activePeersCount;
                    }

                    if (_logger.IsTrace)
                    {
                        if (_logCounter % 5 == 0)
                        {
                            string nl = Environment.NewLine;
                            _logger.Trace($"{nl}{nl}All active peers: {nl} {string.Join(nl, _peerPool.ActivePeers.Values.Select(x => $"{x.Node:s} | P2P: {_stats.GetOrAdd(x.Node).DidEventHappen(NodeStatsEventType.P2PInitialized)} | Eth62: {_stats.GetOrAdd(x.Node).DidEventHappen(NodeStatsEventType.Eth62Initialized)} | {_stats.GetOrAdd(x.Node).P2PNodeDetails?.ClientId} | {_stats.GetOrAdd(x.Node).ToString()}"))} {nl}{nl}");
                        }

                        _logCounter++;
                    }

                    if (_peerPool.ActivePeerCount < MaxActivePeers)
                    {
                        // We been though all the peers once, so wait TIME-WAIT additional delay before
                        // trying them again to avoid busy loop or exhausting sockets.
                        await Task.Delay(TIME_WAIT);
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

                _peerUpdateTimer.Start();
            }
        }

        private static readonly IReadOnlyList<ActivePeerSelectionCounter> _enumValues = FastEnum.GetValues<ActivePeerSelectionCounter>();

        private void SelectAndRankCandidates()
        {
            if (AvailableActivePeersCount <= 0)
            {
                return;
            }

            _currentSelection.PreCandidates.Clear();
            _currentSelection.Candidates.Clear();
            _currentSelection.Incompatible.Clear();

            for (int i = 0; i < _enumValues.Count; i++)
            {
                _currentSelection.Counters[_enumValues[i]] = 0;
            }

            foreach ((_, Peer peer) in _peerPool.Peers)
            {
                // node can be connected but a candidate (for some short times)
                // [describe when]

                // node can be active but not connected (for some short times between sending connection request and
                // establishing a session)
                if (peer.IsAwaitingConnection || IsConnected(peer) || _peerPool.ActivePeers.TryGetValue(peer.Node.Id, out _))
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
            if (_currentSelection.PreCandidates.Count == 0)
            {
                _currentSelection.Candidates.AddRange(_peerPool.StaticPeers.Where(sn => !_peerPool.ActivePeers.ContainsKey(sn.Node.Id)));
                hasOnlyStaticNodes = _currentSelection.PreCandidates.Count > 0;
            }

            if (!_currentSelection.PreCandidates.Any() && !hasOnlyStaticNodes)
            {
                return;
            }

            _currentSelection.Counters[ActivePeerSelectionCounter.AllNonActiveCandidates] =
                _currentSelection.PreCandidates.Count;

            DateTime nowUTC = DateTime.UtcNow;
            foreach (Peer preCandidate in _currentSelection.PreCandidates)
            {
                if (preCandidate.Node.Port == 0)
                {
                    _currentSelection.Counters[ActivePeerSelectionCounter.FilteredByZeroPort]++;
                    continue;
                }

                (bool Result, NodeStatsEventType? DelayReason) delayResult = preCandidate.Stats.IsConnectionDelayed(nowUTC);
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

                if (preCandidate.Stats.FailedCompatibilityValidation.HasValue)
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
                _currentSelection.Candidates.AddRange(_peerPool.StaticPeers.Where(sn => !_peerPool.ActivePeers.ContainsKey(sn.Node.Id)));
            }

            foreach (Peer peer in _currentSelection.Candidates)
            {
                Node? node = peer.Node;

                if (node is null) continue;

                node.CurrentReputation = peer.Stats.CurrentNodeReputation(nowUTC);
            }

            _currentSelection.Candidates.Sort(_peerComparer);
        }

        private void StartPeerUpdateLoop()
        {
            if (_logger.IsDebug) _logger.Debug("Starting peer update timer");

            _peerUpdateTimer = new Timer(_networkConfig.PeersUpdateInterval);
            _peerUpdateTimer.Elapsed += (sender, e) =>
            {
                _peerUpdateTimer.Stop();
                _peerUpdateRequested.Set();
            };

            _peerUpdateTimer.Start();
        }

        private void StopTimers()
        {
            try
            {
                if (_logger.IsDebug) _logger.Debug("Stopping peer timers");
                _peerUpdateTimer?.Stop();
                _peerUpdateTimer?.Dispose();
            }
            catch (Exception e)
            {
                _logger.Error("Error during peer timers stop", e);
            }
        }

        private void CleanupCandidatePeers()
        {
            int peerCount = _peerPool.PeerCount;

            if (peerCount <= _networkConfig.CandidatePeerCountCleanupThreshold)
            {
                return;
            }

            try
            {
                int failedValidationCandidatesCount = 0;
                foreach ((PublicKey key, Peer peer) in _peerPool.Peers)
                {
                    if (!peer.Node.IsStatic)
                    {
                        bool hasFailedValidation = _stats.HasFailedValidation(peer.Node);
                        if (hasFailedValidation)
                        {
                            failedValidationCandidatesCount++;
                            _candidates.Add(new PeerStats(peer, true, _stats.GetCurrentReputation(peer.Node)));
                        }
                        else
                        {
                            bool isActivePeer = _peerPool.ActivePeers.ContainsKey(key);
                            if (!isActivePeer)
                            {
                                _candidates.Add(new PeerStats(peer, false, _stats.GetCurrentReputation(peer.Node)));
                            }
                        }
                    }
                }

                _candidates.Sort(static (x, y) => PeerStatsComparer.Instance.Compare(x, y));

                int countToRemove = _candidates.Count - _networkConfig.MaxCandidatePeerCount;
                if (countToRemove > 0)
                {
                    _logger.Info($"Removing {countToRemove} out of {_candidates.Count} peer candidates (candidates cleanup).");

                    for (int i = 0; i < countToRemove; i++)
                    {
                        _peerPool.TryRemove(_candidates[i].Peer!.Node.Id, out _);
                    }

                    if (_logger.IsDebug)
                    {
                        int failedValidationRemovedCount = Math.Min(failedValidationCandidatesCount, countToRemove);
                        _logger.Debug($"Removing candidate peers: {countToRemove}, failedValidationRemovedCount: {failedValidationRemovedCount}, otherRemovedCount: {countToRemove - failedValidationRemovedCount}, prevCount: {_candidates.Count}, newCount: {peerCount}, CandidatePeerCountCleanupThreshold: {_networkConfig.CandidatePeerCountCleanupThreshold}, MaxCandidatePeerCount: {_networkConfig.MaxCandidatePeerCount}");
                    }
                }
            }
            finally
            {
                _candidates.Clear();
            }
        }

        private enum ActivePeerSelectionCounter
        {
            AllNonActiveCandidates,
            FilteredByZeroPort,
            FilteredByDisconnect,
            FilteredByFailedConnection
        }

        private readonly struct PeerStats
        {
            public Peer Peer { get; }
            public bool FailedValidation { get; }
            public long CurrentReputation { get; }

            public PeerStats(Peer peer, bool failedValidation, long currentReputation)
            {
                Peer = peer;
                FailedValidation = failedValidation;
                CurrentReputation = currentReputation;
            }
        }

        private class PeerStatsComparer : IComparer<PeerStats>
        {
            public static readonly PeerStatsComparer Instance = new();

            public int Compare(PeerStats x, PeerStats y)
            {
                int failedValidationCompare = y.FailedValidation.CompareTo(x.FailedValidation);
                return failedValidationCompare != 0
                    ? failedValidationCompare
                    : x.CurrentReputation.CompareTo(y.CurrentReputation);
            }
        }

        #endregion

        #region Outgoing connection handling

        [Todo(Improve.MissingFunctionality, "Add cancellation support for the peer connection (so it does not wait for the 10sec timeout")]
        private async Task SetupOutgoingPeerConnection(Peer peer)
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
            bool result = await InitializeOutgoingPeerConnection(peer);
            // for some time we will have a peer in active that has no session assigned - analyze this?

            Interlocked.Decrement(ref _pending);
            if (_logger.IsTrace) _logger.Trace($"Connecting to {_stats.GetCurrentReputation(peer.Node)} rep node - {result}, ACTIVE: {_peerPool.ActivePeerCount}, CAND: {_peerPool.PeerCount}");

            if (!result)
            {
                _stats.ReportEvent(peer.Node, NodeStatsEventType.ConnectionFailed);
                Interlocked.Increment(ref _failedInitialConnect);
                if (peer.OutSession is not null)
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

        private async Task<bool> InitializeOutgoingPeerConnection(Peer candidate)
        {
            try
            {
                if (_logger.IsTrace) _logger.Trace($"CONNECTING TO {candidate}");
                candidate.IsAwaitingConnection = true;
                _stats.ReportEvent(candidate.Node, NodeStatsEventType.Connecting);
                await _rlpxHost.ConnectAsync(candidate.Node);
                return true;
            }
            catch (NetworkingException ex)
            {
                if (ex.NetworkExceptionType == NetworkExceptionType.TargetUnreachable)
                {
                    _stats.ReportEvent(candidate.Node, NodeStatsEventType.ConnectionFailedTargetUnreachable);
                }

                if (_logger.IsTrace) _logger.Trace($"Cannot connect to peer [{ex.NetworkExceptionType.ToString()}]: {candidate.Node:s}");
                return false;
            }
            catch (Exception ex)
            {
                if (_logger.IsDebug) _logger.Error($"Error trying to initiate connection with peer: {candidate.Node:s}", ex);
                return false;
            }
        }

        /// <summary>
        /// For when getting event from rlpx. Connection here is probably initiated from `InitializeOutgoingPeerConnection`
        /// </summary>
        /// <param name="session"></param>
        private void ProcessOutgoingConnection(ISession session)
        {
            PublicKey id = session.RemoteNodeId;
            if (_logger.IsTrace) _logger.Trace($"PROCESS OUTGOING {id}");

            if (!_peerPool.ActivePeers.TryGetValue(id, out Peer peer))
            {
                session.MarkDisconnected(DisconnectReason.DisconnectRequested, DisconnectType.Local, "peer removed");
                return;
            }

            _stats.ReportEvent(peer.Node, NodeStatsEventType.ConnectionEstablished);

            AddSession(session, peer);
        }

        #endregion

        #region Incoming connection handling

        private void ProcessIncomingConnection(ISession session)
        {
            if (_peerPool.TryGet(session.Node.Id, out Peer existingPeer))
            {
                // TODO: here the session.Node may not be equal peer.Node -> would be good to check if we can improve it
                session.Node.IsStatic = existingPeer.Node.IsStatic;
            }

            if (_logger.IsTrace) _logger.Trace($"INCOMING {session}");

            // if we have already initiated connection before
            if (_peerPool.ActivePeers.TryGetValue(session.RemoteNodeId, out Peer existingActivePeer))
            {
                AddSession(session, existingActivePeer);
                return;
            }

            if (!session.Node.IsStatic && _peerPool.ActivePeers.Count >= MaxActivePeers)
            {
                int initCount = 0;
                foreach (KeyValuePair<PublicKey, Peer> pair in _peerPool.ActivePeers)
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
                    session.InitiateDisconnect(InitiateDisconnectReason.TooManyPeers, $"{initCount}");
                    return;
                }
            }

            try
            {
                _nodesBeingAdded.TryAdd(session.RemoteNodeId, null);
                Peer peer = _peerPool.GetOrAdd(session.Node);
                AddSession(session, peer);
            }
            finally
            {
                _nodesBeingAdded.TryRemove(session.RemoteNodeId, out _);
            }
        }

        #endregion

        private bool CanConnectToPeer(Peer peer)
        {
            if (_stats.FindCompatibilityValidationResult(peer.Node).HasValue)
            {
                if (_logger.IsTrace) _logger.Trace($"Not connecting peer: {peer} due to failed compatibility result");
                return false;
            }

            (bool delayed, NodeStatsEventType? reason) = peer.Stats.IsConnectionDelayed(DateTime.UtcNow);
            if (delayed)
            {
                if (_logger.IsTrace) _logger.Trace($"Not connecting peer: {peer} due forced connection delay. Reason: {reason}");
                return false;
            }

            return true;
        }

        private bool AddActivePeer(PublicKey nodeId, Peer peer, string reason)
        {
            peer.IsAwaitingConnection = false;
            bool added = _peerPool.ActivePeers.TryAdd(nodeId, peer);
            if (added)
            {
                Interlocked.Increment(ref Metrics.PeerCount);
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
            bool removed = _peerPool.ActivePeers.TryRemove(nodeId, out _);
            if (removed)
            {
                Interlocked.Decrement(ref Metrics.PeerCount);
            }
            // if (removed && _logger.IsDebug) _logger.Debug($"{removedPeer.Node:s} removed from active peers - {reason}");
        }

        private void DeactivatePeerIfDisconnected(Peer peer, string reason)
        {
            if (_logger.IsTrace) _logger.Trace($"DEACTIVATING IF DISCONNECTED {peer}");
            if (!IsConnected(peer) && !peer.IsAwaitingConnection)
            {
                // dropping references to sessions so they can be garbage collected
                peer.InSession = null;
                peer.OutSession = null;
                RemoveActivePeer(peer.Node.Id, reason);
            }
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

        private ConnectionDirection ChooseDirectionToKeep(PublicKey remoteNode)
        {
            if (_logger.IsTrace) _logger.Trace($"CHOOSING DIRECTION {remoteNode}");
            byte[] localKey = _rlpxHost.LocalNodeId.Bytes;
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

        private void AddSession(ISession session, Peer peer)
        {
            if (_logger.IsTrace) _logger.Trace($"ADDING {session} {peer}");
            bool newSessionIsIn = session.Direction == ConnectionDirection.In;
            bool newSessionIsOut = !newSessionIsIn;
            bool peerIsDisconnected = !IsConnected(peer);

            if (peerIsDisconnected || (peer.IsAwaitingConnection && session.Direction == ConnectionDirection.Out))
            {
                if (newSessionIsIn)
                {
                    peer.Stats.AddNodeStatsHandshakeEvent(ConnectionDirection.In);
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
                    session.InitiateDisconnect(InitiateDisconnectReason.SessionAlreadyExist, "same");
                }
                else if (newSessionIsIn && peerHasAnOpenOutSession || newSessionIsOut && peerHasAnOpenInSession)
                {
                    // disconnecting the new session as it lost to the existing one
                    ConnectionDirection directionToKeep = ChooseDirectionToKeep(session.RemoteNodeId);
                    if (session.Direction != directionToKeep)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Disconnecting a new {session} - {directionToKeep} session already connected");
                        session.InitiateDisconnect(InitiateDisconnectReason.ReplacingSessionWithOppositeDirection, "same");
                        if (newSessionIsIn)
                        {
                            peer.Stats.AddNodeStatsHandshakeEvent(ConnectionDirection.In);
                            peer.InSession = session;
                        }
                        else
                        {
                            peer.OutSession = session;
                        }
                    }
                    // replacing existing session with the new one as the new one won
                    else if (newSessionIsIn)
                    {
                        peer.InSession = session;
                        if (_logger.IsDebug) _logger.Debug($"Disconnecting an existing {session} - {directionToKeep} session to replace");
                        peer.OutSession?.InitiateDisconnect(InitiateDisconnectReason.OppositeDirectionCleanup, "same");
                    }
                    else
                    {
                        peer.OutSession = session;
                        if (_logger.IsDebug) _logger.Debug($"Disconnecting an existing {session} - {directionToKeep} session to replace");
                        peer.InSession?.InitiateDisconnect(InitiateDisconnectReason.OppositeDirectionCleanup, "same");
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
            ISession session = (ISession)sender;
            ToggleSessionEventListeners(session, false);
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session} closing");

            if (session.State != SessionState.Disconnected)
            {
                throw new InvalidAsynchronousStateException($"Invalid session state in {nameof(OnDisconnected)} - {session.State}");
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| peer disconnected event in PeerManager - {session} {e.DisconnectReason} {e.DisconnectType}");

            if (session.RemoteNodeId is null)
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

            if (_peerPool.ActivePeers.TryGetValue(session.RemoteNodeId, out Peer activePeer))
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
            ISession session = (ISession)sender;
            _stats.GetOrAdd(session.Node);

            //In case of OUT connections and different RemoteNodeId we need to replace existing Active Peer with new peer
            ManageNewRemoteNodeId(session);

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session} completed handshake - peer manager handling");

            //This is the first moment we get confirmed publicKey of remote node in case of incoming connections
            if (session.Direction == ConnectionDirection.In)
            {
                // For incoming connection, this is the entry point.
                ProcessIncomingConnection(session);
            }
            else
            {
                if (!_peerPool.ActivePeers.TryGetValue(session.RemoteNodeId, out Peer peer))
                {
                    //Can happen when peer sent Disconnect message before handshake is done, it takes us a while to disconnect
                    if (_logger.IsTrace) _logger.Trace($"Initiated handshake (OUT) with a peer without adding it to the Active collection : {session}");
                    return;
                }

                peer.Stats.AddNodeStatsHandshakeEvent(ConnectionDirection.Out);
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session} handshake initialized in peer manager");
        }

        private void ManageNewRemoteNodeId(ISession session)
        {
            if (session.ObsoleteRemoteNodeId is null)
            {
                return;
            }

            Peer newPeer = _peerPool.Replace(session);

            RemoveActivePeer(session.ObsoleteRemoteNodeId, $"handshake difference old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}");
            AddActivePeer(session.RemoteNodeId, newPeer, $"handshake difference old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}");
            if (_logger.IsTrace) _logger.Trace($"RemoteNodeId was updated due to handshake difference, old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}, new peer not present in candidate collection");
        }
    }
}
