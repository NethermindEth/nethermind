// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Timer = System.Timers.Timer;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Synchronization.Peers
{
    /// <summary>
    ///     Eth sync peer pool is capable of returning a sync peer allocation that is best suited for the requesting
    ///     sync process. It also manages all allocations allowing to replace peers with better peers whenever they connect.
    /// </summary>
    public class SyncPeerPool : ISyncPeerPool, IPeerDifficultyRefreshPool
    {
        public const int DefaultUpgradeIntervalInMs = 1000;
        public const int MaxAllocationSlots = byte.MaxValue;

        private readonly IBlockTree _blockTree;
        private readonly ulong _configuredPivotNumber;
        internal TimeProvider RefreshTimeProvider { get; init; } = TimeProvider.System;
        private readonly ILogger _logger;
        private readonly Lock _refreshLock = new();

        private readonly ConcurrentDictionary<PublicKey, PeerInfo> _peers = new();
        private readonly AllocationAllowances _allocationAllowances;

        private readonly Dictionary<PublicKey, RefreshState> _refreshes = [];

        private readonly INodeStatsManager _stats;
        private readonly IBetterPeerStrategy _betterPeerStrategy;
        private readonly int _allocationsUpgradeIntervalInMs;

        private bool _isStarted;
        private readonly Lock _isAllocatedChecks = new();

        private DateTime _lastUselessPeersDropTime = DateTime.UtcNow;

        private readonly CancellationTokenSource _refreshLoopCancellation = new();

        private volatile TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TimeSpan _timeBeforeWakingShallowSleepingPeerUp = TimeSpan.FromMilliseconds(DefaultUpgradeIntervalInMs);
        private Timer? _upgradeTimer;

        public SyncPeerPool(IBlockTree blockTree,
            INodeStatsManager nodeStatsManager,
            IBetterPeerStrategy betterPeerStrategy,
            INetworkConfig networkConfig,
            ISyncConfig syncConfig,
            ILogManager logManager)
        : this(blockTree, nodeStatsManager, betterPeerStrategy, logManager, networkConfig.MaxActivePeers, networkConfig.PriorityPeersMaxCount, syncConfig.AllocationSlots)
        => _configuredPivotNumber = syncConfig.PivotNumber;

        // Internal so Autofac auto-wire only sees the public ctor above. The int defaults here would otherwise
        // win Autofac's "greediest resolvable constructor" race over the INetworkConfig/ISyncConfig overload.
        // Tests reach this via [assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")].
        internal SyncPeerPool(IBlockTree blockTree,
            INodeStatsManager nodeStatsManager,
            IBetterPeerStrategy betterPeerStrategy,
            ILogManager logManager,
            int peersMaxCount = 100,
            int priorityPeerMaxCount = 0,
            int allocationSlots = 1,
            int allocationsUpgradeIntervalInMsInMs = DefaultUpgradeIntervalInMs)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _betterPeerStrategy = betterPeerStrategy ?? throw new ArgumentNullException(nameof(betterPeerStrategy));
            PeerMaxCount = peersMaxCount;
            PriorityPeerMaxCount = priorityPeerMaxCount;
            _allocationsUpgradeIntervalInMs = allocationsUpgradeIntervalInMsInMs;
            _logger = logManager.GetClassLogger<SyncPeerPool>();

            // The packed AllocationAllowances representation reserves 1 byte (8 bits) per context but only honours
            // values up to MaxAllocationSlots; anything higher is clamped at registration time.
            byte slots = (byte)Math.Clamp(allocationSlots, 1, MaxAllocationSlots);
            // Headers reliably hang when given a high allowance, so they remain pinned at 1
            _allocationAllowances = new AllocationAllowances(headers: 1, bodies: slots, receipts: slots, state: slots, snap: slots, forwardHeader: slots, blockAccessLists: slots);

            if (_logger.IsDebug) _logger.Debug($"PeerMaxCount: {PeerMaxCount}, PriorityPeerMaxCount: {PriorityPeerMaxCount}, AllocationSlots: {slots}");
        }

        public void ReportNoSyncProgress(PeerInfo peerInfo, AllocationContexts allocationContexts)
        {
            peerInfo.RecordNoProgress(allocationContexts);
            ReportWeakPeer(peerInfo, allocationContexts, transient: true);
        }

        public void ReportBreachOfProtocol(PeerInfo? peerInfo, DisconnectReason disconnectReason, string details)
        {
            /* since the allocations can have the peers dynamically changed
             * it may be hard for the external classes to ensure that the peerInfo is not null at the time when they report
             * so we decide to check for null here and not consider the scenario to be exceptional
             */
            if (peerInfo is not null)
            {
                _stats.ReportSyncEvent(peerInfo.SyncPeer.Node, NodeStatsEventType.SyncFailed);
                peerInfo.SyncPeer.Disconnect(disconnectReason, details);
            }
        }

        public void ReportWeakPeer(PeerInfo? weakPeer, AllocationContexts allocationContexts) =>
            ReportWeakPeer(weakPeer, allocationContexts, transient: false);

        private static void ReportWeakPeer(PeerInfo? weakPeer, AllocationContexts allocationContexts, bool transient)
        {
            if (weakPeer is null)
            {
                /* it may have just got disconnected and in such case the allocation would be nullified
                 * in such case there is no need to talk about whether the peer is good or bad
                 */
                return;
            }

            AllocationContexts sleeps = weakPeer.IncreaseWeakness(allocationContexts, transient);
            if (sleeps != AllocationContexts.None)
            {
                weakPeer.PutToSleep(sleeps, DateTime.UtcNow);
            }
        }

        public async Task<int?> EstimateRequestLimit(RequestType requestType, IPeerAllocationStrategy allocationStrategy, AllocationContexts context, CancellationToken token)
        {
            // So, to know which peer is next, we just try to allocate it, and then dispose it.
            using SyncPeerAllocation syncPeerAllocation = await Allocate(allocationStrategy, context, 1000, token);
            if (!syncPeerAllocation.HasPeer) return null;

            return _stats.GetStatsOrNull(syncPeerAllocation.Current!.SyncPeer.Node)?.GetCurrentRequestLimit(requestType);
        }

        public void Start()
        {
            _isStarted = true;
            StartUpgradeTimer();
        }

        public PeerInfo? GetPeer(Node node) => _peers.GetValueOrDefault(node.Id);
        public event EventHandler<PeerBlockNotificationEventArgs>? NotifyPeerBlock;
        public event EventHandler<PeerHeadRefreshedEventArgs>? PeerRefreshed;

        public void WakeUpAll()
        {
            foreach (KeyValuePair<PublicKey, PeerInfo> peer in _peers)
            {
                peer.Value.TryToWakeUp(DateTime.UtcNow, TimeSpan.Zero);
            }
        }

        public IEnumerable<PeerInfo> AllPeers
        {
            get
            {
                foreach ((_, PeerInfo peerInfo) in _peers) yield return peerInfo;
            }
        }

        /// <summary>
        /// Peers eligible for worst-peer eviction. Static and trusted peers are excluded: they are
        /// operator-configured must-keep peers (static is actively redialed by the peer manager, trusted
        /// bypasses the peer limit), so evicting them here only causes disconnect/redial churn.
        /// </summary>
        public IEnumerable<PeerInfo> DroppablePeers
        {
            get
            {
                foreach ((_, PeerInfo peerInfo) in _peers)
                {
                    if (!peerInfo.SyncPeer.Node.IsStatic && !peerInfo.SyncPeer.Node.IsTrusted)
                    {
                        yield return peerInfo;
                    }
                }
            }
        }

        public IEnumerable<PeerInfo> InitializedPeers
        {
            get
            {
                foreach ((_, PeerInfo peerInfo) in _peers)
                {
                    if (!peerInfo.SyncPeer.IsInitialized)
                    {
                        continue;
                    }

                    yield return peerInfo;
                }
            }
        }

        public int PeerCount => _peers.Count;
        public int PriorityPeerCount;
        public int InitializedPeersCount => InitializedPeers.Count();
        public int PeerMaxCount { get; }
        private int PriorityPeerMaxCount { get; }

        public void RefreshTotalDifficulty(ISyncPeer syncPeer, Hash256 blockHash)
        {
            RefreshTotalDiffTask task = new(blockHash, syncPeer);
            QueueRefresh(task);
        }

        public void AddPeer(ISyncPeer syncPeer)
        {
            if (_logger.IsTrace) TraceAddingPeer(syncPeer);
            if (!_isStarted)
            {
                if (_logger.IsDebug) _logger.Debug($"Sync peer pool not started yet - adding peer is blocked: {syncPeer.Node.ToString("s"):hide}");
                return;
            }

            if (_peers.ContainsKey(syncPeer.Node.Id))
            {
                if (_logger.IsDebug) _logger.Debug($"Sync peer {syncPeer.Node?.ToString("c"):hide} already in peers collection.");
                return;
            }

            PeerInfo peerInfo = new(syncPeer, _allocationAllowances, RefreshTimeProvider, _blockTree, _configuredPivotNumber);
            if (!_peers.TryAdd(syncPeer.Node.Id, peerInfo))
            {
                return;
            }

            UpdatePeerCountMetric(peerInfo.PeerClientType, 1);

            if (syncPeer.IsPriority)
            {
                Interlocked.Increment(ref PriorityPeerCount);
                Metrics.PriorityPeers = PriorityPeerCount;
            }
            if (_logger.IsTrace) TracePeerCount();

            Hash256 expectedHash = syncPeer.HeadHash;
            ulong expectedNumber = syncPeer.HeadNumber;
            BlockHeader? header = _blockTree.FindHeader(expectedHash, BlockTreeLookupOptions.None);
            if (header is not null)
            {
                if (syncPeer.TrySetHead(expectedHash, expectedNumber, expectedHash, header.Number))
                    syncPeer.TotalDifficulty = header.TotalDifficulty ?? syncPeer.TotalDifficulty;
                syncPeer.IsInitialized = true;
            }
            else
            {
                if (_logger.IsTrace) TraceAddingToRefreshQueue(syncPeer);
                if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportInterestingEvent(syncPeer.Node.Address, "adding node to refresh queue");
                QueueRefresh(new RefreshTotalDiffTask(syncPeer));
            }
        }

        public void RemovePeer(ISyncPeer syncPeer)
        {
            if (_logger.IsTrace) TraceRemovingPeer(syncPeer);

            if (!_isStarted)
            {
                if (_logger.IsDebug) _logger.Debug($"Sync peer pool not started yet - removing {syncPeer.Node?.ToString("c"):hide} is blocked.");
                return;
            }

            PublicKey id = syncPeer.Node.Id;
            if (id is null)
            {
                if (_logger.IsDebug) _logger.Debug("Peer ID was null when removing peer");
                return;
            }

            if (!_peers.TryGetValue(id, out PeerInfo? removedPeer) || !ReferenceEquals(removedPeer.SyncPeer, syncPeer)
                || !_peers.TryRemove(new KeyValuePair<PublicKey, PeerInfo>(id, removedPeer)))
            {
                // possible if sync failed - we remove peer and eventually initiate disconnect, which calls remove peer again
                return;
            }

            UpdatePeerCountMetric(syncPeer.ClientType, -1);

            if (syncPeer.IsPriority)
            {
                Interlocked.Decrement(ref PriorityPeerCount);
                Metrics.PriorityPeers = PriorityPeerCount;
            }
            if (_logger.IsTrace) TracePeerCount();

            CancellationTokenSource? cancellation = null;
            lock (_refreshLock)
            {
                if (_refreshes.TryGetValue(id, out RefreshState? refresh))
                {
                    if (ReferenceEquals(refresh.Pending?.SyncPeer, syncPeer)) refresh.Pending = null;
                    if (ReferenceEquals(refresh.CurrentPeer, syncPeer)) cancellation = refresh.Cancellation;
                }
            }
            try { cancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // Replaces only the cancellation target; the refresh worker retains ownership of the
        // original source and disposes it. Used to reproduce the cancel-versus-dispose race.
        internal bool TryReplaceRefreshCancellation(PublicKey id, CancellationTokenSource replacement)
        {
            lock (_refreshLock)
            {
                if (!_refreshes.TryGetValue(id, out RefreshState? refresh) || refresh.Cancellation is null) return false;
                refresh.Cancellation = replacement;
                return true;
            }
        }

        internal bool RefreshCancellationIs(PublicKey id, CancellationTokenSource expected)
        {
            lock (_refreshLock)
                return _refreshes.TryGetValue(id, out RefreshState? refresh) && ReferenceEquals(refresh.Cancellation, expected);
        }

        public void SetPeerPriority(PublicKey id)
        {
            if (_peers.TryGetValue(id, out PeerInfo peerInfo) && !peerInfo.SyncPeer.IsPriority)
            {
                peerInfo.SyncPeer.IsPriority = true;
                Interlocked.Increment(ref PriorityPeerCount);
            }
        }

        // Cap for the allocation retry backoff. A shallow-sleep wake-up is time-based and never fires
        // _signal, so this retry delay is the only path that notices it, and AllocateAndRun passes an
        // unbounded budget that would otherwise let the backoff grow without limit.
        private const int MaxAllocationWaitTimeMs = 1000;

        internal static int GetAllocationWaitTime(int tryCount) => (int)Math.Min(10L * tryCount, MaxAllocationWaitTimeMs);

        public async Task<SyncPeerAllocation> Allocate(
            IPeerAllocationStrategy peerAllocationStrategy,
            AllocationContexts allocationContexts = AllocationContexts.All,
            int timeoutMilliseconds = 0,
            CancellationToken cancellationToken = default)
        {
            SyncPeerAllocation allocation = new(allocationContexts, _isAllocatedChecks, SignalPeersChanged);
            if (cancellationToken.IsCancellationRequested)
            {
                return allocation;
            }

            int tryCount = 1;
            long timeStamp = Stopwatch.GetTimestamp();

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _refreshLoopCancellation.Token);

            while (true)
            {
                // Snapshot the signal task before attempting allocation so that any
                // signal fired during or after TryAllocateOnce is already on this task.
                Task signal = _signal.Task;

                if (TryAllocateOnce(peerAllocationStrategy, allocationContexts, allocation))
                {
                    return allocation;
                }

                long elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(timeStamp).TotalMilliseconds;
                bool timeoutReached = timeoutMilliseconds == 0
                                      || elapsedMilliseconds < 0
                                      || elapsedMilliseconds > timeoutMilliseconds;
                if (timeoutReached) return allocation;

                int waitTime = GetAllocationWaitTime(tryCount++);
                waitTime = Math.Min(waitTime, timeoutMilliseconds - (int)elapsedMilliseconds);

                if (waitTime > 0)
                {
                    await Task.WhenAny(signal, Task.Delay(waitTime, cts.Token));
                    if (cts.IsCancellationRequested)
                    {
                        return allocation;
                    }
                }
            }
        }

        private bool TryAllocateOnce(IPeerAllocationStrategy peerAllocationStrategy, AllocationContexts allocationContexts, SyncPeerAllocation allocation)
        {
            lock (_isAllocatedChecks)
            {
                PeerInfo? selected = peerAllocationStrategy
                    .Allocate(allocation.Current, InitializedPeers.Where(p => p.CanBeAllocated(allocationContexts)),
                    _stats,
                    _blockTree);

                allocation.AllocatePeer(selected);
                return allocation.HasPeer;
            }
        }

        private void QueueRefresh(RefreshTotalDiffTask task)
        {
            PublicKey id = task.SyncPeer.Node.Id;
            lock (_refreshLock)
            {
                if (!_isStarted || !IsCurrentPeer(task.SyncPeer)) return;
                if (_refreshes.TryGetValue(id, out RefreshState? existing))
                {
                    existing.Pending = task;
                    return;
                }
                RefreshState state = new() { Pending = task };
                _refreshes.Add(id, state);
                state.Work = Task.Run(() => RunPeerRefresh(id, state));
            }
        }

        private bool IsCurrentPeer(ISyncPeer peer) =>
            _peers.TryGetValue(peer.Node.Id, out PeerInfo? current) && ReferenceEquals(current.SyncPeer, peer);

        private async Task RunPeerRefresh(PublicKey id, RefreshState state)
        {
            while (true)
            {
                RefreshTotalDiffTask refreshTask;
                CancellationTokenSource cancellation;
                lock (_refreshLock)
                {
                    if (!_isStarted || state.Pending is not { } pending || !IsCurrentPeer(pending.SyncPeer))
                    {
                        _refreshes.Remove(id);
                        return;
                    }
                    refreshTask = pending;
                    state.Pending = null;
                    state.CurrentPeer = pending.SyncPeer;
                    cancellation = CancellationTokenSource.CreateLinkedTokenSource(_refreshLoopCancellation.Token);
                    state.Cancellation = cancellation;
                }
                ISyncPeer syncPeer = refreshTask.SyncPeer;
                try
                {
                    await ExecuteRefreshTask(refreshTask, cancellation.Token);
                    if (_logger.IsTrace) TraceRefreshedPeerInfo(syncPeer);
                    if (!IsCurrentPeer(syncPeer) || cancellation.IsCancellationRequested) continue;
                    UpgradeAllocations();
                    if (syncPeer.TotalDifficulty is { } syncPeerTD && syncPeerTD == _blockTree.BestSuggestedHeader?.TotalDifficulty &&
                        syncPeer.HeadHash != _blockTree.BestSuggestedHeader?.Hash)
                    {
                        Block block = _blockTree.FindBlock(_blockTree.BestSuggestedHeader.Hash!, BlockTreeLookupOptions.None);
                        if (block is not null)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Sending my best block {block} to {syncPeer:hide}");
                            NotifyPeerBlock?.Invoke(this, new PeerBlockNotificationEventArgs(syncPeer, block));
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Debug($"Refreshing info for {syncPeer:hide} failed {exception:hide}");
                }
                finally
                {
                    lock (_refreshLock)
                    {
                        state.Cancellation = null;
                        state.CurrentPeer = null;
                    }
                    cancellation.Dispose();
                }
            }
        }
        private void StartUpgradeTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting eth sync peer upgrade timer");
            Timer upgradeTimer = _upgradeTimer = new Timer(_allocationsUpgradeIntervalInMs);
            bool disposed = false;
            upgradeTimer.Elapsed += (_, _) =>
            {
                try
                {
                    upgradeTimer.Enabled = false;
                    UpgradeAllocations();
                }
                catch (Exception exception)
                {
                    _logger.DebugError("Allocations upgrade failure", exception);
                }
                finally
                {
                    if (!disposed)
                        upgradeTimer.Enabled = true;
                }
            };
            upgradeTimer.Disposed += (_, _) => { disposed = true; };
            upgradeTimer.Start();
        }

        internal void DropUselessPeers(bool force = false)
        {
            if (!force && DateTime.UtcNow - _lastUselessPeersDropTime < TimeSpan.FromSeconds(30))
                return;

            if (_logger.IsTrace) _logger.Trace($"Reviewing {PeerCount} peer usefulness");

            int peersDropped = 0;
            _lastUselessPeersDropTime = DateTime.UtcNow;

            if (PeerCount >= PeerMaxCount)
            {
                peersDropped += DropWorstPeer();
            }

            if (peersDropped > 0 && _logger.IsTrace) TraceDroppedUselessPeers(peersDropped);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceAddingPeer(ISyncPeer syncPeer) =>
            _logger.Trace($"Adding sync peer {syncPeer.Node?.ToString("c"):hide}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceRemovingPeer(ISyncPeer syncPeer) =>
            _logger.Trace($"Removing sync peer {syncPeer.Node?.ToString("c"):hide}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TracePeerCount() =>
            _logger.Trace($"PeerCount: {PeerCount}, PriorityPeerCount: {PriorityPeerCount}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceAddingToRefreshQueue(ISyncPeer syncPeer) =>
            _logger.Trace($"Adding {syncPeer.Node?.ToString("c"):hide} to refresh queue");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceRefreshedPeerInfo(ISyncPeer syncPeer) =>
            _logger.Trace($"Refreshed peer info for {syncPeer:hide}.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceDroppedUselessPeers(int peersDropped) =>
            _logger.Trace($"Dropped {peersDropped} useless peers");

        private int DropWorstPeer()
        {
            string? IsPeerWorstWithReason(PeerInfo currentPeer, PeerInfo toCompare)
            {
                bool currentHasNoProgress = currentPeer.HasNoProgress(AllocationContexts.Headers | AllocationContexts.Blocks | AllocationContexts.ForwardHeader);
                bool comparedHasNoProgress = toCompare.HasNoProgress(AllocationContexts.Headers | AllocationContexts.Blocks | AllocationContexts.ForwardHeader);
                if (currentHasNoProgress != comparedHasNoProgress)
                {
                    return comparedHasNoProgress ? "NO SYNC PROGRESS" : null;
                }

                if (!toCompare.IsWithinInitialRefreshInterval && !currentPeer.IsWithinInitialRefreshInterval
                    && toCompare.ServedHeadNumber != currentPeer.ServedHeadNumber)
                {
                    return toCompare.ServedHeadNumber < currentPeer.ServedHeadNumber ? "LOWEST SERVED NUMBER" : null;
                }

                if (toCompare.EvictionHeadNumber != currentPeer.EvictionHeadNumber)
                {
                    return toCompare.EvictionHeadNumber < currentPeer.EvictionHeadNumber ? "LOWEST NUMBER" : null;
                }

                if (toCompare.HasTimeoutEvidence != currentPeer.HasTimeoutEvidence)
                {
                    return toCompare.HasTimeoutEvidence ? "SYNC TIMEOUT" : null;
                }

                if ((_stats.GetStatsOrNull(toCompare.SyncPeer.Node)?.GetAverageTransferSpeed(TransferSpeedType.Latency) ?? long.MaxValue) >
                    (_stats.GetStatsOrNull(currentPeer.SyncPeer.Node)?.GetAverageTransferSpeed(TransferSpeedType.Latency) ?? long.MaxValue))
                {
                    return "HIGHEST PING";
                }

                return null;
            }

            bool canDropPriorityPeer = PriorityPeerCount >= PriorityPeerMaxCount;

            PeerInfo? worstPeer = null;
            string? worstReason = "DEFAULT";

            foreach (PeerInfo peerInfo in DroppablePeers)
            {
                if (peerInfo.SyncPeer.IsPriority && !canDropPriorityPeer)
                {
                    continue;
                }

                worstPeer ??= peerInfo;

                string? peerWorstReason = IsPeerWorstWithReason(worstPeer, peerInfo);
                if (peerWorstReason is not null)
                {
                    worstPeer = peerInfo;
                    worstReason = peerWorstReason;
                }
            }

            if (worstPeer is null)
            {
                return 0;
            }

            worstPeer.SyncPeer.Disconnect(DisconnectReason.DropWorstPeer, $"PEER REVIEW / {worstReason}");
            return 1;
        }

        public void SignalPeersChanged() => Interlocked.Exchange(ref _signal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();

        private async Task ExecuteRefreshTask(RefreshTotalDiffTask refreshTotalDiffTask, CancellationToken token)
        {
            ISyncPeer syncPeer = refreshTotalDiffTask.SyncPeer;
            Hash256 expectedHash = syncPeer.HeadHash;
            ulong claimedNumber = syncPeer.HeadNumber;
            Hash256 requestedHash = refreshTotalDiffTask.BlockHash ?? expectedHash;
            bool requestedHead = requestedHash == expectedHash;
            void DemoteUnservedHead()
            {
                if (requestedHead && _peers.TryGetValue(syncPeer.Node.Id, out PeerInfo? peer) && ReferenceEquals(peer.SyncPeer, syncPeer))
                    peer.DemoteHeadClaim(requestedHash, claimedNumber);
            }
            using CancellationTokenSource timeoutCancellation = new(Timeouts.Eth, RefreshTimeProvider);
            using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCancellation.Token);
            try
            {
                BlockHeader? header = await syncPeer.GetHeadBlockHeader(
                    requestedHash, requestCancellation.Token).WaitAsync(requestCancellation.Token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentPeer(syncPeer)) return;
                if (header is null)
                {
                    DemoteUnservedHead();
                    ReportRefreshFailed(syncPeer, "null response");
                    return;
                }
                if (!HeaderValidator.ValidateHash(header))
                {
                    DemoteUnservedHead();
                    ReportRefreshFailed(syncPeer, "invalid header hash");
                    return;
                }
                if (!syncPeer.IsInitialized) _stats.ReportSyncEvent(syncPeer.Node, NodeStatsEventType.SyncInitCompleted);
                if (header.Hash != requestedHash)
                {
                    ReportRefreshFailed(syncPeer, "unexpected header hash");
                    return;
                }
                UpdateSyncPeerHead(syncPeer, header, expectedHash, claimedNumber, null,
                    served: refreshTotalDiffTask.BlockHash is null, clearRefresh: refreshTotalDiffTask.BlockHash is null);
                SignalPeersChanged();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (OperationCanceledException)
            {
                if (IsCurrentPeer(syncPeer) && ReportRefreshFailedCore(syncPeer, "timeout", new TimeoutException()) >= 3)
                    DemoteUnservedHead();
            }
            catch (Exception exception)
            {
                if (IsCurrentPeer(syncPeer))
                {
                    if (ReportRefreshFailedCore(syncPeer, "faulted", exception) >= 3) DemoteUnservedHead();
                }
            }
        }
        /// <summary>
        ///     This is an important operation for long lasting allocations.
        ///     For example the full sync tends to allocate the same peer for many minutes and we use this method to ensure that
        ///     a newly arriving better peer can replace a currently selected one.
        ///     Consider that there are some external changes (e.g. node stats values change based on the sync transfer rates)
        ///     which may not be controlled from inside here, hence we decide to monitor the potential upgrades in a loop.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if an irreplaceable allocation is being replaced by this method (internal implementation error).</exception>
        private void UpgradeAllocations()
        {
            DropUselessPeers();
            WakeUpPeerThatSleptEnough();
        }

        private void WakeUpPeerThatSleptEnough()
        {
            bool limitBackoff = PeerCount <= 2;
            foreach (PeerInfo info in AllPeers)
            {
                info.TryToWakeUp(DateTime.UtcNow, _timeBeforeWakingShallowSleepingPeerUp, limitBackoff);
            }
        }

        /// <inheritdoc />
        public void UpdateSyncPeerHeadIfHeaderIsBetter(ISyncPeer syncPeer, BlockHeader header, Hash256 consensusRequestedHash) =>
            UpdateSyncPeerHead(syncPeer, header, syncPeer.HeadHash, syncPeer.HeadNumber, consensusRequestedHash, served: true, clearRefresh: true);

        private void UpdateSyncPeerHead(ISyncPeer syncPeer, BlockHeader header, Hash256 expectedHash, ulong expectedNumber,
            Hash256? consensusRequestedHash, bool served, bool clearRefresh)
        {
            if (!IsCurrentPeer(syncPeer)) return;
            if (header.Hash is null || !HeaderValidator.ValidateHash(header))
            {
                ReportRefreshFailed(syncPeer, "invalid header hash");
                return;
            }
            BlockHeader? known = _blockTree.FindHeader(header.Hash, BlockTreeLookupOptions.None);
            bool trusted = known is not null || consensusRequestedHash is { IsZero: false } && consensusRequestedHash == header.Hash;
            BlockHeader? parent = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.None);
            if (parent is not null && (parent.Number == ulong.MaxValue || header.Number != parent.Number + 1))
            {
                ReportRefreshFailed(syncPeer, "invalid parent number");
                return;
            }
            UInt256? totalDifficulty = trusted ? known?.TotalDifficulty : null;
            if (trusted && totalDifficulty is null && parent?.TotalDifficulty is { } parentDifficulty)
                totalDifficulty = parentDifficulty + header.Difficulty;
            bool better = header.Number > expectedNumber
                || totalDifficulty is { } difficulty && _betterPeerStrategy.Compare((difficulty, header.Number), syncPeer) > 0;
            if (better && syncPeer.TrySetHead(expectedHash, expectedNumber, header.Hash, header.Number))
            {
                if (totalDifficulty is not null) syncPeer.TotalDifficulty = totalDifficulty;
                PeerRefreshed?.Invoke(this, new PeerHeadRefreshedEventArgs(syncPeer, header));
            }
            syncPeer.IsInitialized = true;
            if (trusted && _peers.TryGetValue(syncPeer.Node.Id, out PeerInfo? peer) && ReferenceEquals(peer.SyncPeer, syncPeer))
            {
                if (served) peer.RecordServedHead(header.Number);
                if (clearRefresh) peer.RecordRefreshResponse();
            }
        }

        public void ReportRefreshFailed(ISyncPeer syncPeer, string reason, Exception? exception = null) =>
            ReportRefreshFailedCore(syncPeer, reason, exception);

        private int ReportRefreshFailedCore(ISyncPeer syncPeer, string reason, Exception? exception)
        {
            if (_logger.IsTrace) _logger.Trace($"Refresh failed reported: {syncPeer.Node?.ToString("c"):hide}, {reason:hide}, {exception:hide}");
            _stats.ReportSyncEvent(syncPeer.Node, syncPeer.IsInitialized ? NodeStatsEventType.SyncFailed : NodeStatsEventType.SyncInitFailed);
            if (IsRefreshTimeout(exception))
            {
                if (_peers.TryGetValue(syncPeer.Node.Id, out PeerInfo? existing) && ReferenceEquals(existing.SyncPeer, syncPeer))
                    return existing.RecordRefreshTimeout();
            }
            else
            {
                syncPeer.Disconnect(DisconnectReason.PeerRefreshFailed, $"refresh peer info fault - {reason}");
            }
            return 0;
        }

        private void UpdatePeerCountMetric(NodeClientType clientType, int delta) => Metrics.SyncPeers.AddOrUpdate(clientType, Math.Max(0, delta), (_, l) => l + delta);

        private static bool IsRefreshTimeout(Exception? exception)
        {
            if (exception is not AggregateException aggregate) return exception is OperationCanceledException or TimeoutException;
            IReadOnlyCollection<Exception> failures = aggregate.Flatten().InnerExceptions;
            if (failures.Count == 0) return false;
            foreach (Exception failure in failures)
                if (failure is not (OperationCanceledException or TimeoutException)) return false;
            return true;
        }

        private sealed class RefreshState
        {
            public RefreshTotalDiffTask? Pending;
            public ISyncPeer? CurrentPeer;
            public CancellationTokenSource? Cancellation;
            public Task Work = Task.CompletedTask;
        }

        private class RefreshTotalDiffTask
        {
            public RefreshTotalDiffTask(ISyncPeer syncPeer) => SyncPeer = syncPeer;

            public RefreshTotalDiffTask(Hash256 blockHash, ISyncPeer syncPeer)
            {
                BlockHash = blockHash;
                SyncPeer = syncPeer;
            }

            public Hash256? BlockHash { get; }

            public ISyncPeer SyncPeer { get; }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_isStarted) return;
            Task[] refreshTasks;
            lock (_refreshLock)
            {
                _isStarted = false;
                refreshTasks = new Task[_refreshes.Count];
                int index = 0;
                foreach (RefreshState refresh in _refreshes.Values) refreshTasks[index++] = refresh.Work;
            }
            _refreshLoopCancellation.Cancel();
            await Task.WhenAll(refreshTasks);
            Parallel.ForEach(_peers, static p => { p.Value.SyncPeer.Disconnect(DisconnectReason.AppClosing, "App Close"); });

            _refreshLoopCancellation.Dispose();
            _upgradeTimer?.Dispose();
        }
    }
}
