// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
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
        private readonly INetworkConfig _networkConfig;
        private readonly IRlpxHost _rlpxHost;
        private readonly INodeStatsManager _stats;
        private readonly SemaphoreSlim _peerUpdateRequested = new(0, 1);
        private readonly PeerComparer _peerComparer = new();
        private Task? _peerUpdateLoopTask;
        private readonly IPeerPool _peerPool;
        private readonly Lock _sessionLock = new();
        private readonly List<PeerStats> _candidates;
        private readonly RateLimiter _outgoingConnectionRateLimiter;

        private int _pending;
        private int _tryCount;
        private int _newActiveNodes;
        private int _failedInitialConnect;
        private int _connectionRounds;

        private Timer? _peerUpdateTimer;

        private int _maxPeerPoolLength;

        private volatile bool _isStarted;
        private int _logCounter = 1;
        private bool _isStopping; // guarded by _sessionLock

        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly ConcurrentDictionary<Guid, ISession> _sessions = new();
        private readonly int _outgoingConnectParallelism;
        private readonly EventHandler<EventArgs> _onHandshakeComplete;
        private readonly EventHandler<DisconnectEventArgs> _onDisconnected;

        public PeerManager(
            IRlpxHost rlpxHost,
            IPeerPool peerPool,
            INodeStatsManager stats,
            INetworkConfig networkConfig,
            ILogManager logManager)
        {
            ArgumentNullException.ThrowIfNull(rlpxHost);
            ArgumentNullException.ThrowIfNull(peerPool);
            ArgumentNullException.ThrowIfNull(stats);
            ArgumentNullException.ThrowIfNull(networkConfig);
            ArgumentNullException.ThrowIfNull(logManager);

            _logger = logManager.GetClassLogger();
            _rlpxHost = rlpxHost;
            _stats = stats;
            _networkConfig = networkConfig;
            _onHandshakeComplete = OnHandshakeComplete;
            _onDisconnected = OnDisconnected;
            _outgoingConnectParallelism = networkConfig.NumConcurrentOutgoingConnects;
            if (_outgoingConnectParallelism == 0)
            {
                _outgoingConnectParallelism = Environment.ProcessorCount;
            }
            _outgoingConnectionRateLimiter = new RateLimiter(networkConfig.MaxOutgoingConnectPerSec);

            _peerPool = peerPool;
            _candidates = new List<PeerStats>(networkConfig.MaxActivePeers * 2);
        }

        public IReadOnlyCollection<Peer> ActivePeers => _peerPool.ActivePeers.Values.ToList();
        public IReadOnlyCollection<Peer> CandidatePeers => _peerPool.Peers.Values.ToList();
        public IReadOnlyCollection<Peer> ConnectedPeers => _peerPool.ActivePeers.Values.Where(IsConnected).ToList();

        public int MaxActivePeers => _networkConfig.MaxActivePeers + _peerPool.StaticPeerCount;
        public int ActivePeersCount => _peerPool.ActivePeerCount;
        public int ConnectedPeersCount => _peerPool.ActivePeers.Values.Count(IsConnected);
        private int AvailableActivePeersCount => MaxActivePeers - _peerPool.ActivePeers.Count;

        /// <summary>
        /// Allow some incoming peer connection before disconnecting. This is to allow the protocol to be initialized
        /// before disconnecting through <see cref="IProtocolValidator"/> so that disconnect message is sent properly
        /// </summary>
        private int MaxActivePeerMargin = 10;

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
            int newPeerPoolLength = _peerPool.PeerCount;
            int currentMaxPeerPoolLength = Volatile.Read(ref _maxPeerPoolLength);
            while (newPeerPoolLength > currentMaxPeerPoolLength + 100)
            {
                int previousMaxPeerPoolLength = Interlocked.CompareExchange(
                    ref _maxPeerPoolLength,
                    newPeerPoolLength,
                    currentMaxPeerPoolLength);
                if (previousMaxPeerPoolLength == currentMaxPeerPoolLength)
                {
                    if (_logger.IsDebug) DebugPeerPoolSize(newPeerPoolLength);
                    break;
                }

                currentMaxPeerPoolLength = previousMaxPeerPoolLength;
            }

            _stats.ReportEvent(peer.Node, NodeStatsEventType.NodeDiscovered);
            if (_pending < AvailableActivePeersCount && CanConnectToPeer(peer))
            {
#pragma warning disable 4014

                // TODO: hack related to not clearly separated peer pool and peer manager
                if (CanQuickConnect(peer))
                {
                    // fire and forget - all the surrounding logic will be executed
                    // exceptions can be lost here without issues
                    // this for rapid connections to newly discovered peers without having to go through the UpdatePeerLoop
                    SetupOutgoingPeerConnection(peer, cancelIfThrottled: true);
                }
#pragma warning restore 4014
            }

            if (_isStarted)
            {
                SignalPeerUpdateNeeded();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void DebugPeerPoolSize(int peerPoolLength)
                => _logger.Debug($"Peer pool size is: {peerPoolLength}");
        }

        private void PeerPoolOnPeerRemoved(object? sender, PeerEventArgs e)
        {
            e.Peer.IsAwaitingConnection = false;
            _peerPool.ActivePeers.TryRemove(e.Peer.Node.Id, out Peer _);
        }

        public void Start()
        {
            lock (_sessionLock)
            {
                _isStopping = false;
                _peerPool.PeerAdded += PeerPoolOnPeerAdded;
                _peerPool.PeerRemoved += PeerPoolOnPeerRemoved;
                _rlpxHost.SessionCreated += RlpxHostOnSessionCreated;
            }

            StartPeerUpdateLoop();

            _peerUpdateLoopTask = RunPeerUpdateLoopAsync();

            _isStarted = true;
            SignalPeerUpdateNeeded();
        }

        private async Task RunPeerUpdateLoopAsync()
        {
            await Task.Yield();

            try
            {
                await RunPeerUpdateLoop();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (_logger.IsError) _logger.Error("Peer update loop encountered an exception.", e);
            }
            catch (OperationCanceledException)
            {
                if (_logger.IsDebug) _logger.Debug("Peer update loop stopped.");
            }
        }

        public async Task StopAsync()
        {
            lock (_sessionLock)
            {
                _isStopping = true;
                _isStarted = false;
                _peerPool.PeerAdded -= PeerPoolOnPeerAdded;
                _peerPool.PeerRemoved -= PeerPoolOnPeerRemoved;
                _rlpxHost.SessionCreated -= RlpxHostOnSessionCreated;
                foreach (ISession session in _sessions.Values)
                {
                    ToggleSessionEventListeners(session, false);
                }

                _sessions.Clear();
            }

            _cancellationTokenSource.Cancel();

            if (_peerUpdateLoopTask is not null)
            {
                try
                {
                    await _peerUpdateLoopTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
            }

            StopTimers();

            if (_logger.IsInfo) _logger.Info("Peer Manager shutdown complete. Please wait for all components to close");
        }

        string IStoppableService.Description => "peer manager";

        private class CandidateSelection
        {
            public List<Peer> PreCandidates { get; } = new();
            public List<Peer> Candidates { get; } = new();
            public List<Peer> Incompatible { get; } = new();
            public Dictionary<string, int> Counters { get; } = new();
        }

        private readonly CandidateSelection _currentSelection = new();

        private async Task RunPeerUpdateLoop()
        {
            Channel<Peer> taskChannel = Channel.CreateBounded<Peer>(1);
            using ArrayPoolList<Task> tasks = Enumerable.Range(0, _outgoingConnectParallelism).Select(async idx =>
            {
                await foreach (Peer peer in taskChannel.Reader.ReadAllAsync(_cancellationTokenSource.Token))
                {
                    try
                    {
                        if (ShouldContactPeer(peer))
                        {
                            await SetupOutgoingPeerConnection(peer);
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        if (_logger.IsDebug) DebugConnectWorker(idx, isCancelled: true);
                        break;
                    }
                    catch (Exception e)
                    {
                        // This is strictly speaking not related to the connection, but something outside of it.
                        if (_logger.IsError) _logger.Error($"Error setting up connection to {peer}, {e}");
                    }
                }
                if (_logger.IsDebug) DebugConnectWorker(idx, isCancelled: false);
            }).ToPooledList(_outgoingConnectParallelism);

            int loopCount = 0;
            long previousActivePeersCount = 0;
            int failCount = 0;
            while (true)
            {
                try
                {
                    if (_logger.IsTrace && loopCount % 100 == 0)
                    {
                        TracePeerUpdateLoopIteration(loopCount);
                    }

                    loopCount++;
                    CleanupCandidatePeersSafely();
                    await WaitForPeerUpdateRequestAsync();

                    if (!await CanRunPeerUpdateIterationAsync())
                    {
                        continue;
                    }

                    ResetConnectionRoundCounters();

                    List<Peer>? remainingCandidates = await GetRemainingCandidates();
                    if (remainingCandidates is null)
                    {
                        continue;
                    }

                    if (ShouldStopPeerUpdateLoop())
                    {
                        break;
                    }

                    await QueueRemainingCandidates(taskChannel, remainingCandidates);
                    if (_logger.IsTrace || (_logger.IsDebug && _logCounter % 5 == 0))
                    {
                        previousActivePeersCount = LogPeerUpdateProgress(previousActivePeersCount);
                    }

                    if (_logger.IsTrace && _logCounter % 5 == 0)
                    {
                        TraceActivePeers();
                    }
                    _logCounter++;

                    await RequestAnotherPeerUpdateIfNeededAsync();

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
                        if (_logger.IsError) _logger.Error("Too much failure in peer update loop", e);
                        break;
                    }
                    else
                    {
                        await Task.Delay(1000, _cancellationTokenSource.Token);
                    }
                }

                _peerUpdateTimer?.Start();
            }

            taskChannel.Writer.Complete();
            await Task.WhenAll(tasks.AsSpan());

            [MethodImpl(MethodImplOptions.NoInlining)]
            void DebugConnectWorker(int workerIndex, bool isCancelled)
                => _logger.Debug($"Connect worker {workerIndex} {(isCancelled ? "cancelled" : "completed")}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TracePeerUpdateLoopIteration(int currentLoopCount)
                => _logger.Trace($"Running peer update loop {currentLoopCount} - active: {_peerPool.ActivePeerCount} | candidates : {_peerPool.PeerCount}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            long LogPeerUpdateProgress(long activePeersBeforeLoop)
            {
                KeyValuePair<PublicKeyAsKey, Peer>[] activePeers = _peerPool.ActivePeers.ToArray();
                int activePeersCount = activePeers.Length;
                if (activePeersCount != activePeersBeforeLoop)
                {
                    string countersLog = string.Join(", ", _currentSelection.Counters.Select(x => $"{x.Key}: {x.Value}"));
                    _logger.Debug($"RunPeerUpdate | {countersLog}, Incompatible: {GetIncompatibleDesc(_currentSelection.Incompatible)}, EligibleCandidates: {_currentSelection.Candidates.Count}, " +
                                  $"Tried: {_tryCount}, Rounds: {_connectionRounds}, Failed initial connect: {_failedInitialConnect}, Established initial connect: {_newActiveNodes}, " +
                                  $"Current candidate peers: {_peerPool.PeerCount}, Current active peers: {activePeers.Length} " +
                                  $"[InOut: {activePeers.Count(x => x.Value.OutSession is not null && x.Value.InSession is not null)} | " +
                                  $"[Out: {activePeers.Count(x => x.Value.OutSession is not null)} | " +
                                  $"In: {activePeers.Count(x => x.Value.InSession is not null)}]");
                }

                return activePeersCount;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceActivePeers()
            {
                string nl = Environment.NewLine;
                _logger.Trace($"{nl}{nl}All active peers: {nl} {string.Join(nl, _peerPool.ActivePeers.Values.Select(x => $"{x.Node:s} | P2P: {_stats.GetOrAdd(x.Node).DidEventHappen(NodeStatsEventType.P2PInitialized)} | Eth62: {_stats.GetOrAdd(x.Node).DidEventHappen(NodeStatsEventType.Eth62Initialized)} | {_stats.GetOrAdd(x.Node).P2PNodeDetails?.ClientId} | {_stats.GetOrAdd(x.Node)}"))} {nl}{nl}");
            }
        }

        private void CleanupCandidatePeersSafely()
        {
            try
            {
                CleanupCandidatePeers();
            }
            catch (Exception e)
            {
                if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Candidate peers cleanup failed", e);
            }
        }

        private async Task WaitForPeerUpdateRequestAsync()
        {
            Metrics.PeerCandidateCount = _peerPool.PeerCount;

            await _peerUpdateRequested.WaitAsync(_cancellationTokenSource.Token);
        }

        private void SignalPeerUpdateNeeded()
        {
            // Fast path: already signaled, nothing to do.
            if (_peerUpdateRequested.CurrentCount > 0) return;

            lock (_peerUpdateRequested)
            {
                if (_peerUpdateRequested.CurrentCount > 0) return;
                _peerUpdateRequested.Release();
            }
        }

        private async Task<bool> CanRunPeerUpdateIterationAsync()
            => _isStarted && await EnsureAvailableActivePeerSlotAsync();

        private void ResetConnectionRoundCounters()
        {
            Volatile.Write(ref _tryCount, 0);
            Volatile.Write(ref _newActiveNodes, 0);
            Volatile.Write(ref _failedInitialConnect, 0);
            Volatile.Write(ref _connectionRounds, 0);
        }

        private async ValueTask<List<Peer>?> GetRemainingCandidates()
        {
            SelectAndRankCandidates();
            List<Peer> remainingCandidates = _currentSelection.Candidates;
            if (remainingCandidates.Count != 0)
            {
                return remainingCandidates;
            }

            // Delay to prevent high CPU use. There is a shortcut path for newly discovered peer, so having
            // a lower delay probably won't do much.
            await Task.Delay(TimeSpan.FromSeconds(1), _cancellationTokenSource.Token);
            return null;
        }

        private bool ShouldStopPeerUpdateLoop()
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                return false;
            }

            if (_logger.IsInfo) _logger.Info("Peer update loop canceled");
            return true;
        }

        private async Task QueueRemainingCandidates(Channel<Peer> taskChannel, List<Peer> remainingCandidates)
        {
            foreach (Peer peer in remainingCandidates)
            {
                if (!await EnsureAvailableActivePeerSlotAsync())
                {
                    // Some new connection are in flight at this point, but statistically speaking, they
                    // are going to fail, so its fine.
                    break;
                }

                await taskChannel.Writer.WriteAsync(peer, _cancellationTokenSource.Token);
            }
        }


        private async Task RequestAnotherPeerUpdateIfNeededAsync()
        {
            if (await EnsureAvailableActivePeerSlotAsync())
            {
                SignalPeerUpdateNeeded();
            }
        }

        private async Task<bool> EnsureAvailableActivePeerSlotAsync()
        {
            if (AvailableActivePeersCount - _pending > 0)
            {
                return true;
            }

            // Once the connection was established, the active peer count will increase, but it might
            // not pass the handshake and the status check. So we wait for a bit to see if we can get
            // the active peer count to go down within this time window.
            DateTimeOffset deadline = DateTimeOffset.UtcNow + Timeouts.Handshake +
                                      TimeSpan.FromMilliseconds(_networkConfig.ConnectTimeoutMs);
            while (DateTimeOffset.UtcNow < deadline && (AvailableActivePeersCount - _pending) <= 0)
            {
                // Wait for a signal or poll every 100ms.
                await _peerUpdateRequested.WaitAsync(TimeSpan.FromMilliseconds(100), _cancellationTokenSource.Token);
            }

            return AvailableActivePeersCount - _pending > 0;
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
            _currentSelection.Counters.Clear();

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

            if (_currentSelection.PreCandidates.Count == 0 && !hasOnlyStaticNodes)
            {
                return;
            }

            DateTime nowUTC = DateTime.UtcNow;
            foreach (Peer preCandidate in _currentSelection.PreCandidates)
            {
                if (preCandidate.Node.Port == 0)
                {
                    _currentSelection.Counters.Increment(ActivePeerSelectionCounter.FilteredByZeroPort.ToString());
                    continue;
                }

                (bool Result, NodeStatsEventType? DelayReason) delayResult = preCandidate.Stats.IsConnectionDelayed(nowUTC);
                if (delayResult.Result)
                {
                    _currentSelection.Counters.Increment(delayResult.DelayReason.ToString());

                    continue;
                }

                if (preCandidate.Stats.FailedCompatibilityValidation.HasValue)
                {
                    _currentSelection.Counters.Increment(ActivePeerSelectionCounter.Incompatible.ToString());
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

            foreach (KeyValuePair<string, int> currentSelectionCounter in _currentSelection.Counters)
            {
                Metrics.PeerCandidateFilter.AddBy(
                    currentSelectionCounter.Key,
                    currentSelectionCounter.Value);
            }
        }

        private void StartPeerUpdateLoop()
        {
            if (_logger.IsDebug) _logger.Debug("Starting peer update timer");

            _peerUpdateTimer = new Timer(_networkConfig.PeersUpdateInterval);
            _peerUpdateTimer.Elapsed += PeerUpdateTimerOnElapsed;

            _peerUpdateTimer.Start();
        }

        private void StopTimers()
        {
            try
            {
                if (_logger.IsDebug) _logger.Debug("Stopping peer timers");
                Timer? peerUpdateTimer = _peerUpdateTimer;
                if (peerUpdateTimer is null)
                {
                    return;
                }

                peerUpdateTimer.Elapsed -= PeerUpdateTimerOnElapsed;
                peerUpdateTimer.Stop();
                peerUpdateTimer.Dispose();
                _peerUpdateTimer = null;
            }
            catch (Exception e)
            {
                _logger.Error("Error during peer timers stop", e);
            }
        }

        private void RlpxHostOnSessionCreated(object? sender, SessionEventArgs args)
        {
            ISession session = args.Session;
            bool isOutgoing;
            lock (_sessionLock)
            {
                if (_isStopping)
                {
                    return;
                }

                _sessions.TryAdd(session.SessionId, session);
                ToggleSessionEventListeners(session, true);

                if (_logger.IsTrace) TraceSessionLifecycle(session, SessionLifecycleTraceEvent.Created);
                isOutgoing = session.Direction == ConnectionDirection.Out;
            }

            // ProcessOutgoingConnection may call MarkDisconnected/InitiateDisconnect,
            // which fires Disconnected → OnDisconnected that also acquires _sessionLock.
            // Must be called outside the lock to avoid deadlock.
            if (isOutgoing)
            {
                ProcessOutgoingConnection(session);
            }
        }

        private void PeerUpdateTimerOnElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            _peerUpdateTimer?.Stop();
            SignalPeerUpdateNeeded();
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
                        DebugRemovingCandidatePeers(countToRemove, failedValidationRemovedCount);
                    }
                }
            }
            finally
            {
                _candidates.Clear();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void DebugRemovingCandidatePeers(int removed, int failedValidationRemovedCount)
                => _logger.Debug($"Removing candidate peers: {removed}, failedValidationRemovedCount: {failedValidationRemovedCount}, otherRemovedCount: {removed - failedValidationRemovedCount}, prevCount: {_candidates.Count}, newCount: {peerCount}, CandidatePeerCountCleanupThreshold: {_networkConfig.CandidatePeerCountCleanupThreshold}, MaxCandidatePeerCount: {_networkConfig.MaxCandidatePeerCount}");
        }

        private enum ActivePeerSelectionCounter
        {
            FilteredByZeroPort,
            Incompatible
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

        [Todo(Improve.MissingFunctionality, "Add cancellation support for the peer connection (so it does not wait for the 10sec timeout")]
        private async Task SetupOutgoingPeerConnection(Peer peer, bool cancelIfThrottled = false)
        {
            if (cancelIfThrottled && _outgoingConnectionRateLimiter.IsThrottled()) return;

            await _outgoingConnectionRateLimiter.WaitAsync(_cancellationTokenSource.Token);

            // Can happen when In connection is received from the same peer and is initialized before we get here
            // In this case we do not initialize OUT connection
            if (!AddActivePeer(peer.Node.Id, peer, "upgrading candidate"))
            {
                if (_logger.IsTrace) TraceActivePeerAlreadyAddedToCollection();
                return;
            }

            Interlocked.Increment(ref _tryCount);
            Interlocked.Increment(ref _pending);
            bool result = await InitializeOutgoingPeerConnection(peer);
            // for some time we will have a peer in active that has no session assigned - analyze this?

            Interlocked.Decrement(ref _pending);
            SignalPeerUpdateNeeded();
            if (_logger.IsTrace) TraceOutgoingConnectionResult();

            if (!result)
            {
                _stats.ReportEvent(peer.Node, NodeStatsEventType.ConnectionFailed);
                Interlocked.Increment(ref _failedInitialConnect);
                if (peer.OutSession is not null)
                {
                    if (_logger.IsTrace) TraceTimeoutDisconnect();
                    peer.OutSession?.MarkDisconnected(DisconnectReason.OutgoingConnectionFailed, DisconnectType.Local, "timeout");
                }

                peer.IsAwaitingConnection = false;
                DeactivatePeerIfDisconnected(peer, "Failed to initialize connections");

                return;
            }

            Interlocked.Increment(ref _newActiveNodes);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceActivePeerAlreadyAddedToCollection()
                => _logger.Trace($"Active peer was already added to collection: {peer.Node.Id}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceOutgoingConnectionResult()
                => _logger.Trace($"Connecting to {_stats.GetCurrentReputation(peer.Node)} rep node - {result}, ACTIVE: {_peerPool.ActivePeerCount}, CAND: {_peerPool.PeerCount}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceTimeoutDisconnect()
                => _logger.Trace($"Timeout, doing additional disconnect: {peer.Node.Id}");
        }

        private async Task<bool> InitializeOutgoingPeerConnection(Peer candidate)
        {
            try
            {
                if (_logger.IsTrace) TraceConnectingToCandidate();
                candidate.IsAwaitingConnection = true;
                _stats.ReportEvent(candidate.Node, NodeStatsEventType.Connecting);
                return await _rlpxHost.ConnectAsync(candidate.Node);
            }
            catch (NetworkingException ex)
            {
                if (ex.NetworkExceptionType == NetworkExceptionType.TargetUnreachable)
                {
                    _stats.ReportEvent(candidate.Node, NodeStatsEventType.ConnectionFailedTargetUnreachable);
                }

                if (_logger.IsTrace) TraceCannotConnectToPeer(ex.NetworkExceptionType);
                return false;
            }
            catch (Exception ex)
            {
                if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Error trying to initiate connection with peer: {candidate.Node:s}", ex);
                return false;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceConnectingToCandidate()
                => _logger.Trace($"CONNECTING TO {candidate}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCannotConnectToPeer(NetworkExceptionType networkExceptionType)
                => _logger.Trace($"Cannot connect to peer [{networkExceptionType}]: {candidate.Node:s}");
        }

        /// <summary>
        /// For when getting event from rlpx. Connection here is probably initiated from `InitializeOutgoingPeerConnection`
        /// </summary>
        /// <param name="session"></param>
        private void ProcessOutgoingConnection(ISession session)
        {
            PublicKey id = session.RemoteNodeId;
            if (_logger.IsTrace) TraceProcessingOutgoing();

            if (!_peerPool.ActivePeers.TryGetValue(id, out Peer peer))
            {
                session.MarkDisconnected(DisconnectReason.DuplicatedConnection, DisconnectType.Local, "peer removed");
                return;
            }

            _stats.ReportEvent(peer.Node, NodeStatsEventType.ConnectionEstablished);

            AddSession(session, peer);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceProcessingOutgoing()
                => _logger.Trace($"PROCESS OUTGOING {id}");
        }

        private void ProcessIncomingConnection(ISession session)
        {
            if (_peerPool.TryGet(session.Node.Id, out Peer existingPeer))
            {
                // TODO: here the session.Node may not be equal peer.Node -> would be good to check if we can improve it
                session.Node.IsStatic = existingPeer.Node.IsStatic;
            }

            if (_logger.IsTrace) TraceProcessingIncoming();

            // if we have already initiated connection before
            if (_peerPool.ActivePeers.TryGetValue(session.RemoteNodeId, out Peer existingActivePeer))
            {
                AddSession(session, existingActivePeer);
                return;
            }

            if (!session.Node.IsStatic && ActivePeersCount >= MaxActivePeers + MaxActivePeerMargin)
            {
                if (_logger.IsTrace) TraceHardLimitDisconnect();
                session.InitiateDisconnect(DisconnectReason.HardLimitTooManyPeers, $"{ActivePeersCount}");
                return;
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

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceProcessingIncoming()
                => _logger.Trace($"INCOMING {session}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceHardLimitDisconnect()
                => _logger.Trace($"Initiating disconnect with {session} {DisconnectReason.HardLimitTooManyPeers} {DisconnectType.Local}");
        }

        private bool ShouldContactPeer(Peer peer)
            => _rlpxHost.ShouldContact(peer.Node.Address.Address, exactOnly: peer.Node.IsStatic || peer.Node.IsBootnode);

        /// <summary>
        /// Fast-path guard for the peer-added event: checks throttle before the IP filter
        /// so a throttled no-op does not consume a filter entry and block the peer for the full timeout window.
        /// </summary>
        private bool CanQuickConnect(Peer peer)
            => !_nodesBeingAdded.ContainsKey(peer.Node.Id)
               && !_outgoingConnectionRateLimiter.IsThrottled()
               && ShouldContactPeer(peer);

        private bool CanConnectToPeer(Peer peer)
        {
            if (_stats.FindCompatibilityValidationResult(peer.Node).HasValue)
            {
                if (_logger.IsTrace) TraceFailedCompatibilityConnection();
                return false;
            }

            (bool delayed, NodeStatsEventType? reason) = peer.Stats.IsConnectionDelayed(DateTime.UtcNow);
            if (delayed)
            {
                if (_logger.IsTrace) TraceConnectionDelay(reason);
                return false;
            }

            return true;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceFailedCompatibilityConnection()
                => _logger.Trace($"Not connecting peer: {peer} due to failed compatibility result");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceConnectionDelay(NodeStatsEventType? delayReason)
                => _logger.Trace($"Not connecting peer: {peer} due forced connection delay. Reason: {delayReason}");
        }

        private bool AddActivePeer(PublicKey nodeId, Peer peer, string reason)
        {
            peer.IsAwaitingConnection = false;
            bool added = _peerPool.ActivePeers.TryAdd(nodeId, peer);
            if (added)
            {
                Interlocked.Increment(ref Metrics.PeerCount);
                if (_logger.IsTrace) TraceActivePeer(isAdded: true);
            }
            else
            {
                if (_logger.IsTrace) TraceActivePeer(isAdded: false);
            }

            return added;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceActivePeer(bool isAdded)
                => _logger.Trace(isAdded
                    ? $"|NetworkTrace| {peer.Node:s} added to active peers - {reason}"
                    : $"|NetworkTrace| {peer.Node:s} already in active peers");
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
            if (_logger.IsTrace) TraceDeactivatingPeer();
            if (!IsConnected(peer) && !peer.IsAwaitingConnection)
            {
                // dropping references to sessions so they can be garbage collected
                peer.InSession = null;
                peer.OutSession = null;
                RemoveActivePeer(peer.Node.Id, reason);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceDeactivatingPeer()
                => _logger.Trace($"DEACTIVATING IF DISCONNECTED {peer}");
        }

        private string GetIncompatibleDesc(IReadOnlyCollection<Peer> incompatibleNodes)
        {
            if (incompatibleNodes.Count == 0)
            {
                return "0";
            }

            IGrouping<CompatibilityValidationType?, Peer>[] validationGroups = incompatibleNodes.GroupBy(x => _stats.FindCompatibilityValidationResult(x.Node)).ToArray();
            return $"[{string.Join(", ", validationGroups.Select(x => $"{x.Key}:{x.Count()}"))}]";
        }

        private ConnectionDirection ChooseDirectionToKeep(PublicKey remoteNode)
        {
            if (_logger.IsTrace) TraceChoosingDirection();
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

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceChoosingDirection()
                => _logger.Trace($"CHOOSING DIRECTION {remoteNode}");
        }

        private void AddSession(ISession session, Peer peer)
        {
            if (_logger.IsTrace) TraceAddingSession();
            ConnectionDirection sessionDirection = session.Direction;
            bool newSessionIsIn = sessionDirection == ConnectionDirection.In;

            if (CanAttachSessionDirectly(session, peer))
            {
                AttachSession(peer, session, sessionDirection, disconnectOpposite: false);
            }
            else
            {
                ResolveSessionConflict(session, peer, sessionDirection);
            }

            AddActivePeer(peer.Node.Id, peer, newSessionIsIn ? "new IN session" : "new OUT session");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceAddingSession()
                => _logger.Trace($"ADDING {session} {peer}");
        }

        private bool CanAttachSessionDirectly(ISession session, Peer peer)
            => !IsConnected(peer) || (peer.IsAwaitingConnection && session.Direction == ConnectionDirection.Out);

        private void AttachSession(Peer peer, ISession session, ConnectionDirection sessionDirection, bool disconnectOpposite)
        {
            if (sessionDirection == ConnectionDirection.In)
            {
                peer.Stats.AddNodeStatsHandshakeEvent(ConnectionDirection.In);
                peer.InSession = session;
            }
            else
            {
                peer.OutSession = session;
            }

            if (disconnectOpposite)
            {
                GetSession(peer, GetOppositeDirection(sessionDirection))?.InitiateDisconnect(DisconnectReason.OppositeDirectionCleanup, "same");
            }
        }

        private void ResolveSessionConflict(ISession session, Peer peer, ConnectionDirection sessionDirection)
        {
            bool peerHasAnOpenSameDirectionSession = HasOpenSession(GetSession(peer, sessionDirection));
            bool peerHasAnOpenOppositeDirectionSession = HasOpenSession(GetSession(peer, GetOppositeDirection(sessionDirection)));

            if (peerHasAnOpenSameDirectionSession)
            {
                if (_logger.IsDebug) DebugSessionConflict(session, SessionConflictLogEvent.AlreadyConnected);
                session.InitiateDisconnect(DisconnectReason.SessionAlreadyExist, "same");
                return;
            }

            if (peerHasAnOpenOppositeDirectionSession)
            {
                ResolveOppositeDirectionSessionConflict(session, peer, sessionDirection);
            }
        }

        private static bool HasOpenSession(ISession? session)
            => !(session?.IsClosing ?? true);

        private void ResolveOppositeDirectionSessionConflict(ISession session, Peer peer, ConnectionDirection sessionDirection)
        {
            ConnectionDirection directionToKeep = ChooseDirectionToKeep(session.RemoteNodeId);
            if (session.Direction != directionToKeep)
            {
                if (_logger.IsDebug) DebugSessionConflict(session, SessionConflictLogEvent.NewSessionAlreadyConnected, directionToKeep);
                session.InitiateDisconnect(DisconnectReason.ReplacingSessionWithOppositeDirection, "same");
                AttachSession(peer, session, sessionDirection, disconnectOpposite: false);
                return;
            }

            if (_logger.IsDebug) DebugSessionConflict(session, SessionConflictLogEvent.ExistingSessionReplacing, directionToKeep);
            AttachSession(peer, session, sessionDirection, disconnectOpposite: true);
        }

        private static ConnectionDirection GetOppositeDirection(ConnectionDirection sessionDirection)
            => sessionDirection == ConnectionDirection.In ? ConnectionDirection.Out : ConnectionDirection.In;

        private static ISession? GetSession(Peer peer, ConnectionDirection sessionDirection)
            => sessionDirection == ConnectionDirection.In ? peer.InSession : peer.OutSession;

        private static bool IsConnected(Peer peer)
            => HasOpenSession(peer.InSession) || HasOpenSession(peer.OutSession);

        private void OnDisconnected(object sender, DisconnectEventArgs e)
        {
            ISession session = (ISession)sender;
            lock (_sessionLock)
            {
                ToggleSessionEventListeners(session, false);
                _sessions.TryRemove(session.SessionId, out _);
                if (_isStopping)
                {
                    return;
                }

                if (_logger.IsTrace) TraceSessionLifecycle(session, SessionLifecycleTraceEvent.Closing);

                if (session.State != SessionState.Disconnected)
                {
                    ThrowInvalidOnDisconnectedState(session);
                }

                if (_logger.IsTrace) TracePeerDisconnected();

                if (session.RemoteNodeId is null)
                {
                    // this happens when we have a disconnect on incoming connection before handshake
                    if (_logger.IsTrace) TraceDisconnectWithoutRemoteNodeId();
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
                        if (_logger.IsTrace) TraceIgnoringDifferentSessionDisconnect(activePeer.Node.Id);
                        return;
                    }

                    DeactivatePeerIfDisconnected(activePeer, "session disconnected");
                    SignalPeerUpdateNeeded();
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TracePeerDisconnected()
                => _logger.Trace($"|NetworkTrace| peer disconnected event in PeerManager - {session} {e.DisconnectReason} {e.DisconnectType}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceDisconnectWithoutRemoteNodeId()
                => _logger.Trace($"Disconnect on session with no RemoteNodeId - {session}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceIgnoringDifferentSessionDisconnect(PublicKey nodeId)
                => _logger.Trace($"Received disconnect on a different session than the active peer runs. Ignoring. Id: {nodeId}");
        }

        private void ToggleSessionEventListeners(ISession session, bool shouldListen)
        {
            if (shouldListen)
            {
                session.HandshakeComplete += _onHandshakeComplete;
                session.Disconnected += _onDisconnected;
            }
            else
            {
                session.HandshakeComplete -= _onHandshakeComplete;
                session.Disconnected -= _onDisconnected;
            }
        }

        private void OnHandshakeComplete(object sender, EventArgs args)
        {
            ISession session = (ISession)sender;
            lock (_sessionLock)
            {
                if (_isStopping)
                {
                    return;
                }

                _stats.GetOrAdd(session.Node);

                //In case of OUT connections and different RemoteNodeId we need to replace existing Active Peer with new peer
                ManageNewRemoteNodeId(session);

                if (_logger.IsTrace) TraceSessionLifecycle(session, SessionLifecycleTraceEvent.HandshakeCompleted);

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
                        if (_logger.IsTrace) TraceHandshakeWithoutActivePeer();
                        return;
                    }

                    peer.Stats.AddNodeStatsHandshakeEvent(ConnectionDirection.Out);
                }

                if (_logger.IsTrace) TraceSessionLifecycle(session, SessionLifecycleTraceEvent.HandshakeInitialized);

                [MethodImpl(MethodImplOptions.NoInlining)]
                void TraceHandshakeWithoutActivePeer()
                    => _logger.Trace($"Initiated handshake (OUT) with a peer without adding it to the Active collection : {session}");
            }
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
            if (_logger.IsTrace) TraceRemoteNodeIdUpdated();

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceRemoteNodeIdUpdated()
                => _logger.Trace($"RemoteNodeId was updated due to handshake difference, old: {session.ObsoleteRemoteNodeId}, new: {session.RemoteNodeId}, new peer not present in candidate collection");
        }

        private enum SessionLifecycleTraceEvent
        {
            Created,
            Closing,
            HandshakeCompleted,
            HandshakeInitialized
        }

        private enum SessionConflictLogEvent
        {
            AlreadyConnected,
            NewSessionAlreadyConnected,
            ExistingSessionReplacing
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceSessionLifecycle(ISession session, SessionLifecycleTraceEvent traceEvent)
        {
            switch (traceEvent)
            {
                case SessionLifecycleTraceEvent.Created:
                    _logger.Trace($"|NetworkTrace| {session} created in peer manager");
                    return;
                case SessionLifecycleTraceEvent.Closing:
                    _logger.Trace($"|NetworkTrace| {session} closing");
                    return;
                case SessionLifecycleTraceEvent.HandshakeCompleted:
                    _logger.Trace($"|NetworkTrace| {session} completed handshake - peer manager handling");
                    return;
                case SessionLifecycleTraceEvent.HandshakeInitialized:
                    _logger.Trace($"|NetworkTrace| {session} handshake initialized in peer manager");
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(traceEvent), traceEvent, null);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DebugSessionConflict(ISession session, SessionConflictLogEvent logEvent, ConnectionDirection directionToKeep = default)
        {
            switch (logEvent)
            {
                case SessionConflictLogEvent.AlreadyConnected:
                    _logger.Debug($"Disconnecting a {session} - already connected");
                    return;
                case SessionConflictLogEvent.NewSessionAlreadyConnected:
                    _logger.Debug($"Disconnecting a new {session} - {directionToKeep} session already connected");
                    return;
                case SessionConflictLogEvent.ExistingSessionReplacing:
                    _logger.Debug($"Disconnecting an existing {session} - {directionToKeep} session to replace");
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(logEvent), logEvent, null);
            }
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowInvalidOnDisconnectedState(ISession session)
            => throw new InvalidAsynchronousStateException($"Invalid session state in {nameof(OnDisconnected)} - {session.State}");
    }
}
