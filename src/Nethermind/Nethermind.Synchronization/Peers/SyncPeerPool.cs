// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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

        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private readonly Channel<RefreshTotalDiffTask> _peerRefreshQueue = Channel.CreateUnbounded<RefreshTotalDiffTask>();

        private readonly ConcurrentDictionary<PublicKey, PeerInfo> _peers = new();

        private readonly ConcurrentDictionary<PublicKey, CancellationTokenSource> _refreshCancelTokens = new();

        private readonly INodeStatsManager _stats;
        private readonly IBetterPeerStrategy _betterPeerStrategy;
        private readonly int _allocationsUpgradeIntervalInMs;

        private bool _isStarted;
        private readonly Lock _isAllocatedChecks = new();

        private DateTime _lastUselessPeersDropTime = DateTime.UtcNow;

        private readonly CancellationTokenSource _refreshLoopCancellation = new();
        private Task? _refreshLoopTask;

        private readonly ManualResetEvent _signals = new(true);
        private readonly TimeSpan _timeBeforeWakingShallowSleepingPeerUp = TimeSpan.FromMilliseconds(DefaultUpgradeIntervalInMs);
        private Timer? _upgradeTimer;

        public SyncPeerPool(IBlockTree blockTree,
            INodeStatsManager nodeStatsManager,
            IBetterPeerStrategy betterPeerStrategy,
            INetworkConfig networkConfig,
            ILogManager logManager)
        : this(blockTree, nodeStatsManager, betterPeerStrategy, logManager, networkConfig.ActivePeersMaxCount, networkConfig.PriorityPeersMaxCount)
        {

        }

        public SyncPeerPool(IBlockTree blockTree,
            INodeStatsManager nodeStatsManager,
            IBetterPeerStrategy betterPeerStrategy,
            ILogManager logManager,
            int peersMaxCount = 100,
            int priorityPeerMaxCount = 0,
            int allocationsUpgradeIntervalInMsInMs = DefaultUpgradeIntervalInMs)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _betterPeerStrategy = betterPeerStrategy ?? throw new ArgumentNullException(nameof(betterPeerStrategy));
            PeerMaxCount = peersMaxCount;
            PriorityPeerMaxCount = priorityPeerMaxCount;
            _allocationsUpgradeIntervalInMs = allocationsUpgradeIntervalInMsInMs;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            if (_logger.IsDebug) _logger.Debug($"PeerMaxCount: {PeerMaxCount}, PriorityPeerMaxCount: {PriorityPeerMaxCount}");
        }

        public void ReportNoSyncProgress(PeerInfo peerInfo, AllocationContexts allocationContexts)
        {
            ReportWeakPeer(peerInfo, allocationContexts);
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

        public void ReportWeakPeer(PeerInfo? weakPeer, AllocationContexts allocationContexts)
        {
            if (weakPeer is null)
            {
                /* it may have just got disconnected and in such case the allocation would be nullified
                 * in such case there is no need to talk about whether the peer is good or bad
                 */
                return;
            }

            AllocationContexts sleeps = weakPeer.IncreaseWeakness(allocationContexts);
            if (sleeps != AllocationContexts.None)
            {
                weakPeer.PutToSleep(sleeps, DateTime.UtcNow);
            }
        }

        public async Task<int?> EstimateRequestLimit(RequestType requestType, IPeerAllocationStrategy allocationStrategy, AllocationContexts context, CancellationToken token)
        {
            // So, to know which peer is next, we just try to allocate it, and then free it back.
            SyncPeerAllocation syncPeerAllocation = await Allocate(allocationStrategy, context, 1000, token);
            if (!syncPeerAllocation.HasPeer) return null;

            int requestSize = _stats.GetOrAdd(syncPeerAllocation.Current!.SyncPeer.Node).GetCurrentRequestLimit(requestType);
            Free(syncPeerAllocation);
            return requestSize;
        }

        public void Start()
        {
            _refreshLoopTask = Task.Factory.StartNew(
                    RunRefreshPeerLoop,
                    _refreshLoopCancellation.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap()
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error("Init peer loop encountered an exception.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        if (_logger.IsDebug) _logger.Debug("Init peer loop stopped.");
                    }
                    else if (t.IsCompleted)
                    {
                        if (_logger.IsError) _logger.Error("Peer loop completed unexpectedly.");
                    }
                });

            _isStarted = true;
            StartUpgradeTimer();
        }

        public PeerInfo? GetPeer(Node node) => _peers.TryGetValue(node.Id, out PeerInfo? peerInfo) ? peerInfo : null;
        public event EventHandler<PeerBlockNotificationEventArgs>? NotifyPeerBlock;
        public event EventHandler<PeerHeadRefreshedEventArgs>? PeerRefreshed;

        public void WakeUpAll()
        {
            foreach (var peer in _peers)
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

        public IEnumerable<PeerInfo> NonStaticPeers
        {
            get
            {
                foreach ((_, PeerInfo peerInfo) in _peers)
                {
                    if (peerInfo.SyncPeer.Node?.IsStatic == false)
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
        public int PriorityPeerCount = 0;
        public int InitializedPeersCount => InitializedPeers.Count();
        public int PeerMaxCount { get; }
        private int PriorityPeerMaxCount { get; }

        public void RefreshTotalDifficulty(ISyncPeer syncPeer, Hash256 blockHash)
        {
            RefreshTotalDiffTask task = new(blockHash, syncPeer);
            _peerRefreshQueue.Writer.TryWrite(task);
        }

        public void AddPeer(ISyncPeer syncPeer)
        {
            if (_logger.IsDebug) _logger.Debug($"Adding sync peer {syncPeer.Node:c}");
            if (!_isStarted)
            {
                if (_logger.IsDebug) _logger.Debug($"Sync peer pool not started yet - adding peer is blocked: {syncPeer.Node:s}");
                return;
            }

            if (_peers.ContainsKey(syncPeer.Node.Id))
            {
                if (_logger.IsDebug) _logger.Debug($"Sync peer {syncPeer.Node:c} already in peers collection.");
                return;
            }

            PeerInfo peerInfo = new(syncPeer);
            _peers.TryAdd(syncPeer.Node.Id, peerInfo);
            UpdatePeerCountMetric(peerInfo.PeerClientType, 1);

            if (syncPeer.IsPriority)
            {
                Interlocked.Increment(ref PriorityPeerCount);
                Metrics.PriorityPeers = PriorityPeerCount;
            }
            if (_logger.IsDebug) _logger.Debug($"PeerCount: {PeerCount}, PriorityPeerCount: {PriorityPeerCount}");

            BlockHeader? header = _blockTree.FindHeader(syncPeer.HeadHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (header is not null)
            {
                syncPeer.HeadNumber = header.Number;
                UpdateSyncPeerHeadIfHeaderIsBetter(syncPeer, header);
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Adding {syncPeer.Node:c} to refresh queue");
                if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportInterestingEvent(syncPeer.Node.Address, "adding node to refresh queue");
                _peerRefreshQueue.Writer.TryWrite(new RefreshTotalDiffTask(syncPeer));
            }
        }

        public void RemovePeer(ISyncPeer syncPeer)
        {
            if (_logger.IsDebug) _logger.Debug($"Removing sync peer {syncPeer.Node:c}");

            if (!_isStarted)
            {
                if (_logger.IsDebug) _logger.Debug($"Sync peer pool not started yet - removing {syncPeer.Node:c} is blocked.");
                return;
            }

            PublicKey id = syncPeer.Node.Id;
            if (id is null)
            {
                if (_logger.IsDebug) _logger.Debug("Peer ID was null when removing peer");
                return;
            }

            if (!_peers.TryRemove(id, out _))
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
            if (_logger.IsDebug) _logger.Debug($"PeerCount: {PeerCount}, PriorityPeerCount: {PriorityPeerCount}");

            if (_refreshCancelTokens.TryGetValue(id, out CancellationTokenSource? initCancelTokenSource))
            {
                initCancelTokenSource?.Cancel();
            }
        }

        public void SetPeerPriority(PublicKey id)
        {
            if (_peers.TryGetValue(id, out PeerInfo peerInfo) && !peerInfo.SyncPeer.IsPriority)
            {
                peerInfo.SyncPeer.IsPriority = true;
                Interlocked.Increment(ref PriorityPeerCount);
            }
        }

        public async Task<SyncPeerAllocation> Allocate(
            IPeerAllocationStrategy peerAllocationStrategy,
            AllocationContexts allocationContexts = AllocationContexts.All,
            int timeoutMilliseconds = 0,
            CancellationToken cancellationToken = default)
        {
            int tryCount = 1;
            DateTime startTime = DateTime.UtcNow;

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _refreshLoopCancellation.Token);

            SyncPeerAllocation allocation = new(allocationContexts, _isAllocatedChecks);
            while (true)
            {
                if (TryAllocateOnce(peerAllocationStrategy, allocationContexts, allocation))
                {
                    return allocation;
                }

                bool timeoutReached = timeoutMilliseconds == 0
                                      || (DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMilliseconds;
                if (timeoutReached) return SyncPeerAllocation.FailedAllocation;

                int waitTime = 10 * tryCount++;
                waitTime = Math.Min(waitTime, timeoutMilliseconds);

                if (waitTime > 0 && !_signals.SafeWaitHandle.IsClosed)
                {
                    await _signals.WaitOneAsync(waitTime, cts.Token);
                    if (!_signals.SafeWaitHandle.IsClosed)
                    {
                        _signals.Reset(); // without this we have no delay
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

        /// <summary>
        ///     Frees the allocation space borrowed earlier for some sync consumer.
        /// </summary>
        /// <param name="syncPeerAllocation">Allocation to free</param>
        public void Free(SyncPeerAllocation syncPeerAllocation)
        {
            if (_logger.IsTrace) _logger.Trace($"Returning {syncPeerAllocation}");

            syncPeerAllocation.Cancel();

            SignalPeersChanged();
        }

        private async Task RunRefreshPeerLoop()
        {
            await foreach (RefreshTotalDiffTask refreshTask in _peerRefreshQueue.Reader.ReadAllAsync(_refreshLoopCancellation.Token))
            {
                ISyncPeer syncPeer = refreshTask.SyncPeer;
                if (_logger.IsTrace) _logger.Trace($"Refreshing info for {syncPeer}.");
                CancellationTokenSource initCancelSource = _refreshCancelTokens[syncPeer.Node.Id] = new CancellationTokenSource();
                CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(initCancelSource.Token, _refreshLoopCancellation.Token);

#pragma warning disable 4014
                ExecuteRefreshTask(refreshTask, linkedSource.Token).ContinueWith(t =>
#pragma warning restore 4014
                {
                    _refreshCancelTokens.TryRemove(syncPeer.Node.Id, out _);
                    if (t.IsFaulted)
                    {
                        if (t.HasTimeoutException())
                        {
                            if (_logger.IsTrace) _logger.Trace($"Refreshing info for {syncPeer} failed due to timeout: {t.Exception.Message}");
                        }
                        else if (_logger.IsDebug)
                        {
                            _logger.Debug($"Refreshing info for {syncPeer} failed {t.Exception}");
                        }
                    }
                    else if (t.IsCanceled)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Refresh peer info canceled: {syncPeer.Node:s}");
                    }
                    else
                    {
                        UpgradeAllocations();
                        // cases when we want other nodes to resolve the impasse (check Goerli discussion on 5 out of 9 validators)
                        if (syncPeer.TotalDifficulty == _blockTree.BestSuggestedHeader?.TotalDifficulty && syncPeer.HeadHash != _blockTree.BestSuggestedHeader?.Hash)
                        {
                            Block block = _blockTree.FindBlock(_blockTree.BestSuggestedHeader.Hash!, BlockTreeLookupOptions.None);
                            if (block is not null) // can be null if fast syncing headers only
                            {
                                if (_logger.IsDebug) _logger.Debug($"Sending my best block {block} to {syncPeer}");
                                NotifyPeerBlock?.Invoke(this, new PeerBlockNotificationEventArgs(syncPeer, block));
                            }
                        }
                    }

                    if (_logger.IsDebug) _logger.Debug($"Refreshed peer info for {syncPeer}.");

                    initCancelSource.Dispose();
                    linkedSource.Dispose();
                });
            }

            if (_logger.IsInfo) _logger.Info("Exiting sync peer refresh loop");
            await Task.CompletedTask;
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
                    if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Allocations upgrade failure", exception);
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

            if (PeerCount == PeerMaxCount)
            {
                peersDropped += DropWorstPeer();
            }

            if (_logger.IsDebug) _logger.Debug($"Dropped {peersDropped} useless peers");
        }

        private int DropWorstPeer()
        {
            string? IsPeerWorstWithReason(PeerInfo currentPeer, PeerInfo toCompare)
            {
                if (toCompare.HeadNumber < currentPeer.HeadNumber)
                {
                    return "LOWEST NUMBER";
                }

                if (toCompare.TotalDifficulty < currentPeer.TotalDifficulty)
                {
                    return "LOWEST DIFFICULTY";
                }

                if ((_stats.GetOrAdd(toCompare.SyncPeer.Node).GetAverageTransferSpeed(TransferSpeedType.Latency) ?? long.MaxValue) >
                    (_stats.GetOrAdd(currentPeer.SyncPeer.Node).GetAverageTransferSpeed(TransferSpeedType.Latency) ?? long.MaxValue))
                {
                    return "HIGHEST PING";
                }

                return null;
            }

            bool canDropPriorityPeer = PriorityPeerCount >= PriorityPeerMaxCount;

            PeerInfo? worstPeer = null;
            string? worstReason = "DEFAULT";

            foreach (PeerInfo peerInfo in NonStaticPeers)
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

            worstPeer?.SyncPeer.Disconnect(DisconnectReason.DropWorstPeer, $"PEER REVIEW / {worstReason}");
            return 1;
        }

        public void SignalPeersChanged()
        {
            if (!_signals.SafeWaitHandle.IsClosed)
            {
                _signals.Set();
            }
        }

        private async Task ExecuteRefreshTask(RefreshTotalDiffTask refreshTotalDiffTask, CancellationToken token)
        {
            ISyncPeer syncPeer = refreshTotalDiffTask.SyncPeer;
            if (_logger.IsTrace) _logger.Trace($"Requesting head block info from {syncPeer.Node:s}");

            Task<BlockHeader?> getHeadHeaderTask = syncPeer.GetHeadBlockHeader(refreshTotalDiffTask.BlockHash ?? syncPeer.HeadHash, token);
            CancellationTokenSource delaySource = new();
            CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(delaySource.Token, token);
            Task delayTask = Task.Delay(Timeouts.Eth, linkedSource.Token);
            Task firstToComplete = await Task.WhenAny(getHeadHeaderTask, delayTask);
            await firstToComplete.ContinueWith(
                t =>
                {
                    try
                    {
                        if (firstToComplete == delayTask)
                        {
                            ReportRefreshFailed(syncPeer, "timeout", new TimeoutException());
                        }
                        else if (firstToComplete.IsFaulted)
                        {
                            ReportRefreshFailed(syncPeer, "faulted", t.Exception);
                        }
                        else if (firstToComplete.IsCanceled)
                        {
                            _stats.ReportSyncEvent(syncPeer.Node, syncPeer.IsInitialized ? NodeStatsEventType.SyncCancelled : NodeStatsEventType.SyncInitCancelled);
                            token.ThrowIfCancellationRequested();
                        }
                        else
                        {
                            delaySource.Cancel();
                            BlockHeader? header = getHeadHeaderTask.Result;
                            if (header is null)
                            {
                                ReportRefreshFailed(syncPeer, "null response");
                                return;
                            }

                            if (!HeaderValidator.ValidateHash(header))
                            {
                                ReportRefreshFailed(syncPeer, "invalid header hash");
                                return;
                            }

                            if (_logger.IsTrace) _logger.Trace($"Received head block info from {syncPeer.Node:c} with head block {header.ToString(BlockHeader.Format.Short)}, total difficulty {header.TotalDifficulty}");
                            if (!syncPeer.IsInitialized) _stats.ReportSyncEvent(syncPeer.Node, NodeStatsEventType.SyncInitCompleted);

                            if (_logger.IsTrace) _logger.Trace($"REFRESH Updating header of {syncPeer} from {syncPeer.HeadNumber} to {header.Number}");

                            UpdateSyncPeerHeadIfHeaderIsBetter(syncPeer, header);

                            SignalPeersChanged();
                        }
                    }
                    finally
                    {
                        linkedSource.Dispose();
                        delaySource.Dispose();
                    }
                }, token);
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
            foreach (PeerInfo info in AllPeers)
            {
                info.TryToWakeUp(DateTime.UtcNow, _timeBeforeWakingShallowSleepingPeerUp);
            }
        }

        public void UpdateSyncPeerHeadIfHeaderIsBetter(ISyncPeer syncPeer, BlockHeader header)
        {
            if (_logger.IsTrace) _logger.Trace($"REFRESH Updating header of {syncPeer} from {syncPeer.HeadNumber} to {header.Number}");
            BlockHeader? parent = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.None);
            if (parent is not null && (parent.TotalDifficulty ?? 0) != 0)
            {
                UInt256 newTotalDifficulty = (parent.TotalDifficulty ?? UInt256.Zero) + header.Difficulty;
                bool newValueIsNotWorseThanPeer = _betterPeerStrategy.Compare((newTotalDifficulty, header.Number), syncPeer) >= 0;
                if (_logger.IsTrace) _logger.Trace($"REFRESH Updating header of {syncPeer} from {syncPeer.HeadNumber} to {header.Number} based on totalDifficulty, newValueIsNotWorseThanPeer {newValueIsNotWorseThanPeer}, newTotalDifficulty: {newTotalDifficulty}, header.Difficulty: {header.Difficulty}, Parent total difficulty: {parent.TotalDifficulty}");
                if (newValueIsNotWorseThanPeer)
                {
                    syncPeer.TotalDifficulty = newTotalDifficulty;
                    syncPeer.HeadNumber = header.Number;
                    syncPeer.HeadHash = header.Hash!;
                    PeerRefreshed?.Invoke(this, new PeerHeadRefreshedEventArgs(syncPeer, header));
                }
            }
            else if (header.Number > syncPeer.HeadNumber)
            {
                if (_logger.IsTrace) _logger.Trace($"REFRESH Updating header of {syncPeer} from {syncPeer.HeadNumber} to {header.Number} based on headNumber");
                syncPeer.HeadNumber = header.Number;
                syncPeer.HeadHash = header.Hash!;
            }
            syncPeer.IsInitialized = true;
        }

        public void ReportRefreshFailed(ISyncPeer syncPeer, string reason, Exception? exception = null)
        {
            if (_logger.IsTrace) _logger.Trace($"Refresh failed reported: {syncPeer.Node:c}, {reason}, {exception}");
            _stats.ReportSyncEvent(syncPeer.Node, syncPeer.IsInitialized ? NodeStatsEventType.SyncFailed : NodeStatsEventType.SyncInitFailed);

            if (exception is OperationCanceledException || exception is TimeoutException)
            {
                // We don't want to disconnect on timeout. It could be that we are downloading from the peer,
                // or we have some connection issue
                ReportWeakPeer(new PeerInfo(syncPeer), AllocationContexts.All);
            }
            else
            {
                syncPeer.Disconnect(DisconnectReason.PeerRefreshFailed, $"refresh peer info fault - {reason}");
            }
        }

        private void UpdatePeerCountMetric(NodeClientType clientType, int delta)
        {
            Metrics.SyncPeers.AddOrUpdate(clientType, Math.Max(0, delta), (_, l) => l + delta);
        }

        private class RefreshTotalDiffTask
        {
            public RefreshTotalDiffTask(ISyncPeer syncPeer)
            {
                SyncPeer = syncPeer;
            }

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
            _isStarted = false;
            _refreshLoopCancellation.Cancel();
            await (_refreshLoopTask ?? Task.CompletedTask);
            Parallel.ForEach(_peers, static p => { p.Value.SyncPeer.Disconnect(DisconnectReason.AppClosing, "App Close"); });

            _peerRefreshQueue.Writer.TryComplete();
            _refreshLoopCancellation.Dispose();
            _refreshLoopTask?.Dispose();
            _signals.Dispose();
            _upgradeTimer?.Dispose();
        }
    }
}
