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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Timer = System.Timers.Timer;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Blockchain.Synchronization
{
    /// <summary>
    ///     Eth sync peer pool is capable of returning a sync peer allocation that is best suited for the requesting
    ///     sync process. It also manages all allocations allowing to replace peers with better peers whenever they connect.
    /// </summary>
    public class EthSyncPeerPool : IEthSyncPeerPool
    {
        private const decimal MinDiffPercentageForSpeedSwitch = 0.10m;
        private const int MinDiffForSpeedSwitch = 10;
        private const int MaxPeerWeakness = 10;
        private const int InitTimeout = 10000; // the Eth.Timeout should hit us earlier
        
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private readonly BlockingCollection<RefreshTotalDiffTask> _peerRefreshQueue = new BlockingCollection<RefreshTotalDiffTask>();

        private readonly ConcurrentDictionary<PublicKey, PeerInfo> _peers = new ConcurrentDictionary<PublicKey, PeerInfo>();
        private readonly ConcurrentDictionary<PublicKey, CancellationTokenSource> _refreshCancelTokens = new ConcurrentDictionary<PublicKey, CancellationTokenSource>();
        private readonly ConcurrentDictionary<SyncPeerAllocation, object> _replaceableAllocations = new ConcurrentDictionary<SyncPeerAllocation, object>();
        private readonly INodeStatsManager _stats;
        private int _allocationsUpgradeIntervalInMs;

        private bool _isStarted;

        private DateTime _lastUselessPeersDropTime = DateTime.UtcNow;

        private ConcurrentDictionary<PeerInfo, int> _peerWeakness = new ConcurrentDictionary<PeerInfo, int>();
        private CancellationTokenSource _refreshLoopCancellation = new CancellationTokenSource();
        private Task _refreshLoopTask;

        private ManualResetEvent _signals = new ManualResetEvent(true);
        private TimeSpan _timeBeforeWakingDeepSleepingPeerUp = TimeSpan.FromSeconds(3);
        private TimeSpan _timeBeforeWakingShallowSleepingPeerUp = TimeSpan.FromMilliseconds(500);
        private Timer _upgradeTimer;

        public EthSyncPeerPool(IBlockTree blockTree,
            INodeStatsManager nodeStatsManager,
            int peersMaxCount,
            ILogManager logManager)
            : this(blockTree, nodeStatsManager, peersMaxCount, 1000, logManager)
        {
        }

        public EthSyncPeerPool(IBlockTree blockTree,
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

        public void ReportNoSyncProgress(PeerInfo peerInfo, bool isSevere = true)
        {
            if (peerInfo == null) return;

            if (_logger.IsDebug) _logger.Debug($"No sync progress reported with {peerInfo}");
            peerInfo.SleepingSince = DateTime.UtcNow;
            peerInfo.IsSleepingDeeply = isSevere;
        }

        public void ReportInvalid(PeerInfo peerInfo, string details)
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

        public void ReportWeakPeer(SyncPeerAllocation allocation)
        {
            if (allocation.CanBeReplaced)
            {
                /* we are sneaking some external knowledge here
                 * we generally do not want to confuse the invalid / weak scenario
                 * weak peer is one that we want to give some time and then use again
                 * invalid peer is one that we prefer not to talk to as it behaves in a malicious way
                 * we know that more dynamic allocations are short lived and not replaceable
                 * and only such allocations should define weak peers
                 */
                throw new InvalidOperationException("Reporting weak peer is only supported for non-dynamic allocations");
            }

            PeerInfo weakPeer = allocation.Current;
            if (weakPeer == null) 
            {
                /* it may have just got disconnected and in such case the allocation would be nullified
                 * in such case there is no need to talk about whether the peer is good or bad
                 */
                return;
            }
            
            _peerWeakness.AddOrUpdate(weakPeer, 0, (pi, weaknessMeasure) => weaknessMeasure + 1);
            if (_peerWeakness[weakPeer] >= MaxPeerWeakness)
            {
                /* fast Geth nodes send invalid nodes quite often :/
                 * so we let them deliver fast and only disconnect them when they really misbehave
                 */
                allocation.Current.SyncPeer.Disconnect(DisconnectReason.UselessPeer, "peer is too weak");
                _peerWeakness.TryRemove(weakPeer, out _);
            }
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
            Parallel.ForEach(_peers, p => { p.Value.SyncPeer.Disconnect(DisconnectReason.ClientQuitting, "App Close"); });
        }

        public void WakeUpAll()
        {
            foreach (var peer in _peers) WakeUpPeer(peer.Value);

            _signals.Set();
        }

        public event EventHandler PeerAdded;

        public IEnumerable<PeerInfo> AllPeers
        {
            get
            {
                foreach ((_, PeerInfo peerInfo) in _peers) yield return peerInfo;
            }
        }

        public IEnumerable<PeerInfo> UsefulPeers
        {
            get
            {
                foreach ((_, PeerInfo peerInfo) in _peers)
                {
                    if (peerInfo.IsAsleep) continue;

                    if (!peerInfo.IsInitialized) continue;

                    /* While there are scenarios where we want peers with equal difficulty (node sync)
                     * I can think of no scenarios where lower difficulty node would be exceptionally useful
                     * Such nodes are not necessarily malicious or weak - they may be within their own sync processes.
                     */
                    if (peerInfo.TotalDifficulty < (_blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0)) continue;

                    yield return peerInfo;
                }
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
        public int UsefulPeerCount => UsefulPeers.Count();
        public int PeerMaxCount { get; }

        public void RefreshTotalDifficulty(PeerInfo peerInfo, Keccak blockHash)
        {
            _peerRefreshQueue.Add(new RefreshTotalDiffTask {PeerInfo = peerInfo, BlockHash = blockHash});
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

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            _peers.TryAdd(syncPeer.Node.Id, peerInfo);
            Metrics.SyncPeers = _peers.Count;

            if (_logger.IsDebug) _logger.Debug($"Adding {syncPeer.Node:c} to refresh queue");
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportInterestingEvent(peerInfo.SyncPeer.Node.Host, "adding node to refresh queue");
            _peerRefreshQueue.Add(new RefreshTotalDiffTask {PeerInfo = peerInfo});
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

            if (_logger.IsDebug) _logger.Debug($"Removing sync peer {syncPeer.Node:c}");

            if (!_peers.TryRemove(id, out _))
            {
                // possible if sync failed - we remove peer and eventually initiate disconnect, which calls remove peer again
                return;
            }

            Metrics.SyncPeers = _peers.Count;

            foreach ((SyncPeerAllocation allocation, _) in _replaceableAllocations)
            {
                if (allocation.Current?.SyncPeer.Node.Id == id)
                {
                    if (_logger.IsTrace) _logger.Trace($"Requesting peer cancel with {syncPeer.Node:c} on {allocation}");
                    allocation.Cancel();
                }
            }

            if (_refreshCancelTokens.TryGetValue(id, out CancellationTokenSource initCancelTokenSource)) initCancelTokenSource?.Cancel();
        }

        public bool TryFind(PublicKey nodeId, out PeerInfo peerInfo)
        {
            return _peers.TryGetValue(nodeId, out peerInfo);
        }

        public async Task<SyncPeerAllocation> BorrowAsync(PeerSelectionOptions peerSelectionOptions = PeerSelectionOptions.None, string description = "", long? minNumber = null, int timeoutMilliseconds = 0)
        {
            int tryCount = 1;
            DateTime startTime = DateTime.UtcNow;
            SyncPeerAllocation allocation = new SyncPeerAllocation(description);
            allocation.MinBlocksAhead = minNumber - _blockTree.BestSuggestedHeader?.Number;

            if ((peerSelectionOptions & PeerSelectionOptions.DoNotReplace) == PeerSelectionOptions.DoNotReplace)
            {
                allocation.CanBeReplaced = false;
            }
            else
            {
                _replaceableAllocations.TryAdd(allocation, null);
            }

            while (true)
            {
                PeerInfo bestPeer = SelectBestPeerForAllocation(allocation, "BORROW", peerSelectionOptions);
                if (bestPeer != null)
                {
                    allocation.ReplaceCurrent(bestPeer);
                    return allocation;
                }

                bool timeoutReached = timeoutMilliseconds == 0
                                      || (DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMilliseconds;
                if (timeoutReached) return allocation;

                int waitTime = 10 * tryCount++;

                await _signals.WaitOneAsync(waitTime, CancellationToken.None);
                _signals.Reset(); // without this we have no delay
            }
        }

        /// <summary>
        ///     Frees the allocation space borrowed earlier for some sync consumer.
        /// </summary>
        /// <param name="syncPeerAllocation">Allocation to free</param>
        public void Free(SyncPeerAllocation syncPeerAllocation)
        {
            if (_logger.IsTrace) _logger.Trace($"Returning {syncPeerAllocation}");

            PeerInfo peerInfo = syncPeerAllocation.Current;
            if (peerInfo != null && !syncPeerAllocation.CanBeReplaced)
            {
                // TODO: need to investigate it - does it negate any meaning of weak peers?
                _peerWeakness.TryRemove(peerInfo, out _);
            }

            _replaceableAllocations.TryRemove(syncPeerAllocation, out _);
            syncPeerAllocation.Cancel();

            if (_replaceableAllocations.Count > 1024 * 16) _logger.Warn($"Peer allocations leakage - {_replaceableAllocations.Count}");

            _signals.Set();
        }

        private async Task RunRefreshPeerLoop()
        {
            foreach (RefreshTotalDiffTask refreshTask in _peerRefreshQueue.GetConsumingEnumerable(_refreshLoopCancellation.Token))
            {
                PeerInfo peerInfo = refreshTask.PeerInfo;
                if (_logger.IsDebug) _logger.Debug($"Refreshing info for {peerInfo}.");
                CancellationTokenSource initCancelSource = _refreshCancelTokens[peerInfo.SyncPeer.Node.Id] = new CancellationTokenSource();
                CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(initCancelSource.Token, _refreshLoopCancellation.Token);

#pragma warning disable 4014
                ExecuteRefreshTask(refreshTask, linkedSource.Token).ContinueWith(t =>
#pragma warning restore 4014
                {
                    _refreshCancelTokens.TryRemove(peerInfo.SyncPeer.Node.Id, out _);
                    if (t.IsFaulted)
                    {
                        if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                        {
                            if (_logger.IsTrace) _logger.Trace($"Refreshing info for {peerInfo} failed due to timeout: {t.Exception.Message}");
                        }
                        else if (_logger.IsDebug)
                        {
                            _logger.Debug($"Refreshing info for {peerInfo} failed {t.Exception}");
                        }
                    }
                    else if (t.IsCanceled)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Refresh peer info canceled: {peerInfo.SyncPeer.Node:s}");
                    }
                    else
                    {
                        UpgradeAllocations("REFRESH");
                        // cases when we want other nodes to resolve the impasse (check Goerli discussion on 5 out of 9 validators)
                        if (peerInfo.TotalDifficulty == _blockTree.BestSuggestedHeader?.TotalDifficulty && peerInfo.HeadHash != _blockTree.BestSuggestedHeader?.Hash)
                        {
                            Block block = _blockTree.FindBlock(_blockTree.BestSuggestedHeader.Hash, BlockTreeLookupOptions.None);
                            if (block != null) // can be null if fast syncing headers only
                            {
                                peerInfo.SyncPeer.SendNewBlock(block);
                                if (_logger.IsDebug) _logger.Debug($"Sending my best block {block} to {peerInfo}");
                            }
                        }
                    }

                    if (_logger.IsDebug) _logger.Debug($"Refreshed peer info for {peerInfo}.");

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
            foreach (PeerInfo peerInfo in AllPeers)
            {
                if (peerInfo.HeadNumber > ourNumber)
                    // as long as we are behind we can use the stuck peers
                    continue;

                if (peerInfo.HeadNumber == 0
                    && peerInfo.IsInitialized
                    && ourNumber != 0
                    && !peerInfo.SyncPeer.ClientId.Contains("Nethermind"))
                    // we know that Nethermind reports 0 HeadNumber when it is in sync (and it can still serve a lot of data to other nodes)
                {
                    peersDropped++;
                    peerInfo.SyncPeer.Disconnect(DisconnectReason.UselessPeer, "PEER REVIEW / HEAD 0");
                }
                else if (peerInfo.HeadNumber == 1920000) // mainnet, stuck Geth nodes
                {
                    peersDropped++;
                    peerInfo.SyncPeer.Disconnect(DisconnectReason.UselessPeer, "PEER REVIEW / 1920000");
                }
                else if (peerInfo.HeadNumber == 7280022) // mainnet, stuck Geth nodes
                {
                    peersDropped++;
                    peerInfo.SyncPeer.Disconnect(DisconnectReason.UselessPeer, "PEER REVIEW / 7280022");
                }
                else if (peerInfo.HeadNumber > ourNumber + 1024L && peerInfo.TotalDifficulty < ourDifficulty)
                {
                    // probably Ethereum Classic nodes tht remain connected after we went pass the DAO
                    // worth to find a better way to discard them at the right time
                    peersDropped++;
                    peerInfo.SyncPeer.Disconnect(DisconnectReason.UselessPeer, "STRAY PEER");
                }
            }

            if (PeerCount == PeerMaxCount)
            {
                long worstSpeed = long.MaxValue;
                PeerInfo worstPeer = null;
                foreach (PeerInfo peerInfo in AllPeers)
                {
                    long transferSpeed = _stats.GetOrAdd(peerInfo.SyncPeer.Node).GetAverageTransferSpeed() ?? 0;
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
            _signals.Set();
        }

        private async Task ExecuteRefreshTask(RefreshTotalDiffTask refreshTotalDiffTask, CancellationToken token)
        {
            PeerInfo peerInfo = refreshTotalDiffTask.PeerInfo;
            if (_logger.IsTrace) _logger.Trace($"Requesting head block info from {peerInfo.SyncPeer.Node:s}");

            ISyncPeer syncPeer = peerInfo.SyncPeer;
            var getHeadHeaderTask = peerInfo.SyncPeer.GetHeadBlockHeader(refreshTotalDiffTask.BlockHash ?? peerInfo.HeadHash, token);
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
                            _stats.ReportSyncEvent(syncPeer.Node, peerInfo.IsInitialized ? NodeStatsEventType.SyncFailed : NodeStatsEventType.SyncInitFailed);
                            syncPeer.Disconnect(DisconnectReason.DisconnectRequested, "refresh peer info fault - timeout");
                        }
                        else if (firstToComplete.IsCanceled)
                        {
                            if (_logger.IsTrace) _logger.Trace($"InitPeerInfo canceled for node: {syncPeer.Node:c}{Environment.NewLine}{t.Exception}");
                            _stats.ReportSyncEvent(syncPeer.Node, peerInfo.IsInitialized ? NodeStatsEventType.SyncCancelled : NodeStatsEventType.SyncInitCancelled);
                            token.ThrowIfCancellationRequested();
                        }
                        else
                        {
                            delaySource.Cancel();
                            BlockHeader header = getHeadHeaderTask.Result;
                            if (header == null)
                            {
                                if (_logger.IsDebug) _logger.Debug($"InitPeerInfo failed for node: {syncPeer.Node:c}{Environment.NewLine}{t.Exception}");

                                _stats.ReportSyncEvent(syncPeer.Node, peerInfo.IsInitialized ? NodeStatsEventType.SyncFailed : NodeStatsEventType.SyncInitFailed);
                                syncPeer.Disconnect(DisconnectReason.DisconnectRequested, "refresh peer info fault - null response");
                                return;
                            }

                            if (_logger.IsTrace) _logger.Trace($"Received head block info from {syncPeer.Node:c} with head block numer {header.Number}");
                            if (!peerInfo.IsInitialized) _stats.ReportSyncEvent(syncPeer.Node, NodeStatsEventType.SyncInitCompleted);

                            if (_logger.IsTrace) _logger.Trace($"REFRESH Updating header of {peerInfo} from {peerInfo.HeadNumber} to {header.Number}");

                            BlockHeader parent = _blockTree.FindHeader(header.ParentHash, BlockTreeLookupOptions.None);
                            if (parent != null)
                            {
                                UInt256 newTotalDifficulty = (parent.TotalDifficulty ?? UInt256.Zero) + header.Difficulty;
                                if (newTotalDifficulty >= peerInfo.TotalDifficulty)
                                {
                                    peerInfo.TotalDifficulty = newTotalDifficulty;
                                    peerInfo.HeadNumber = header.Number;
                                    peerInfo.HeadHash = header.Hash;
                                }
                            }
                            else if (header.Number > peerInfo.HeadNumber)
                            {
                                peerInfo.HeadNumber = header.Number;
                                peerInfo.HeadHash = header.Hash;
                            }

                            peerInfo.IsInitialized = true;
                            foreach ((SyncPeerAllocation allocation, object _) in _replaceableAllocations)
                                if (allocation.Current == peerInfo)
                                    allocation.Refresh();

                            _signals.Set();
                            PeerAdded?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    finally
                    {
                        linkedSource.Dispose();
                        delaySource.Dispose();
                    }
                }, token);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private PeerInfo SelectBestPeerForAllocation(SyncPeerAllocation allocation, string reason, PeerSelectionOptions options)
        {
            bool isLowPriority = options.IsLowPriority();
            bool requireHigherDifficulty = options.RequiresHigherDifficulty();

            (PeerInfo Info, long TransferSpeed) bestPeer = (null, isLowPriority ? long.MaxValue : -1);
            foreach ((_, PeerInfo info) in _peers)
            {
                if (info.IsAllocated && info != allocation.Current)
                    // this means that the peer is already allocated somewhere else
                    continue;

                if (!info.IsInitialized)
                    // if peer is not yet initialized then we do not know much about its highest block
                    continue;

                if (allocation.MinBlocksAhead.HasValue && info.HeadNumber < (_blockTree.BestSuggestedHeader?.Number ?? 0) + allocation.MinBlocksAhead.Value)
                    // in some sync modes we need to be able to download some blocks ahead
                    continue;

                UInt256 localTotalDiff = _blockTree.BestSuggestedHeader?.TotalDifficulty ?? UInt256.Zero;
                if (requireHigherDifficulty && info.TotalDifficulty <= localTotalDiff)
                    // if we require higher difficulty then we need to discard peers with same diff as ours
                    continue;

                if (!requireHigherDifficulty && info.TotalDifficulty < localTotalDiff)
                    // if we do not require higher difficulty then we discard only peers with lower diff than ours
                    continue;

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

                if (info.TotalDifficulty - (_blockTree.BestSuggestedHeader?.TotalDifficulty ?? UInt256.Zero) <= 2 && info.PeerClientType == PeerClientType.Parity)
                    // if (_logger.IsWarn) _logger.Warn($"[{reason}] parity diff");
                    // Parity advertises a better block but never sends it back and then it disconnects after a few conversations like this
                    // Geth responds all fine here
                    // note this is only 2 difficulty difference which means that is just for the POA / Clique chains
                    continue;

                long averageTransferSpeed = _stats.GetOrAdd(info.SyncPeer.Node).GetAverageTransferSpeed() ?? 0;

                bool selectedByDiff = false;
                if (requireHigherDifficulty)
                {
                    decimal diff = (decimal) averageTransferSpeed / (bestPeer.TransferSpeed == 0 ? 1 : bestPeer.TransferSpeed);
                    // if not much difference in speed then prefer total diff
                    if (diff > 0.75m && diff < 1.25m)
                    {
                        if (info.TotalDifficulty == 0 && info.HeadNumber > bestPeer.Info.HeadNumber)
                            // actually worth to learn about the difficulty of this peer and this may be a good way
                            bestPeer = (info, averageTransferSpeed);
                        else if (info.TotalDifficulty > bestPeer.Info.TotalDifficulty) bestPeer = (info, averageTransferSpeed);

                        selectedByDiff = true;
                    }
                }

                if (!selectedByDiff)
                    if (isLowPriority ? averageTransferSpeed <= bestPeer.TransferSpeed : averageTransferSpeed > bestPeer.TransferSpeed)
                        bestPeer = (info, averageTransferSpeed);
            }

            if (_peers.Count == 0)
                if (_logger.IsTrace)
                    _logger.Trace($"[{reason}] count 0");

            if (bestPeer.Info == null)
            {
                if (_logger.IsTrace) _logger.Trace($"[{reason}] No peer found for ETH sync");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"[{reason}] Best ETH sync peer: {bestPeer.Info} | BlockHeaderAvSpeed: {bestPeer.TransferSpeed}");
            }

            return bestPeer.Info;
        }

        private void ReplaceIfWorthReplacing(SyncPeerAllocation allocation, PeerInfo candidatePeerInfo)
        {
            if (!allocation.CanBeReplaced) return;

            if (candidatePeerInfo == null) return;

            PeerInfo currentPeer = allocation.Current;
            if (currentPeer == null)
            {
                /* any peer is better than no peer when we need a peer
                 */
                if (_logger.IsTrace) _logger.Trace($"{allocation} had no peer and now has {candidatePeerInfo}");
                allocation.ReplaceCurrent(candidatePeerInfo);
                return;
            }

            if (candidatePeerInfo == allocation.Current)
            {
                if (_logger.IsTrace) _logger.Trace($"{allocation} is already syncing with best peer {candidatePeerInfo}");
                return;
            }

            long currentSpeed = _stats.GetOrAdd(currentPeer.SyncPeer.Node)?.GetAverageTransferSpeed() ?? 0;
            long newSpeed = _stats.GetOrAdd(candidatePeerInfo.SyncPeer.Node)?.GetAverageTransferSpeed() ?? 0;

            /* we used to select based on latency
             * and it was generally worse except for some cases:
             *   1) we can see some significant speed differences between block and node data sync speed
             *      and so sometimes a good block data peer will get selected for node data
             *   2) newly joining peers have to have some arbitrarily high number assigned
             */
            if (newSpeed / (decimal) Math.Max(1L, currentSpeed) > 1m + MinDiffPercentageForSpeedSwitch
                && newSpeed > currentSpeed + MinDiffForSpeedSwitch)
            {
                if (_logger.IsDebug) _logger.Debug($"Sync peer substitution{Environment.NewLine}  OUT: {allocation.Current}[{currentSpeed}]{Environment.NewLine}  IN : {candidatePeerInfo}[{newSpeed}]");
                allocation.ReplaceCurrent(candidatePeerInfo);
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Staying with current peer {allocation.Current}[{currentSpeed}] (ignoring {candidatePeerInfo}[{newSpeed}])");
            }
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
            foreach ((SyncPeerAllocation allocation, _) in _replaceableAllocations)
            {
                if (!allocation.CanBeReplaced) throw new InvalidOperationException("Unreplaceable allocation in the replacable allocations collection");

                PeerInfo bestPeer = SelectBestPeerForAllocation(allocation, reason, PeerSelectionOptions.None);
                if (bestPeer != allocation.Current)
                {
                    ReplaceIfWorthReplacing(allocation, bestPeer);
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace("No better peer to sync with when updating allocations");
                }
            }
        }

        private class RefreshTotalDiffTask
        {
            public Keccak BlockHash { get; set; }

            public PeerInfo PeerInfo { get; set; }
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