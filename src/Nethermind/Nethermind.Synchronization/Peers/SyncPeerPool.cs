//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Specs;
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
    public class SyncPeerPool : ISyncPeerPool
    {
        public const int PeerWeaknessBeforeSleep = 2;
        public const int PeerWeaknessBeforeDisconnect = 8;
        private const int InitTimeout = 10000; // the Eth.Timeout hits us at 5000

        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private readonly BlockingCollection<RefreshTotalDiffTask> _peerRefreshQueue = new BlockingCollection<RefreshTotalDiffTask>();

        private readonly ConcurrentDictionary<PublicKey, ISyncPeer> _peers = new ConcurrentDictionary<PublicKey, ISyncPeer>();
        private readonly ConcurrentDictionary<ISyncPeer, PeerInfo> _tempMapping = new ConcurrentDictionary<ISyncPeer, PeerInfo>();
        
        private readonly ConcurrentDictionary<PublicKey, CancellationTokenSource> _refreshCancelTokens = new ConcurrentDictionary<PublicKey, CancellationTokenSource>();
        private readonly ConcurrentDictionary<SyncPeerAllocation, object> _replaceableAllocations = new ConcurrentDictionary<SyncPeerAllocation, object>();
        private readonly INodeStatsManager _stats;
        private int _allocationsUpgradeIntervalInMs;

        private bool _isStarted;

        private DateTime _lastUselessPeersDropTime = DateTime.UtcNow;

        private CancellationTokenSource _refreshLoopCancellation = new CancellationTokenSource();
        private Task _refreshLoopTask;

        private ManualResetEvent _signals = new ManualResetEvent(true);
        private TimeSpan _timeBeforeWakingDeepSleepingPeerUp = TimeSpan.FromSeconds(3);
        private TimeSpan _timeBeforeWakingShallowSleepingPeerUp = TimeSpan.FromMilliseconds(500);
        private Timer _upgradeTimer;

        public SyncPeerPool(IBlockTree blockTree,
            INodeStatsManager nodeStatsManager,
            int peersMaxCount,
            ILogManager logManager)
            : this(blockTree, nodeStatsManager, peersMaxCount, 1000, logManager)
        {
        }

        public SyncPeerPool(IBlockTree blockTree,
            INodeStatsManager nodeStatsManager,
            int peersMaxCount,
            int allocationsUpgradeIntervalInMsInMs,
            ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            PeerMaxCount = peersMaxCount;
            _allocationsUpgradeIntervalInMs = allocationsUpgradeIntervalInMsInMs;
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void ReportNoSyncProgress(PeerInfo peerInfo)
        {
            ReportWeakPeer(peerInfo);
        }

        public void ReportBreachOfProtocol(PeerInfo peerInfo, string details)
        {
            /* since the allocations can have the peers dynamically changed
             * it may be hard for the external classes to ensure that the peerInfo is not null at the time when they report
             * so we decide to check for null here and not consider the scenario to be exceptional
             */
            if (peerInfo != null)
            {
                _stats.ReportSyncEvent(peerInfo.SyncPeer.Node, NodeStatsEventType.SyncFailed);
                peerInfo.SyncPeer.Disconnect(DisconnectReason.BreachOfProtocol, details);
            }
        }

        public void ReportWeakPeer(PeerInfo weakPeer)
        {
            if (weakPeer == null)
            {
                /* it may have just got disconnected and in such case the allocation would be nullified
                 * in such case there is no need to talk about whether the peer is good or bad
                 */
                return;
            }

            int currentWeakness = weakPeer.IncreaseWeakness();
            if (currentWeakness > PeerWeaknessBeforeDisconnect)
            {
                /* fast Geth nodes send invalid nodes quite often :/
                 * so we let them deliver fast and only disconnect them when they really misbehave
                 */
                _logger.Warn("Disconnecting a weak peer");
                weakPeer.SyncPeer.Disconnect(DisconnectReason.UselessPeer, "peer is too weak");
            }
            else if (currentWeakness > PeerWeaknessBeforeSleep)
            {
                weakPeer.SleepingSince = DateTime.UtcNow;
                weakPeer.IsSleepingDeeply = false; // not used at the moment
            }
        }

        public void ReportWeakPeer(SyncPeerAllocation allocation)
        {
            ReportWeakPeer(allocation.Current);
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

        public async Task StopAsync()
        {
            _isStarted = false;
            _refreshLoopCancellation.Cancel();
            await (_refreshLoopTask ?? Task.CompletedTask);
            Parallel.ForEach(_peers, p => { p.Value.Disconnect(DisconnectReason.ClientQuitting, "App Close"); });
        }

        public void WakeUpAll()
        {
            foreach (var peer in _peers) WakeUpPeer(_tempMapping[peer.Value]);
        }

        public IEnumerable<ISyncPeer> AllPeers
        {
            get
            {
                foreach ((_, ISyncPeer peerInfo) in _peers) yield return peerInfo;
            }
        }

        public IEnumerable<PeerInfo> UsefulPeers
        {
            get
            {
                foreach (PeerInfo peerInfo in UsefulPeersWhateverDiff)
                {
                    /* This condition is critical when trying to recognize the chain head for sync mode selections
                     */
                    if (peerInfo.TotalDifficulty < (_blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0))
                    {
                        continue;
                    }


                    if (peerInfo.TotalDifficulty < (_blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0))
                    {
                        continue;
                    }

                    yield return peerInfo;
                }
            }
        }

        public IEnumerable<PeerInfo> UsefulPeersWhateverDiff
        {
            get
            {
                int sleepingCount = 0;
                int uninitializedCount = 0;
                int okCount = 0;

                foreach ((_, ISyncPeer syncPeer) in _peers)
                {
                    PeerInfo peerInfo = _tempMapping[syncPeer];
                    if (peerInfo.IsAsleep)
                    {
                        sleepingCount++;
                        continue;
                    }

                    if (!peerInfo.SyncPeer.IsInitialized)
                    {
                        uninitializedCount++;
                        continue;
                    }

                    okCount++;
                    yield return peerInfo;
                }

                // _logger.Warn($"Sleeping: {sleepingCount}, Uninitialized: {uninitializedCount}, Low Diff: {lowTotalDiff}, OK: {okCount}");
            }
        }

        internal IEnumerable<SyncPeerAllocation> ReplaceableAllocations
        {
            get
            {
                foreach ((SyncPeerAllocation allocation, _) in _replaceableAllocations) yield return allocation;
            }
        }

        public int PeerCount => _peers.Count;
        public int UsefulPeerCountWhateverDiff => UsefulPeersWhateverDiff.Count();
        public int PeerMaxCount { get; }

        public void RefreshTotalDifficulty(ISyncPeer syncPeer, Keccak blockHash)
        {
            _peerRefreshQueue.Add(new RefreshTotalDiffTask {SyncPeer = syncPeer, BlockHash = blockHash});
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
            
            _peers.TryAdd(syncPeer.Node.Id, syncPeer);
            _tempMapping.TryAdd(syncPeer, new PeerInfo(syncPeer));
            Metrics.SyncPeers = _peers.Count;

            if (_logger.IsDebug) _logger.Debug($"Adding {syncPeer.Node:c} to refresh queue");
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportInterestingEvent(syncPeer.Node.Host, "adding node to refresh queue");
            _peerRefreshQueue.Add(new RefreshTotalDiffTask {SyncPeer = syncPeer});
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
            if (id == null)
            {
                if (_logger.IsDebug) _logger.Debug("Peer ID was null when removing peer");
                return;
            }

            if (!_peers.TryRemove(id, out ISyncPeer removedPeer))
            {
                // possible if sync failed - we remove peer and eventually initiate disconnect, which calls remove peer again
                return;
            }
            else
            {
                _tempMapping.TryRemove(removedPeer, out _);    
            }

            Metrics.SyncPeers = _peers.Count;

            foreach ((SyncPeerAllocation allocation, _) in _replaceableAllocations)
            {
                if (allocation.Current?.SyncPeer.Node.Id == id)
                {
                    PeerInfo peerInfo = allocation.Current;
                    if (_logger.IsTrace) _logger.Trace($"Requesting peer cancel with {syncPeer.Node:c} on {allocation}");
                    allocation.Cancel();
                    peerInfo.MarkDisconnected();
                }
            }

            if (_refreshCancelTokens.TryGetValue(id, out CancellationTokenSource initCancelTokenSource)) initCancelTokenSource?.Cancel();
        }

        private object _isAllocatedChecks = new object();

        public async Task<SyncPeerAllocation> Allocate(IPeerAllocationStrategy peerAllocationStrategy, int timeoutMilliseconds = 0)
        {
            int tryCount = 1;
            DateTime startTime = DateTime.UtcNow;

            SyncPeerAllocation allocation = new SyncPeerAllocation(peerAllocationStrategy);
            while (true)
            {
                lock (_isAllocatedChecks)
                {
                    allocation.AllocateBestPeer(UsefulPeersWhateverDiff.Where(p => !p.IsAllocated), _stats, _blockTree);
                    if (allocation.HasPeer)
                    {
                        if (peerAllocationStrategy.CanBeReplaced)
                        {
                            _replaceableAllocations.TryAdd(allocation, null);
                        }

                        return allocation;
                    }
                }

                bool timeoutReached = timeoutMilliseconds == 0
                                      || (DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMilliseconds;
                if (timeoutReached) return SyncPeerAllocation.FailedAllocation;

                int waitTime = 10 * tryCount++;
                
                if (!_signals.SafeWaitHandle.IsClosed)
                {
                    await _signals.WaitOneAsync(waitTime, _refreshLoopCancellation.Token);
                    if (!_signals.SafeWaitHandle.IsClosed)
                    {
                        _signals.Reset(); // without this we have no delay
                    }
                }
            }
        }

        /// <summary>
        ///     Frees the allocation space borrowed earlier for some sync consumer.
        /// </summary>
        /// <param name="syncPeerAllocation">Allocation to free</param>
        public void Free(SyncPeerAllocation syncPeerAllocation)
        {
            if (_logger.IsTrace) _logger.Trace($"Returning {syncPeerAllocation}");

            _replaceableAllocations.TryRemove(syncPeerAllocation, out _);
            syncPeerAllocation.Cancel();

            if (_replaceableAllocations.Count > 1024 * 16) _logger.Warn($"Peer allocations leakage - {_replaceableAllocations.Count}");

            SignalPeersChanged();
        }

        private async Task RunRefreshPeerLoop()
        {
            foreach (RefreshTotalDiffTask refreshTask in _peerRefreshQueue.GetConsumingEnumerable(_refreshLoopCancellation.Token))
            {
                ISyncPeer syncPeer = refreshTask.SyncPeer;
                if (_logger.IsDebug) _logger.Debug($"Refreshing info for {syncPeer}.");
                CancellationTokenSource initCancelSource = _refreshCancelTokens[syncPeer.Node.Id] = new CancellationTokenSource();
                CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(initCancelSource.Token, _refreshLoopCancellation.Token);

#pragma warning disable 4014
                ExecuteRefreshTask(refreshTask, linkedSource.Token).ContinueWith(t =>
#pragma warning restore 4014
                {
                    _refreshCancelTokens.TryRemove(syncPeer.Node.Id, out _);
                    if (t.IsFaulted)
                    {
                        if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
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
                        UpgradeAllocations("REFRESH");
                        // cases when we want other nodes to resolve the impasse (check Goerli discussion on 5 out of 9 validators)
                        if (syncPeer.TotalDifficulty == _blockTree.BestSuggestedHeader?.TotalDifficulty && syncPeer.HeadHash != _blockTree.BestSuggestedHeader?.Hash)
                        {
                            Block block = _blockTree.FindBlock(_blockTree.BestSuggestedHeader.Hash, BlockTreeLookupOptions.None);
                            if (block != null) // can be null if fast syncing headers only
                            {
                                syncPeer.SendNewBlock(block);
                                if (_logger.IsDebug) _logger.Debug($"Sending my best block {block} to {syncPeer}");
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
            _upgradeTimer = new Timer(_allocationsUpgradeIntervalInMs);
            _upgradeTimer.Elapsed += (s, e) =>
            {
                try
                {
                    _upgradeTimer.Enabled = false;
                    UpgradeAllocations("TIMER");
                }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Error("Allocations upgrade failure", exception);
                }
                finally
                {
                    _upgradeTimer.Enabled = true;
                }
            };

            _upgradeTimer.Start();
        }

        private bool CanBeUsefulForFastBlocks(long blockNumber)
        {
            long lowestInsertedBody = _blockTree.LowestInsertedBody?.Number ?? long.MaxValue;
            long lowestInsertedHeader = _blockTree.LowestInsertedHeader?.Number ?? long.MaxValue;
            return lowestInsertedBody > 1 && lowestInsertedBody < blockNumber ||
                   lowestInsertedHeader > 1 && lowestInsertedHeader < blockNumber;
        }

        internal void DropUselessPeers(bool force = false)
        {
            if (!force && DateTime.UtcNow - _lastUselessPeersDropTime < TimeSpan.FromSeconds(30))
                // give some time to monitoring nodes
                // (monitoring nodes are nodes that are investigating the network but are not synced themselves)
                return;

            if (_logger.IsTrace) _logger.Trace($"Reviewing {PeerCount} peer usefulness");

            int peersDropped = 0;
            _lastUselessPeersDropTime = DateTime.UtcNow;

            long ourNumber = _blockTree.BestSuggestedHeader?.Number ?? 0L;
            UInt256 ourDifficulty = _blockTree.BestSuggestedHeader?.TotalDifficulty ?? UInt256.Zero;
            foreach (ISyncPeer syncPeer in AllPeers)
            {
                PeerInfo peerInfo = _tempMapping[syncPeer];
                if (syncPeer.HeadNumber == 0
                    && syncPeer.IsInitialized
                    && ourNumber != 0
                    && peerInfo.PeerClientType != PeerClientType.Nethermind)
                    // we know that Nethermind reports 0 HeadNumber when it is in sync (and it can still serve a lot of data to other nodes)
                {
                    if (!CanBeUsefulForFastBlocks(syncPeer.HeadNumber))
                    {
                        peersDropped++;
                        syncPeer.Disconnect(DisconnectReason.UselessPeer, "PEER REVIEW / HEAD 0");
                    }
                }
                else if (syncPeer.HeadNumber == 1920000 && _blockTree.ChainId == ChainId.Mainnet) // mainnet, stuck Geth nodes
                {
                    if (!CanBeUsefulForFastBlocks(syncPeer.HeadNumber))
                    {
                        peersDropped++;
                        syncPeer.Disconnect(DisconnectReason.UselessPeer, "PEER REVIEW / 1920000");
                    }
                }
                else if (syncPeer.HeadNumber == 7280022 && _blockTree.ChainId == ChainId.Mainnet) // mainnet, stuck Geth nodes
                {
                    if (!CanBeUsefulForFastBlocks(syncPeer.HeadNumber))
                    {
                        peersDropped++;
                        syncPeer.Disconnect(DisconnectReason.UselessPeer, "PEER REVIEW / 7280022");
                    }
                }
                else if (syncPeer.HeadNumber > ourNumber + 1024L && syncPeer.TotalDifficulty < ourDifficulty)
                {
                    if (!CanBeUsefulForFastBlocks(MainnetSpecProvider.Instance.DaoBlockNumber ?? 0))
                    {
                        // probably Ethereum Classic nodes tht remain connected after we went pass the DAO
                        // worth to find a better way to discard them at the right time
                        peersDropped++;
                        syncPeer.Disconnect(DisconnectReason.UselessPeer, "STRAY PEER");
                    }
                }
            }

            if (PeerCount == PeerMaxCount)
            {
                long worstSpeed = long.MaxValue;
                PeerInfo worstPeer = null;
                foreach (ISyncPeer syncPeer in AllPeers)
                {
                    PeerInfo peerInfo = _tempMapping[syncPeer];
                    long transferSpeed = _stats.GetOrAdd(syncPeer.Node).GetAverageTransferSpeed() ?? 0;
                    if (transferSpeed < worstSpeed) worstPeer = peerInfo;
                }

                peersDropped++;
                worstPeer?.SyncPeer.Disconnect(DisconnectReason.TooManyPeers, "PEER REVIEW / LATENCY");
            }

            if (_logger.IsDebug) _logger.Debug($"Dropped {peersDropped} useless peers");
        }

        private void WakeUpPeer(PeerInfo info)
        {
            info.SleepingSince = null;
            info.IsSleepingDeeply = false;
            SignalPeersChanged();
        }

        private void SignalPeersChanged()
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

            var getHeadHeaderTask = syncPeer.GetHeadBlockHeader(refreshTotalDiffTask.BlockHash ?? syncPeer.HeadHash, token);
            CancellationTokenSource delaySource = new CancellationTokenSource();
            CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(delaySource.Token, token);
            Task delayTask = Task.Delay(InitTimeout, linkedSource.Token);
            Task firstToComplete = await Task.WhenAny(getHeadHeaderTask, delayTask);
            await firstToComplete.ContinueWith(
                t =>
                {
                    try
                    {
                        if (firstToComplete.IsFaulted || firstToComplete == delayTask)
                        {
                            if (_logger.IsDebug) _logger.Debug($"InitPeerInfo failed for node: {syncPeer.Node:c}{Environment.NewLine}{t.Exception}");
                            _stats.ReportSyncEvent(syncPeer.Node, syncPeer.IsInitialized ? NodeStatsEventType.SyncFailed : NodeStatsEventType.SyncInitFailed);
                            syncPeer.Disconnect(DisconnectReason.DisconnectRequested, "refresh peer info fault - timeout");
                        }
                        else if (firstToComplete.IsCanceled)
                        {
                            if (_logger.IsTrace) _logger.Trace($"InitPeerInfo canceled for node: {syncPeer.Node:c}{Environment.NewLine}{t.Exception}");
                            _stats.ReportSyncEvent(syncPeer.Node, syncPeer.IsInitialized ? NodeStatsEventType.SyncCancelled : NodeStatsEventType.SyncInitCancelled);
                            token.ThrowIfCancellationRequested();
                        }
                        else
                        {
                            delaySource.Cancel();
                            BlockHeader header = getHeadHeaderTask.Result;
                            if (header == null)
                            {
                                if (_logger.IsDebug) _logger.Debug($"InitPeerInfo failed for node: {syncPeer.Node:c}{Environment.NewLine}{t.Exception}");

                                _stats.ReportSyncEvent(syncPeer.Node, syncPeer.IsInitialized ? NodeStatsEventType.SyncFailed : NodeStatsEventType.SyncInitFailed);
                                syncPeer.Disconnect(DisconnectReason.DisconnectRequested, "refresh peer info fault - null response");
                                return;
                            }

                            if (_logger.IsTrace) _logger.Trace($"Received head block info from {syncPeer.Node:c} with head block numer {header.Number}");
                            if (!syncPeer.IsInitialized) _stats.ReportSyncEvent(syncPeer.Node, NodeStatsEventType.SyncInitCompleted);

                            if (_logger.IsTrace) _logger.Trace($"REFRESH Updating header of {syncPeer} from {syncPeer.HeadNumber} to {header.Number}");

                            BlockHeader parent = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.None);
                            if (parent != null)
                            {
                                UInt256 newTotalDifficulty = (parent.TotalDifficulty ?? UInt256.Zero) + header.Difficulty;
                                if (newTotalDifficulty >= syncPeer.TotalDifficulty)
                                {
                                    syncPeer.TotalDifficulty = newTotalDifficulty;
                                    syncPeer.HeadNumber = header.Number;
                                    syncPeer.HeadHash = header.Hash;
                                }
                            }
                            else if (header.Number > syncPeer.HeadNumber)
                            {
                                syncPeer.HeadNumber = header.Number;
                                syncPeer.HeadHash = header.Hash;
                            }

                            syncPeer.IsInitialized = true;

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
        /// <param name="reason">Reason for the method invocation for the diagnostics</param>
        /// <exception cref="InvalidOperationException">Thrown if an irreplaceable allocation is being replaced by this method (internal implementation error).</exception>
        private void UpgradeAllocations(string reason)
        {
            DropUselessPeers();
            WakeUpPeerThatSleptEnough(reason);
            foreach ((SyncPeerAllocation allocation, _) in _replaceableAllocations)
            {
                lock (_isAllocatedChecks)
                {
                    var unallocatedPeers = UsefulPeersWhateverDiff.Where(p => !p.IsAllocated);
                    allocation.AllocateBestPeer(unallocatedPeers, _stats, _blockTree);
                }
            }
        }

        private void WakeUpPeerThatSleptEnough(string reason)
        {
            foreach (ISyncPeer syncPeer in AllPeers)
            {
                PeerInfo info = _tempMapping[syncPeer];
                if (info.IsAsleep)
                {
                    if (DateTime.UtcNow - info.SleepingSince <
                        (info.IsSleepingDeeply
                            ? _timeBeforeWakingDeepSleepingPeerUp
                            : _timeBeforeWakingShallowSleepingPeerUp))
                    {
                        if (_logger.IsTrace) _logger.Trace($"[{reason}] {(info.IsSleepingDeeply ? "deeply" : "lightly")} asleep");
                        continue;
                    }

                    WakeUpPeer(info);
                }
            }
        }

        private class RefreshTotalDiffTask
        {
            public Keccak BlockHash { get; set; }

            public ISyncPeer SyncPeer { get; set; }
        }

        public void Dispose()
        {
            _peerRefreshQueue?.Dispose();
            _refreshLoopCancellation?.Dispose();
            _refreshLoopTask?.Dispose();
            _signals?.Dispose();
            _upgradeTimer?.Dispose();
        }
    }
}