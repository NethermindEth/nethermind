// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    /// <summary>
    /// </summary>
    public class PeerManager : IPeerManager
    {
        private readonly ILogger _logger;
        private readonly INetworkConfig _networkConfig;
        private readonly IRlpxHost _rlpxHost;
        private readonly IEnode _enode;
        private readonly INodeStatsManager _stats;
        private readonly SemaphoreSlim _peerUpdateRequested = new(0, 1);
        private readonly ConditionalWeakTable<Peer, StrongBox<long>> _contactRetryAfter = [];
        private readonly TimeProvider _timeProvider;
        private long _lastDialableCandidateAt;
        private bool _candidateSelectionCompleted;
        private bool _hasDialableCandidate;
        private readonly Dictionary<Guid, (long Order, ISession Session)> _inboundAdmissionOrder = [];
        private readonly Dictionary<Guid, (Peer Peer, ISession Session)> _pendingCapacityDisconnects = [];
        private long _nextInboundAdmissionOrder;
        private readonly Dictionary<IPAddress, int> _inboundCandidatesPerIp = [];
        private readonly Dictionary<PublicKeyAsKey, (Peer Peer, IPAddress Address)> _inboundCandidates = [];
        private Task? _peerUpdateLoopTask;
        private Task? _peerUpdateTimerTask;
        private readonly IPeerPool _peerPool;
        private readonly Lock _sessionLock = new();
        private readonly List<PeerStats> _candidates;
        private readonly RateLimiter _outgoingConnectionRateLimiter;

        private int _pending;
        private int _tryCount;
        private int _newActiveNodes;
        private int _failedInitialConnect;
        private int _connectionRounds;

        private int _maxPeerPoolLength;

        private volatile bool _isStarted;
        private int _logCounter = 1;
        private bool _isStopping; // guarded by _sessionLock

        private readonly CancellationTokenSource _cancellationTokenSource = new();
        // Admission state and the pending count are guarded by _sessionLock.
        private readonly Dictionary<ISession, AdmissionState> _sessions = [with(ReferenceEqualityComparer.Instance)];
        private int _incomingSessionsAwaitingHello;
        private readonly HashSet<Guid> _authenticatedSessions = [];
        internal const int MaxInboundCandidatesPerIp = 16;
        private readonly int _outgoingConnectParallelism;
        private readonly EventHandler<EventArgs> _onHandshakeComplete;
        private readonly SessionDisconnectedEventHandler _onSessionDisconnected;

        public PeerManager(
            IRlpxHost rlpxHost,
            IPeerPool peerPool,
            INodeStatsManager stats,
            INetworkConfig networkConfig,
            IEnode enode,
            ILogManager logManager)
            : this(rlpxHost, peerPool, stats, networkConfig, enode, logManager, TimeProvider.System)
        {
        }

        internal PeerManager(
            IRlpxHost rlpxHost,
            IPeerPool peerPool,
            INodeStatsManager stats,
            INetworkConfig networkConfig,
            IEnode enode,
            ILogManager logManager,
            TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
            _logger = logManager.GetClassLogger<PeerManager>();
            _rlpxHost = rlpxHost;
            _enode = enode;
            _stats = stats;
            _networkConfig = networkConfig;
            _onHandshakeComplete = OnHandshakeComplete;
            _onSessionDisconnected = OnDisconnected;
            _outgoingConnectParallelism = networkConfig.NumConcurrentOutgoingConnects;
            if (_outgoingConnectParallelism == 0)
            {
                _outgoingConnectParallelism = Environment.ProcessorCount;
            }
            _outgoingConnectionRateLimiter = new RateLimiter(networkConfig.MaxOutgoingConnectPerSec);

            _peerPool = peerPool;
            _candidates = [with(networkConfig.MaxActivePeers * 2)];
        }

        public IReadOnlyCollection<Peer> ActivePeers => _peerPool.ActivePeers.Select(static kvp => kvp.Value).ToList();
        public IReadOnlyCollection<Peer> CandidatePeers => _peerPool.Peers.Select(static kvp => kvp.Value).ToList();
        public IReadOnlyCollection<Peer> ConnectedPeers => _peerPool.ActivePeers.Select(static kvp => kvp.Value).Where(IsConnected).ToList();

        public int MaxActivePeers => _networkConfig.MaxActivePeers;
        public int ActivePeersCount => _peerPool.ActivePeerCount;
        public int ConnectedPeersCount => _peerPool.ActivePeers.Count(static kvp => IsConnected(kvp.Value));
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
            if (peer.IsInboundCandidate)
            {
                lock (_sessionLock) TrackInboundCandidate(peer);
            }
            int newPeerPoolLength = _peerPool.PeerCount;
            Metrics.PeerCandidateCount = newPeerPoolLength;
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
            if (HasAvailableActivePeerSlot() && CanConnectToPeer(peer))
            {
#pragma warning disable 4014

                // TODO: hack related to not clearly separated peer pool and peer manager
                if (!_nodesBeingAdded.ContainsKey(peer.Node.Id))
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
            lock (_sessionLock)
            {
                if (_inboundCandidates.TryGetValue(e.Peer.Node.Id, out (Peer Peer, IPAddress Address) candidate)
                    && ReferenceEquals(candidate.Peer, e.Peer))
                    RemoveInboundCandidate(e.Peer.Node.Id, candidate.Address);
                lock (e.Peer.SessionLock)
                {
                    if (IsConnected(e.Peer)) return;
                    e.Peer.IsAwaitingConnection = false;
                    if (_peerPool.ActivePeers.TryGetValue(e.Peer.Node.Id, out Peer? active) && ReferenceEquals(active, e.Peer) &&
                        _peerPool.ActivePeers.TryRemove(e.Peer.Node.Id, out _))
                        Interlocked.Decrement(ref Metrics.PeerCount);
                }
            }
        }

        private void TrackInboundCandidate(Peer peer)
        {
            if (!peer.IsInboundCandidate) return;
            if (_inboundCandidates.TryGetValue(peer.Node.Id, out (Peer Peer, IPAddress Address) existing))
            {
                if (ReferenceEquals(existing.Peer, peer)) return;
                RemoveInboundCandidate(peer.Node.Id, existing.Address);
            }

            IPAddress address = peer.Node.Address.Address.NormalizeMappedIPv4();
            _inboundCandidates.Add(peer.Node.Id, (peer, address));
            _inboundCandidatesPerIp.TryGetValue(address, out int count);
            _inboundCandidatesPerIp[address] = count + 1;
        }

        private void RemoveInboundCandidate(PublicKeyAsKey id, IPAddress address)
        {
            _inboundCandidates.Remove(id);
            int remaining = _inboundCandidatesPerIp[address] - 1;
            if (remaining == 0) _inboundCandidatesPerIp.Remove(address);
            else _inboundCandidatesPerIp[address] = remaining;
        }

        public void Start()
        {
            int peersUpdateInterval = _networkConfig.PeersUpdateInterval;
            if (peersUpdateInterval is <= 0)
            {
                ThrowInvalidPeersUpdateInterval(peersUpdateInterval);
            }

            lock (_sessionLock)
            {
                _isStopping = false;
                _peerPool.PeerAdded += PeerPoolOnPeerAdded;
                _peerPool.PeerRemoved += PeerPoolOnPeerRemoved;
                _inboundCandidates.Clear();
                _inboundCandidatesPerIp.Clear();
                foreach ((_, Peer peer) in _peerPool.Peers) TrackInboundCandidate(peer);
                _rlpxHost.SessionCreated += RlpxHostOnSessionCreated;
                _rlpxHost.SessionDisconnected += _onSessionDisconnected;
            }

            _peerUpdateTimerTask = RunPeerUpdateTimerAsync(peersUpdateInterval);
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
                _rlpxHost.SessionDisconnected -= _onSessionDisconnected;
                foreach (ISession session in _sessions.Keys)
                {
                    ToggleSessionEventListeners(session, false);
                }

                _sessions.Clear();
                foreach ((long _, ISession session) in _inboundAdmissionOrder.Values)
                    session.Disconnected -= OnInboundAdmissionEnded;
                _inboundAdmissionOrder.Clear();
                _pendingCapacityDisconnects.Clear();
                _inboundCandidates.Clear();
                _inboundCandidatesPerIp.Clear();
                _incomingSessionsAwaitingHello = 0;
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

            if (_peerUpdateTimerTask is not null)
            {
                await _peerUpdateTimerTask;
            }

            foreach ((PublicKeyAsKey id, Peer peer) in _peerPool.ActivePeers)
                if (!_peerPool.Peers.TryGetValue(id, out Peer? pooled) || !ReferenceEquals(pooled, peer)) peer.ReleaseStats();
            if (_logger.IsInfo) _logger.Info("Peer Manager shutdown complete. Please wait for all components to close");
        }

        string IStoppableService.Description => "peer manager";

        private class CandidateSelection
        {
            public List<Peer> PreCandidates { get; } = [];
            public List<Peer> Candidates { get; } = [];
            public List<Peer> Incompatible { get; } = [];
            public Dictionary<string, int> Counters { get; } = [];
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
                        await SetupOutgoingPeerConnection(peer);
                    }
                    catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
                    {
                        if (_logger.IsDebug) DebugConnectWorker(idx, isCancelled: true);
                        break;
                    }
                    catch (Exception e)
                    {
                        // This is strictly speaking not related to the connection, but something outside of it.
                        if (_logger.IsError) _logger.Error($"Error setting up connection to {peer:hide}, {e:hide}");
                    }
                }
                if (_logger.IsDebug) DebugConnectWorker(idx, isCancelled: false);
            }).ToPooledList(_outgoingConnectParallelism);

            int loopCount = 0;
            int consecutiveFailures = 0;
            long previousActivePeersCount = 0;
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

                    if (!_isStarted || !HasAvailableActivePeerSlot() && !HasExcessOrdinaryInbound())
                    {
                        continue;
                    }

                    ResetConnectionRoundCounters();

                    List<Peer>? remainingCandidates = await GetRemainingCandidates();
                    if (remainingCandidates is null)
                    {
                        consecutiveFailures = 0;
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

                    RequestAnotherPeerUpdateIfNeeded();
                    consecutiveFailures = 0;

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
                    consecutiveFailures = Math.Min(consecutiveFailures + 1, 7);
                    if (consecutiveFailures == 1)
                    {
                        if (_logger.IsError) _logger.Error("Peer update loop failure", e);
                    }
                    else
                    {
                        if (_logger.IsDebug) _logger.Debug($"Peer update loop failure: {e}");
                    }
                    try
                    {
                        int delaySeconds = Math.Min(1 << (consecutiveFailures - 1), 60);
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _timeProvider, _cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
                    {
                        break;
                    }
                }
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
                _logger.Trace($"{nl}{nl}All active peers: {nl} {string.Join(nl, _peerPool.ActivePeers.Select(static kvp => kvp.Value).Select(x => $"{x.Node:s} | P2P: {(x.Stats ?? _stats.GetStatsOrNull(x.Node))?.DidEventHappen(NodeStatsEventType.P2PInitialized)} | Eth62: {(x.Stats ?? _stats.GetStatsOrNull(x.Node))?.DidEventHappen(NodeStatsEventType.Eth62Initialized)} | {(x.Stats ?? _stats.GetStatsOrNull(x.Node))?.P2PNodeDetails?.ClientId} | {x.Stats ?? _stats.GetStatsOrNull(x.Node)}")):hide} {nl}{nl}");
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
                _logger.DebugError("Candidate peers cleanup failed", e);
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
            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, _cancellationTokenSource.Token);
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
                if (!HasAvailableActivePeerSlot()
                    && (HasExcessOrdinaryInbound() || !await EnsureAvailableActivePeerSlotAsync()))
                {
                    break;
                }

                await taskChannel.Writer.WriteAsync(peer, _cancellationTokenSource.Token);
            }
        }

        private void RequestAnotherPeerUpdateIfNeeded()
        {
            if (HasAvailableActivePeerSlot())
            {
                SignalPeerUpdateNeeded();
            }
        }

        private bool HasAvailableActivePeerSlot() => AvailableActivePeersCount - _pending > 0;

        /// <summary>
        /// Reserves one of the <see cref="MaxActivePeers"/> slots for an outgoing connection attempt.
        /// </summary>
        /// <remarks>
        /// Claims first and gives the claim back when there turns out to be no room, so that concurrent
        /// callers cannot all pass <see cref="HasAvailableActivePeerSlot"/> for the same slot and then
        /// dial once they resume. Static and trusted peers keep their claim either way: they are must-keep
        /// and exempt from the cap everywhere else, and once the cap is full nothing re-selects them, so a
        /// refusal here would drop them for good. The claim is released in
        /// <see cref="SetupOutgoingPeerConnection"/>.
        /// </remarks>
        private bool TryClaimActivePeerSlot(Peer peer)
        {
            if (Interlocked.Increment(ref _pending) <= AvailableActivePeersCount
                || peer.Node.IsStatic
                || peer.Node.IsTrusted)
            {
                return true;
            }

            Interlocked.Decrement(ref _pending);
            return false;
        }

        private async Task<bool> EnsureAvailableActivePeerSlotAsync()
        {
            if (HasAvailableActivePeerSlot()) return true;

            long started = _timeProvider.GetTimestamp();
            TimeSpan timeout = Timeouts.Handshake + TimeSpan.FromMilliseconds(_networkConfig.ConnectTimeoutMs);
            while (_timeProvider.GetElapsedTime(started) < timeout
                   && !HasAvailableActivePeerSlot()
                   && !HasExcessOrdinaryInbound())
            {
                await _peerUpdateRequested.WaitAsync(TimeSpan.FromMilliseconds(100), _cancellationTokenSource.Token);
            }

            return HasAvailableActivePeerSlot();
        }

        private void SelectAndRankCandidates()
        {
            _currentSelection.PreCandidates.Clear();
            _currentSelection.Candidates.Clear();
            _currentSelection.Incompatible.Clear();
            _currentSelection.Counters.Clear();
            if (!HasAvailableActivePeerSlot() && !HasExcessOrdinaryInbound()) return;

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

                if (IsSelf(peer))
                {
                    continue;
                }

                _currentSelection.PreCandidates.Add(peer);
            }

            long now = _timeProvider.GetTimestamp();
            bool hasOnlyStaticNodes = false;
            if (_currentSelection.PreCandidates.Count == 0)
            {
                _currentSelection.Candidates.AddRange(_peerPool.StaticPeers.Where(sn => sn.Stats is not null && !IsSelf(sn) && !IsCandidateContactRetryDelayed(sn, now) && !_peerPool.ActivePeers.ContainsKey(sn.Node.Id) && !_rlpxHost.IsEndpointRejected(sn.Node)));
                hasOnlyStaticNodes = _currentSelection.Candidates.Count > 0;
            }

            if (_currentSelection.PreCandidates.Count == 0 && !hasOnlyStaticNodes)
            {
                RecordCandidateAvailability(now);
                RecordCandidateFilterMetrics();
                return;
            }

            DateTime nowUTC = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (Peer preCandidate in _currentSelection.PreCandidates)
            {
                if (IsCandidateContactRetryDelayed(preCandidate, now)) continue;

                if (preCandidate.Node.Port == 0)
                {
                    _currentSelection.Counters.Increment(ActivePeerSelectionCounter.FilteredByZeroPort.ToString());
                    continue;
                }

                if (preCandidate.Stats is not { } candidateStats) continue;
                (bool Result, NodeStatsEventType? DelayReason) delayResult = candidateStats.IsConnectionDelayed(nowUTC);
                if (delayResult.Result)
                {
                    _currentSelection.Counters.Increment(delayResult.DelayReason.ToString());

                    continue;
                }

                if (candidateStats.FailedCompatibilityValidation.HasValue)
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

                if (_rlpxHost.IsEndpointRejected(preCandidate.Node)) continue;
                // Static peers are added once, together with the other static peers, below
                if (preCandidate.Node.IsStatic)
                {
                    continue;
                }

                _currentSelection.Candidates.Add(preCandidate);
            }

            if (!hasOnlyStaticNodes)
            {
                _currentSelection.Candidates.AddRange(_peerPool.StaticPeers.Where(sn => sn.Stats is not null && !IsSelf(sn) && !IsCandidateContactRetryDelayed(sn, now) && !_peerPool.ActivePeers.ContainsKey(sn.Node.Id) && !_rlpxHost.IsEndpointRejected(sn.Node)));
            }

            foreach (Peer peer in _currentSelection.Candidates)
            {
                Node? node = peer.Node;

                if (node is null) continue;

                node.CurrentReputation = peer.Stats.CurrentNodeReputation(nowUTC);
            }

            CollectionsMarshal.AsSpan(_currentSelection.Candidates).Sort(default(PeerComparer));

            RecordCandidateAvailability(now);
            RecordCandidateFilterMetrics();
        }

        private void RecordCandidateAvailability(long now)
        {
            bool hasCandidate = false;
            foreach (Peer candidate in _currentSelection.Candidates)
            {
                if (!IsDialableCandidate(candidate, now)) continue;
                hasCandidate = true;
                break;
            }
            lock (_sessionLock)
            {
                if (!_candidateSelectionCompleted && !hasCandidate) return;
                if (!_candidateSelectionCompleted || hasCandidate || _hasDialableCandidate)
                    _lastDialableCandidateAt = now;
                _hasDialableCandidate = hasCandidate;
                _candidateSelectionCompleted = true;
            }
        }

        private ISession? SelectNewestExcessInbound()
        {
            ISession? newest = null;
            int inboundCount = 0;
            long newestOrder = long.MinValue;
            foreach ((_, Peer peer) in _peerPool.ActivePeers)
            {
                lock (peer.SessionLock)
                {
                    if (!IsOrdinaryInboundPeer(peer) || IsPendingCapacityDisconnect(peer, peer.InSession)) continue;
                    inboundCount++;
                    ISession session = peer.InSession!;
                    if (_inboundAdmissionOrder.TryGetValue(session.SessionId, out (long Order, ISession Session) admission) && admission.Order > newestOrder)
                    {
                        newestOrder = admission.Order;
                        newest = session;
                    }
                }
            }

            return inboundCount > MaxActivePeers - (MaxActivePeers + 2) / 3 ? newest : null;
        }

        private bool IsPendingCapacityDisconnect(Peer peer, ISession? session)
            => session is not null &&
               _pendingCapacityDisconnects.TryGetValue(session.SessionId, out (Peer Peer, ISession Session) pending) &&
               ReferenceEquals(pending.Peer, peer) && ReferenceEquals(pending.Session, session);

        private void PrunePendingCapacityDisconnects()
        {
            foreach ((Guid id, (Peer Peer, ISession Session) pending) in _pendingCapacityDisconnects)
            {
                lock (pending.Peer.SessionLock)
                {
                    if (!_peerPool.ActivePeers.TryGetValue(pending.Peer.Node.Id, out Peer? owner) || !ReferenceEquals(owner, pending.Peer) || owner.Node.IsStatic || owner.Node.IsTrusted ||
                        !ReferenceEquals(owner.InSession, pending.Session) && !ReferenceEquals(owner.OutSession, pending.Session))
                        _pendingCapacityDisconnects.Remove(id);
                }
            }
        }

        private int GetProjectedActivePeerCount()
        {
            int projected = ActivePeersCount;
            foreach ((_, Peer peer) in _peerPool.ActivePeers)
            {
                lock (peer.SessionLock)
                {
                    bool pendingInbound = IsPendingCapacityDisconnect(peer, peer.InSession);
                    bool pendingOutbound = IsPendingCapacityDisconnect(peer, peer.OutSession);
                    if (!peer.IsAwaitingConnection && (pendingInbound || pendingOutbound) &&
                        (!HasOpenSession(peer.InSession) || pendingInbound) && (!HasOpenSession(peer.OutSession) || pendingOutbound))
                        projected--;
                }
            }
            return projected;
        }

        private ISession? SelectCapacityVictim(ISession session)
        {
            if (session.Direction == ConnectionDirection.Out)
            {
                foreach ((_, Peer peer) in _peerPool.ActivePeers)
                {
                    lock (peer.SessionLock)
                    {
                        if (IsOrdinaryInboundPeer(peer) && !IsPendingCapacityDisconnect(peer, peer.InSession) &&
                            !HasOpenSession(peer.OutSession) && !peer.IsAwaitingConnection)
                            return peer.InSession;
                    }
                }
            }
            return session;
        }

        private void DisconnectCapacityVictim(ISession session)
        {
            try
            {
                session.InitiateDisconnect(DisconnectReason.TooManyPeers, "restoring peer capacity");
            }
            finally
            {
                lock (_sessionLock)
                {
                    if (!session.IsClosing && !session.IsChannelClosed)
                        _pendingCapacityDisconnects.Remove(session.SessionId);
                    PrunePendingCapacityDisconnects();
                }
            }
        }

        private bool HasExcessOrdinaryInbound()
        {
            lock (_sessionLock)
            {
                int remaining = MaxActivePeers - (MaxActivePeers + 2) / 3;
                foreach ((_, Peer peer) in _peerPool.ActivePeers)
                    if (IsOrdinaryInboundPeer(peer) && --remaining < 0) return true;
                return false;
            }
        }

        private bool IsCandidateContactRetryDelayed(Peer peer, long now)
        {
            if (!IsContactRetryDelayed(peer, now)) return false;
            _currentSelection.Counters.Increment(nameof(ActivePeerSelectionCounter.FilteredByContactRetryDelay));
            return true;
        }

        private void RecordCandidateFilterMetrics()
        {
            foreach (KeyValuePair<string, int> currentSelectionCounter in _currentSelection.Counters)
            {
                Metrics.PeerCandidateFilter.AddBy(
                    currentSelectionCounter.Key,
                    currentSelectionCounter.Value);
            }
        }

        private async Task RunPeerUpdateTimerAsync(int peersUpdateInterval)
        {
            if (_logger.IsDebug) _logger.Debug("Starting peer update timer");

            try
            {
                using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(peersUpdateInterval));
                while (await timer.WaitForNextTickAsync(_cancellationTokenSource.Token))
                {
                    SignalPeerUpdateNeeded();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (_logger.IsError) _logger.Error("Peer update timer encountered an exception.", e);
            }
            catch (OperationCanceledException)
            {
                if (_logger.IsDebug) _logger.Debug("Peer update timer stopped.");
            }
        }

        private void RlpxHostOnSessionCreated(object? sender, SessionEventArgs args)
        {
            ISession session = args.Session;
            bool isOutgoing;
            lock (_sessionLock)
            {
                if (_isStopping || session.IsClosing)
                {
                    return;
                }

                isOutgoing = session.Direction == ConnectionDirection.Out;
                if (!_sessions.TryAdd(session, isOutgoing ? AdmissionState.Accepted : AdmissionState.AwaitingHandshake)) return;
                ToggleSessionEventListeners(session, true);

                if (_logger.IsTrace) TraceSessionLifecycle(session, SessionLifecycleTraceEvent.Created);
            }

            // ProcessOutgoingConnection may call MarkDisconnected/InitiateDisconnect,
            // which fires Disconnected → OnDisconnected that also acquires _sessionLock.
            // Must be called outside the lock to avoid deadlock.
            if (isOutgoing)
            {
                ProcessOutgoingConnection(session);
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
                    if (!peer.Node.IsStatic && !peer.Node.IsTrusted)
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

                CollectionsMarshal.AsSpan(_candidates).Sort(default(PeerStatsComparer));

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
            Incompatible,
            FilteredByContactRetryDelay
        }

        private readonly struct PeerStats(Peer peer, bool failedValidation, long currentReputation)
        {
            public Peer Peer { get; } = peer;
            public bool FailedValidation { get; } = failedValidation;
            public long CurrentReputation { get; } = currentReputation;
        }

        private readonly struct PeerStatsComparer : IComparer<PeerStats>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Compare(PeerStats x, PeerStats y)
            {
                int failedValidationCompare = y.FailedValidation.CompareTo(x.FailedValidation);
                return failedValidationCompare != 0
                    ? failedValidationCompare
                    : x.CurrentReputation.CompareTo(y.CurrentReputation);
            }
        }

        private async Task SetupOutgoingPeerConnection(Peer peer, bool cancelIfThrottled = false)
        {
            if (IsContactRetryDelayed(peer, _timeProvider.GetTimestamp())) return;
            if (cancelIfThrottled && _outgoingConnectionRateLimiter.IsThrottled()) return;
            if (_rlpxHost.IsEndpointRejected(peer.Node)) return;

            // Claim the slot before the first await: a caller suspended in the rate limiter is invisible
            // to a plain capacity check, so without the claim all of them dial past MaxActivePeers.
            if (!TryClaimActivePeerSlot(peer))
            {
                // The candidate was selected and queued, so say it went nowhere - otherwise a slot freed
                // in the meantime idles until the next PeersUpdateInterval.
                SignalPeerUpdateNeeded();
                return;
            }

            bool result = false;
            try
            {
                await _outgoingConnectionRateLimiter.WaitAsync(_cancellationTokenSource.Token);
                if (_rlpxHost.IsEndpointRejected(peer.Node)) return;

                // Records the address in the recent-contact filter, so it has to run under the claim:
                // a candidate refused a slot would otherwise stay suppressed for the whole filter
                // window without ever having been dialed.
                if (!ShouldContactPeer(peer))
                {
                    StrongBox<long> retryAfter = _contactRetryAfter.GetValue(peer, static _ => new());
                    Volatile.Write(ref retryAfter.Value, _timeProvider.GetTimestamp() + _timeProvider.TimestampFrequency);
                    return;
                }

                // Can happen when In connection is received from the same peer and is initialized before we get here
                // In this case we do not initialize OUT connection
                if (!TryActivateCandidate(peer))
                {
                    if (_logger.IsTrace) TraceActivePeerAlreadyAddedToCollection();
                    return;
                }

                Interlocked.Increment(ref _tryCount);
                result = await InitializeOutgoingPeerConnection(peer);
                // for some time we will have a peer in active that has no session assigned - analyze this?
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
                SignalPeerUpdateNeeded();
            }

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
                => _logger.Trace($"Active peer was already added to collection: {peer.Node.Id:hide}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceOutgoingConnectionResult()
                => _logger.Trace($"Connecting to {_stats.GetCurrentReputation(peer.Node)} rep node - {result}, ACTIVE: {_peerPool.ActivePeerCount}, CAND: {_peerPool.PeerCount}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceTimeoutDisconnect()
                => _logger.Trace($"Timeout, doing additional disconnect: {peer.Node.Id:hide}");
        }

        internal bool TryActivateCandidate(Peer candidate)
        {
            lock (candidate.SessionLock)
            {
                if (candidate.RemovedFromPool || !AddActivePeer(candidate.Node.Id, candidate, "upgrading candidate")) return false;
                candidate.IsAwaitingConnection = true;
                return true;
            }
        }

        private async Task<bool> InitializeOutgoingPeerConnection(Peer candidate)
        {
            try
            {
                if (_logger.IsTrace) TraceConnectingToCandidate();
                _stats.ReportEvent(candidate.Node, NodeStatsEventType.Connecting);
                return await _rlpxHost.ConnectAsync(candidate.Node, _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                throw;
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
                _logger.DebugError($"Error trying to initiate connection with peer: {candidate.Node.ToString("s"):hide}", SensitiveLogMasking.Enabled ? null : ex);
                return false;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceConnectingToCandidate()
                => _logger.Trace($"CONNECTING TO {candidate:hide}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCannotConnectToPeer(NetworkExceptionType networkExceptionType)
                => _logger.Trace($"Cannot connect to peer [{networkExceptionType}]: {candidate.Node.ToString("s"):hide}");
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
                => _logger.Trace($"PROCESS OUTGOING {id:hide}");
        }

        public void OnP2PProtocolInitialized(ISession session)
        {
            ISession? victim = null;
            lock (_sessionLock)
            {
                if (_isStopping || !HasOpenSession(session)) return;
                if (session.Direction == ConnectionDirection.In)
                {
                    // A session that connected before Start was never tracked; it is admitted on its Hello as before.
                    if (_sessions.TryGetValue(session, out AdmissionState state))
                    {
                        if (state != AdmissionState.AwaitingHello) return;
                        _incomingSessionsAwaitingHello--;
                    }
                    _sessions[session] = AdmissionState.Accepted;
                    CopyPrivilegedFlags(session);
                }
                if (!_authenticatedSessions.Add(session.SessionId)) return;
                if (session.Direction == ConnectionDirection.In)
                {
                    ProcessIncomingConnection(session);
                }
                if (!HasOpenSession(session) || !_peerPool.ActivePeers.TryGetValue(session.Node.Id, out Peer? owner) ||
                    !ReferenceEquals(GetSession(owner, session.Direction), session)) return;
                if (session.Node.IsStatic || session.Node.IsTrusted) return;

                PrunePendingCapacityDisconnects();
                if (session.Direction == ConnectionDirection.Out)
                    victim = SelectNewestExcessInbound();
                if (victim is null && GetProjectedActivePeerCount() > MaxActivePeers)
                    victim = SelectCapacityVictim(session);
                if (victim is not null && _peerPool.ActivePeers.TryGetValue(victim.Node.Id, out Peer? victimOwner))
                {
                    lock (victimOwner.SessionLock)
                    {
                        if (!ReferenceEquals(GetSession(victimOwner, victim.Direction), victim) ||
                            !HasOpenSession(victim) || IsPendingCapacityDisconnect(victimOwner, victim)) return;
                        _pendingCapacityDisconnects.Add(victim.SessionId, (victimOwner, victim));
                    }
                }
                else victim = null;
            }
            if (victim is not null) DisconnectCapacityVictim(victim);
        }

        public void OnHelloReceived(ISession session)
        {
            if (session.Direction != ConnectionDirection.In) return;
            lock (_sessionLock)
            {
                if (!_sessions.TryGetValue(session, out AdmissionState state) || state != AdmissionState.AwaitingHello) return;
                CopyPrivilegedFlags(session);
            }
        }

        // Only after the Hello: it arrives over frames keyed to the claimed identity, so the remote holds that key.
        // A key claimed in the AUTH alone must not grant the disconnect exemptions of static and trusted peers.
        private void CopyPrivilegedFlags(ISession session)
        {
            if (_peerPool.TryGet(session.Node.Id, out Peer? existingPeer))
            {
                session.Node.IsStatic = existingPeer.Node.IsStatic;
                session.Node.IsTrusted = existingPeer.Node.IsTrusted;
            }
        }

        private void ProcessIncomingConnection(ISession session)
        {
            _peerPool.TryGet(session.Node.Id, out Peer? existingPeer);

            if (_logger.IsTrace) TraceProcessingIncoming();

            IDisposable? lease = null;
            INodeStats? stats = existingPeer?.Stats;
            if (stats is null && !_stats.TryAcquire(session.Node, true, out stats, out lease))
                stats = _stats.GetStatsOrNull(session.Node) ?? new NodeStatsLight(session.Node);
            using IDisposable? admission = lease;
            if (stats.FailedCompatibilityValidation.HasValue ||
                (!session.Node.IsStatic && !session.Node.IsTrusted && _stats.IsInboundBanned(session.Node, _timeProvider.GetUtcNow().UtcDateTime)))
            {
                session.MarkDisconnected(DisconnectReason.UselessPeer, DisconnectType.Local, "inbound identity is not eligible for reconnection");
                return;
            }

            // if we have already initiated connection before
            if (_peerPool.ActivePeers.TryGetValue(session.RemoteNodeId, out Peer existingActivePeer))
            {
                AddSession(session, existingActivePeer);
                return;
            }

            bool candidateQuotaReached = false;
            if (existingPeer is null && session.Node.Port != 0 && !session.Node.IsStatic && !session.Node.IsTrusted)
            {
                IPAddress address = session.Node.Address.Address.NormalizeMappedIPv4();
                _inboundCandidatesPerIp.TryGetValue(address, out int candidatesAtAddress);
                candidateQuotaReached = candidatesAtAddress >= MaxInboundCandidatesPerIp;
            }

            if (!session.Node.IsStatic && !session.Node.IsTrusted)
            {
                int inboundCount = 0;
                foreach ((PublicKeyAsKey _, Peer activePeer) in _peerPool.ActivePeers)
                {
                    if (IsOrdinaryInboundPeer(activePeer))
                        inboundCount++;
                }

                int maxInboundPeers = GetMaxInboundPeers();
                if (inboundCount >= maxInboundPeers)
                {
                    session.InitiateDisconnect(DisconnectReason.TooManyPeers, $"inbound limit {maxInboundPeers}");
                    return;
                }
            }

            if (!session.Node.IsStatic && !session.Node.IsTrusted && ActivePeersCount >= MaxActivePeers + MaxActivePeerMargin)
            {
                if (_logger.IsTrace) TraceHardLimitDisconnect();
                session.InitiateDisconnect(DisconnectReason.HardLimitTooManyPeers, $"{ActivePeersCount}");
                return;
            }

            try
            {
                _nodesBeingAdded.TryAdd(session.RemoteNodeId, null);
                Peer peer;
                if (existingPeer is not null) peer = existingPeer;
                else if (session.Node.Port == 0 || candidateQuotaReached)
                {
                    _stats.TryAcquire(session.Node, true, out INodeStats? activeStats, out IDisposable? activeLease);
                    peer = new Peer(session.Node, activeStats ?? stats, activeLease, _stats);
                }
                else
                {
                    if (!_peerPool.TryGetOrAdd(session.Node, out Peer? admitted, inboundCandidate: true))
                    {
                        _stats.TryAcquire(session.Node, true, out INodeStats? activeStats, out IDisposable? activeLease);
                        peer = new Peer(session.Node, activeStats ?? stats, activeLease, _stats);
                    }
                    else peer = admitted;
                }
                try
                {
                    AddSession(session, peer);
                    if (ReferenceEquals(peer.InSession, session) && HasOpenSession(session)) TrackInboundCandidate(peer);
                }
                finally
                {
                    if ((!_peerPool.Peers.TryGetValue(peer.Node.Id, out Peer? pooled) || !ReferenceEquals(pooled, peer)) &&
                        (!_peerPool.ActivePeers.TryGetValue(peer.Node.Id, out Peer? active) || !ReferenceEquals(active, peer)))
                        peer.ReleaseStats();
                }
            }
            finally
            {
                _nodesBeingAdded.TryRemove(session.RemoteNodeId, out _);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceProcessingIncoming()
                => _logger.Trace($"INCOMING {session:hide}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceHardLimitDisconnect()
                => _logger.Trace($"Initiating disconnect with {session:hide} {DisconnectReason.HardLimitTooManyPeers} {DisconnectType.Local}");
        }

        private static bool IsOrdinaryInboundPeer(Peer peer)
            => !peer.Node.IsStatic && !peer.Node.IsTrusted && HasOpenSession(peer.InSession);

        private int GetMaxInboundPeers()
        {
            long now = _timeProvider.GetTimestamp();
            int reserved = (MaxActivePeers + 2) / 3;
            if (_candidateSelectionCompleted && !_hasDialableCandidate
                && _timeProvider.GetElapsedTime(_lastDialableCandidateAt, now) >= TimeSpan.FromMinutes(5))
                reserved = Math.Min(reserved, 2);
            return MaxActivePeers - reserved;
        }

        private bool IsDialableCandidate(Peer candidate, long now)
            => candidate.Node.Port is > 0 and <= 65535 && !IsSelf(candidate) && !candidate.IsAwaitingConnection &&
               !IsConnected(candidate) && !_peerPool.ActivePeers.ContainsKey(candidate.Node.Id) &&
               !IsContactRetryDelayed(candidate, now) && candidate.Stats is not null && !candidate.Stats.FailedCompatibilityValidation.HasValue &&
               !candidate.Stats.IsConnectionDelayed(_timeProvider.GetUtcNow().UtcDateTime).Result && !_rlpxHost.IsEndpointRejected(candidate.Node);

        private bool ShouldContactPeer(Peer peer)
            => !IsSelf(peer)
               && _rlpxHost.ShouldContact(peer.Node.Address.Address, exactOnly: peer.Node.IsStatic || peer.Node.IsBootnode);

        private bool IsContactRetryDelayed(Peer peer, long now)
            => _contactRetryAfter.TryGetValue(peer, out StrongBox<long>? retryAfter)
               && now < Volatile.Read(ref retryAfter.Value);

        private bool IsSelf(Peer peer) => peer.Node.Id == _enode.PublicKey;

        private bool CanConnectToPeer(Peer peer)
        {
            if (_stats.FindCompatibilityValidationResult(peer.Node).HasValue)
            {
                if (_logger.IsTrace) TraceFailedCompatibilityConnection();
                return false;
            }

            if (peer.Stats is not { } stats) return false;
            (bool delayed, NodeStatsEventType? reason) = stats.IsConnectionDelayed(_timeProvider.GetUtcNow().UtcDateTime);
            if (delayed)
            {
                if (_logger.IsTrace) TraceConnectionDelay(reason);
                return false;
            }

            return true;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceFailedCompatibilityConnection()
                => _logger.Trace($"Not connecting peer: {peer:hide} due to failed compatibility result");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceConnectionDelay(NodeStatsEventType? delayReason)
                => _logger.Trace($"Not connecting peer: {peer:hide} due forced connection delay. Reason: {delayReason}");
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
            {
                if (isAdded)
                    _logger.Trace($"|NetworkTrace| {peer.Node.ToString("s"):hide} added to active peers - {reason:hide}");
                else
                    _logger.Trace($"|NetworkTrace| {peer.Node.ToString("s"):hide} already in active peers");
            }
        }

        private void RemoveActivePeer(PublicKey nodeId, string reason)
        {
            bool removed = _peerPool.ActivePeers.TryRemove(nodeId, out Peer? removedPeer);
            if (removed)
            {
                if (!_peerPool.Peers.TryGetValue(nodeId, out Peer? pooled) || !ReferenceEquals(pooled, removedPeer)) removedPeer.ReleaseStats();
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
                => _logger.Trace($"DEACTIVATING IF DISCONNECTED {peer:hide}");
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
            byte[] localKey = _enode.PublicKey.Bytes;
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
                => _logger.Trace($"CHOOSING DIRECTION {remoteNode:hide}");
        }

        private void AddSession(ISession session, Peer peer)
        {
            if (_logger.IsTrace) TraceAddingSession();
            ConnectionDirection sessionDirection = session.Direction;
            bool newSessionIsIn = sessionDirection == ConnectionDirection.In;

            // Decided and attached as one step, or an IN and an OUT session added on different threads could both
            // find the other direction empty and both attach directly. The disconnect waits until the lock is
            // released: its handlers take the session lock, which the IN path holds while it waits for this one.
            (ISession Session, DisconnectReason Reason)? disconnect;
            lock (peer.SessionLock)
            {
                if (peer.RemovedFromPool)
                {
                    disconnect = (session, DisconnectReason.PeerRemoved);
                }
                else
                {
                    disconnect = CanAttachSessionDirectly(session, peer)
                        ? AttachSession(peer, session, sessionDirection, disconnectOpposite: false)
                        : ResolveSessionConflict(session, peer, sessionDirection);
                    AddActivePeer(peer.Node.Id, peer, newSessionIsIn ? "new IN session" : "new OUT session");
                }
            }

            if (disconnect is { } closing) closing.Session.InitiateDisconnect(closing.Reason, closing.Reason == DisconnectReason.PeerRemoved ? "candidate removed" : "same");
            if (newSessionIsIn && ReferenceEquals(peer.InSession, session) && HasOpenSession(session))
            {
                lock (_sessionLock)
                {
                    if (_inboundAdmissionOrder.TryAdd(session.SessionId, (_nextInboundAdmissionOrder++, session)))
                        session.Disconnected += OnInboundAdmissionEnded;
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceAddingSession()
                => _logger.Trace($"ADDING {session:hide} {peer:hide}");
        }

        // An IN session attached while the dial was in flight still has to go through the conflict resolution,
        // or both directions stay open.
        private bool CanAttachSessionDirectly(ISession session, Peer peer)
            => !IsConnected(peer) || (peer.IsAwaitingConnection && session.Direction == ConnectionDirection.Out && !HasOpenSession(peer.InSession));

        private static (ISession Session, DisconnectReason Reason)? AttachSession(Peer peer, ISession session, ConnectionDirection sessionDirection, bool disconnectOpposite)
        {
            if (sessionDirection == ConnectionDirection.In)
            {
                peer.Stats?.AddNodeStatsHandshakeEvent(ConnectionDirection.In);
                peer.InSession = session;
            }
            else
            {
                peer.OutSession = session;
            }

            return disconnectOpposite && GetSession(peer, GetOppositeDirection(sessionDirection)) is { } opposite
                ? (opposite, DisconnectReason.OppositeDirectionCleanup)
                : null;
        }

        private (ISession Session, DisconnectReason Reason)? ResolveSessionConflict(ISession session, Peer peer, ConnectionDirection sessionDirection)
        {
            bool peerHasAnOpenSameDirectionSession = HasOpenSession(GetSession(peer, sessionDirection));
            bool peerHasAnOpenOppositeDirectionSession = HasOpenSession(GetSession(peer, GetOppositeDirection(sessionDirection)));

            if (peerHasAnOpenSameDirectionSession)
            {
                if (_logger.IsDebug) DebugSessionConflict(session, SessionConflictLogEvent.AlreadyConnected);
                return (session, DisconnectReason.SessionAlreadyExist);
            }

            return peerHasAnOpenOppositeDirectionSession
                ? ResolveOppositeDirectionSessionConflict(session, peer, sessionDirection)
                : null;
        }

        private static bool HasOpenSession(ISession? session)
            => session is not null
               && !session.IsClosing
               && !session.IsChannelClosed;

        private (ISession Session, DisconnectReason Reason)? ResolveOppositeDirectionSessionConflict(ISession session, Peer peer, ConnectionDirection sessionDirection)
        {
            ConnectionDirection directionToKeep = ChooseDirectionToKeep(session.RemoteNodeId);
            if (session.Direction != directionToKeep)
            {
                if (_logger.IsDebug) DebugSessionConflict(session, SessionConflictLogEvent.NewSessionAlreadyConnected, directionToKeep);
                AttachSession(peer, session, sessionDirection, disconnectOpposite: false);
                return (session, DisconnectReason.ReplacingSessionWithOppositeDirection);
            }

            if (_logger.IsDebug) DebugSessionConflict(session, SessionConflictLogEvent.ExistingSessionReplacing, directionToKeep);
            return AttachSession(peer, session, sessionDirection, disconnectOpposite: true);
        }

        private static ConnectionDirection GetOppositeDirection(ConnectionDirection sessionDirection)
            => sessionDirection == ConnectionDirection.In ? ConnectionDirection.Out : ConnectionDirection.In;

        private static ISession? GetSession(Peer peer, ConnectionDirection sessionDirection)
            => sessionDirection == ConnectionDirection.In ? peer.InSession : peer.OutSession;

        private static bool IsConnected(Peer peer)
            => HasOpenSession(peer.InSession) || HasOpenSession(peer.OutSession);

        private void OnInboundAdmissionEnded(object? sender, DisconnectEventArgs e)
        {
            ISession session = (ISession)sender!;
            session.Disconnected -= OnInboundAdmissionEnded;
            lock (_sessionLock) _inboundAdmissionOrder.Remove(session.SessionId);
        }

        private void OnDisconnected(object sender, ISession session, DisconnectEventArgs e)
        {
            lock (_sessionLock)
            {
                try
                {
                    ToggleSessionEventListeners(session, false);
                    _inboundAdmissionOrder.Remove(session.SessionId);
                    bool authenticatedInbound = _authenticatedSessions.Remove(session.SessionId);
                    if (!_sessions.Remove(session, out AdmissionState state)) return;
                    if (state == AdmissionState.AwaitingHello) _incomingSessionsAwaitingHello--;
                    if (state != AdmissionState.Accepted) return;
                    if (session.Direction == ConnectionDirection.In && !authenticatedInbound) return;
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

                    if (_peerPool.ActivePeers.TryGetValue(session.RemoteNodeId, out Peer activePeer))
                    {
                        if (activePeer.InSession?.SessionId != session.SessionId && activePeer.OutSession?.SessionId != session.SessionId)
                        {
                            if (_logger.IsTrace) TraceIgnoringDifferentSessionDisconnect(activePeer.Node.Id);
                            return;
                        }

                        if (session.Direction == ConnectionDirection.Out) activePeer.IsAwaitingConnection = false;
                        _stats.ReportDisconnect(session.Node, e.DisconnectType, e.DisconnectReason);
                        DeactivatePeerIfDisconnected(activePeer, "session disconnected");
                        if (!IsConnected(activePeer) && !activePeer.IsAwaitingConnection &&
                            !activePeer.Node.IsStatic && !activePeer.Node.IsTrusted &&
                            (activePeer.Node.Port == 0 || _stats.HasFailedValidation(activePeer.Node)))
                            _peerPool.TryRemove(activePeer.Node.Id, out _);
                        SignalPeerUpdateNeeded();
                    }
                }
                finally
                {
                    _pendingCapacityDisconnects.Remove(session.SessionId);
                    PrunePendingCapacityDisconnects();
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TracePeerDisconnected()
                => _logger.Trace($"|NetworkTrace| peer disconnected event in PeerManager - {session:hide} {e.DisconnectReason} {e.DisconnectType}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceDisconnectWithoutRemoteNodeId()
                => _logger.Trace($"Disconnect on session with no RemoteNodeId - {session:hide}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceIgnoringDifferentSessionDisconnect(PublicKey nodeId)
                => _logger.Trace($"Received disconnect on a different session than the active peer runs. Ignoring. Id: {nodeId:hide}");
        }

        private void ToggleSessionEventListeners(ISession session, bool shouldListen)
        {
            if (shouldListen)
            {
                session.HandshakeComplete += _onHandshakeComplete;
            }
            else
            {
                session.HandshakeComplete -= _onHandshakeComplete;
            }
        }

        private void OnHandshakeComplete(object sender, EventArgs args)
        {
            ISession session = (ISession)sender;
            lock (_sessionLock)
            {
                if (!_sessions.TryGetValue(session, out AdmissionState state))
                {
                    return;
                }

                if (_logger.IsTrace) TraceSessionLifecycle(session, SessionLifecycleTraceEvent.HandshakeCompleted);

                if (session.Direction == ConnectionDirection.In)
                {
                    if (state != AdmissionState.AwaitingHandshake) return;

                    // AUTH names a key but does not prove possession until an authenticated frame arrives.
                    if (_peerPool.ActivePeers.TryGetValue(session.RemoteNodeId, out Peer activePeer) &&
                        (IsAuthenticatedOpenSession(activePeer.InSession) || IsAuthenticatedOpenSession(activePeer.OutSession)))
                    {
                        session.MarkDisconnected(DisconnectReason.SessionAlreadyExist, DisconnectType.Local, "unauthenticated identity conflict");
                        return;
                    }

                    bool isPrivileged = _peerPool.TryGet(session.Node.Id, out Peer? existingPeer)
                        && (existingPeer.Node.IsStatic || existingPeer.Node.IsTrusted);

                    // A privileged id may exceed the cap with one pending session, so false claims of it stay bounded.
                    // The session itself gets the flags only once its Hello arrives (OnHelloReceived), as the AUTH alone does not prove the key.
                    if (_incomingSessionsAwaitingHello >= (long)MaxActivePeers + MaxActivePeerMargin
                        && (!isPrivileged || IsAwaitingHello(session.Node.Id)))
                    {
                        session.InitiateDisconnect(DisconnectReason.HardLimitTooManyPeers, "pending inbound handshakes");
                        return;
                    }

                    _incomingSessionsAwaitingHello++;
                    _sessions[session] = AdmissionState.AwaitingHello;

                    return;
                }

                //In case of OUT connections and different RemoteNodeId we need to replace existing Active Peer with new peer
                ManageNewRemoteNodeId(session);

                if (!_peerPool.ActivePeers.TryGetValue(session.RemoteNodeId, out Peer peer))
                {
                    //Can happen when peer sent Disconnect message before handshake is done, it takes us a while to disconnect
                    if (_logger.IsTrace) TraceHandshakeWithoutActivePeer();
                    return;
                }

                peer.Stats?.AddNodeStatsHandshakeEvent(ConnectionDirection.Out);

                if (_logger.IsTrace) TraceSessionLifecycle(session, SessionLifecycleTraceEvent.HandshakeInitialized);

                [MethodImpl(MethodImplOptions.NoInlining)]
                void TraceHandshakeWithoutActivePeer()
                    => _logger.Trace($"Initiated handshake (OUT) with a peer without adding it to the Active collection : {session:hide}");
            }
        }

        private bool IsAuthenticatedOpenSession(ISession? session)
            => HasOpenSession(session) && _authenticatedSessions.Contains(session!.SessionId);

        private bool IsAwaitingHello(PublicKey nodeId)
        {
            foreach ((ISession session, AdmissionState state) in _sessions)
            {
                if (state == AdmissionState.AwaitingHello && session.Node.Id == nodeId) return true;
            }

            return false;
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
                => _logger.Trace($"RemoteNodeId was updated due to handshake difference, old: {session.ObsoleteRemoteNodeId:hide}, new: {session.RemoteNodeId:hide}, new peer not present in candidate collection");
        }

        // An incoming AUTH claims an identity. Only the authenticated Hello establishes it for peer accounting.
        private enum AdmissionState
        {
            AwaitingHandshake,
            AwaitingHello,
            Accepted
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
                    _logger.Trace($"|NetworkTrace| {session:hide} created in peer manager");
                    return;
                case SessionLifecycleTraceEvent.Closing:
                    _logger.Trace($"|NetworkTrace| {session:hide} closing");
                    return;
                case SessionLifecycleTraceEvent.HandshakeCompleted:
                    _logger.Trace($"|NetworkTrace| {session:hide} completed handshake - peer manager handling");
                    return;
                case SessionLifecycleTraceEvent.HandshakeInitialized:
                    _logger.Trace($"|NetworkTrace| {session:hide} handshake initialized in peer manager");
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
                    _logger.Debug($"Disconnecting a {session:hide} - already connected");
                    return;
                case SessionConflictLogEvent.NewSessionAlreadyConnected:
                    _logger.Debug($"Disconnecting a new {session:hide} - {directionToKeep} session already connected");
                    return;
                case SessionConflictLogEvent.ExistingSessionReplacing:
                    _logger.Debug($"Disconnecting an existing {session:hide} - {directionToKeep} session to replace");
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(logEvent), logEvent, null);
            }
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowInvalidOnDisconnectedState(ISession session)
            => throw new InvalidAsynchronousStateException($"Invalid session state in {nameof(OnDisconnected)} - {session.State}");

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowInvalidPeersUpdateInterval(int peersUpdateInterval)
            => throw new InvalidConfigurationException(
                $"{nameof(INetworkConfig.PeersUpdateInterval)} must be greater than zero, but was {peersUpdateInterval}.",
                ExitCodes.ForbiddenOptionValue);
    }
}
