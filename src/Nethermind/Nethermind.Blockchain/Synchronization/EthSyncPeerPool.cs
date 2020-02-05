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

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Blockchain.Synchronization
{
    /// <summary>
    /// Eth sync peer pool is capable of returning a sync peer allocation that is best suited for the requesting
    /// sync process. It also manages all allocations allowing to replace peers with better peers whenever they connect.
    /// </summary>
    public class EthSyncPeerPool : IEthSyncPeerPool
    {
        private const decimal MinDiffPercentageForSpeedSwitch = 0.10m;
        private const int MinDiffForSpeedSwitch = 10;
        private const int InitTimeout = 10000; // the Eth.Timeout should hit us earlier

        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        private readonly INodeStatsManager _stats;

        private readonly ConcurrentDictionary<PublicKey, PeerInfo> _peers = new ConcurrentDictionary<PublicKey, PeerInfo>();
        private readonly ConcurrentDictionary<SyncPeerAllocation, object> _replaceableAllocations = new ConcurrentDictionary<SyncPeerAllocation, object>();
        private int _allocationsUpgradeIntervalInMs;
        private System.Timers.Timer _upgradeTimer;
        
        private readonly BlockingCollection<RefreshTask> _peerRefreshQueue = new BlockingCollection<RefreshTask>();
        private Task _refreshLoopTask;
        private CancellationTokenSource _refreshLoopCancellation = new CancellationTokenSource();
        private readonly ConcurrentDictionary<PublicKey, CancellationTokenSource> _refreshCancelTokens = new ConcurrentDictionary<PublicKey, CancellationTokenSource>();
        private TimeSpan _timeBeforeWakingDeepSleepingPeerUp = TimeSpan.FromSeconds(3);
        private TimeSpan _timeBeforeWakingShallowSleepingPeerUp = TimeSpan.FromMilliseconds(500);

        private bool _isStarted;

        public void ReportNoSyncProgress(SyncPeerAllocation allocation, bool isSevere = true)
        {
            ReportNoSyncProgress(allocation?.Current, isSevere);
        }

        public void ReportNoSyncProgress(PeerInfo peerInfo, bool isSevere = true)
        {
            if (peerInfo == null)
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug($"No sync progress reported with {peerInfo}");
            peerInfo.SleepingSince = DateTime.UtcNow;
            peerInfo.IsSleepingDeeply = isSevere;
        }

        public void ReportInvalid(SyncPeerAllocation allocation, string details)
        {
            ReportInvalid(allocation?.Current, details);
        }

        public void ReportInvalid(PeerInfo peerInfo, string details)
        {
            if (peerInfo != null)
            {
                _stats.ReportSyncEvent(peerInfo.SyncPeer.Node, NodeStatsEventType.SyncFailed);
                peerInfo.SyncPeer.Disconnect(DisconnectReason.BreachOfProtocol, details);
            }
        }

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

        private async Task RunRefreshPeerLoop()
        {
            foreach (RefreshTask refreshTask in _peerRefreshQueue.GetConsumingEnumerable(_refreshLoopCancellation.Token))
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
                            Console.WriteLine("TIMEOUTTTTT");
                            if (_logger.IsTrace) _logger.Trace($"Refreshing info for {peerInfo} failed due to timeout: {t.Exception.Message}");
                        }
                        else if (_logger.IsDebug) _logger.Debug($"Refreshing info for {peerInfo} failed {t.Exception}");
                    }
                    else if (t.IsCanceled)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Refresh peer info canceled: {peerInfo.SyncPeer.Node:s}");
                    }
                    else
                    {
                        UpdateAllocations("REFRESH");
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

            if (_logger.IsInfo) _logger.Info($"Exiting sync peer refresh loop");
            await Task.CompletedTask;
        }

        public void Start()
        {
//            _refreshLoopTask = Task.Run(RunRefreshPeerLoop, _refreshLoopCancellation.Token)
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

        private void StartUpgradeTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting eth sync peer upgrade timer");
            _upgradeTimer = new System.Timers.Timer(_allocationsUpgradeIntervalInMs);
            _upgradeTimer.Elapsed += (s, e) =>
            {
                try
                {
                    _upgradeTimer.Enabled = false;
                    UpdateAllocations("TIMER");
                    DropUselessPeers();
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

        private DateTime _lastUselessDrop = DateTime.UtcNow;

        internal void DropUselessPeers(bool force = false)
        {
            if (!force && DateTime.UtcNow - _lastUselessDrop < TimeSpan.FromSeconds(30))
            {
                // give some time to monitoring nodes
                // (monitoring nodes are nodes that are investigating the network but are not synced themselves)
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Reviewing {PeerCount} peer usefulness");

            int peersDropped = 0;
            _lastUselessDrop = DateTime.UtcNow;

            long ourNumber = _blockTree.BestSuggestedHeader?.Number ?? 0L;
            UInt256 ourDifficulty = _blockTree.BestSuggestedHeader?.TotalDifficulty ?? UInt256.Zero;
            foreach (PeerInfo peerInfo in AllPeers)
            {
                if (peerInfo.HeadNumber > ourNumber)
                {
                    // as long as we are behind we can use the stuck peers
                    continue;
                }

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
                    if (transferSpeed < worstSpeed)
                    {
                        worstPeer = peerInfo;
                    }
                }

                peersDropped++;
                worstPeer?.SyncPeer.Disconnect(DisconnectReason.TooManyPeers, "PEER REVIEW / LATENCY");
            }

            if (_logger.IsDebug) _logger.Debug($"Dropped {peersDropped} useless peers");
        }

        public async Task StopAsync()
        {
            _isStarted = false;
            _refreshLoopCancellation.Cancel();
            await (_refreshLoopTask ?? Task.CompletedTask);
            Parallel.ForEach(_peers, p => { p.Value.SyncPeer.Disconnect(DisconnectReason.ClientQuitting, "App Close"); });
        }

        private ConcurrentDictionary<PeerInfo, int> _peerBadness = new ConcurrentDictionary<PeerInfo, int>();

        public void ReportBadPeer(SyncPeerAllocation batchAssignedPeer)
        {
            if (batchAssignedPeer.CanBeReplaced)
            {
                throw new InvalidOperationException("Reporting bad peer is only supported for non-dynamic allocations");
            }

            _peerBadness.AddOrUpdate(batchAssignedPeer.Current, 0, (pi, badness) => badness + 1);
            if (_peerBadness[batchAssignedPeer.Current] >= 10)
            {
                // fast Geth nodes send invalid nodes quite often :/
                // so we let them deliver fast and only disconnect them when they really misbehave
                batchAssignedPeer.Current.SyncPeer.Disconnect(DisconnectReason.BreachOfProtocol, "bad node data");
            }
        }

        private void WakeUpPeer(PeerInfo info)
        {
            info.SleepingSince = null;
            info.IsSleepingDeeply = false;
            _signals.Set();
        }

        public void WakeUpAll()
        {
            foreach (var peer in _peers)
            {
                WakeUpPeer(peer.Value);
            }

            _signals.Set();
        }

        public event EventHandler PeerAdded;

        private class RefreshTask
        {
            public Keccak BlockHash { get; set; }

            public PeerInfo PeerInfo { get; set; }
        }

        private async Task ExecuteRefreshTask(RefreshTask refreshTask, CancellationToken token)
        {
            PeerInfo peerInfo = refreshTask.PeerInfo;
            if (_logger.IsTrace) _logger.Trace($"Requesting head block info from {peerInfo.SyncPeer.Node:s}");

            ISyncPeer syncPeer = peerInfo.SyncPeer;
            Task<BlockHeader> getHeadHeaderTask = peerInfo.SyncPeer.GetHeadBlockHeader(refreshTask.BlockHash ?? peerInfo.HeadHash, token);
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
                            if (!peerInfo.IsInitialized)
                            {
                                _stats.ReportSyncEvent(syncPeer.Node, NodeStatsEventType.SyncInitCompleted);
                            }

                            if (_logger.IsTrace) _logger.Trace($"REFRESH Updating header of {peerInfo} from {peerInfo.HeadNumber} to {header.Number}");
                            peerInfo.HeadNumber = header.Number;
                            peerInfo.HeadHash = header.Hash;

                            BlockHeader parent = _blockTree.FindHeader(header.ParentHash, BlockTreeLookupOptions.None);
                            if (parent != null)
                            {
                                peerInfo.TotalDifficulty = (parent.TotalDifficulty ?? UInt256.Zero) + header.Difficulty;
                            }

                            peerInfo.IsInitialized = true;
                            foreach ((SyncPeerAllocation allocation, object _) in _replaceableAllocations)
                            {
                                if (allocation.Current == peerInfo)
                                {
                                    allocation.Refresh();
                                }
                            }

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

        public IEnumerable<PeerInfo> AllPeers
        {
            get
            {
                foreach ((_, PeerInfo peerInfo) in _peers)
                {
                    yield return peerInfo;
                }
            }
        }

        public IEnumerable<PeerInfo> UsefulPeers
        {
            get
            {
                foreach ((_, PeerInfo peerInfo) in _peers)
                {
                    if (peerInfo.IsAsleep)
                    {
                        continue;
                    }

                    if (!peerInfo.IsInitialized)
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

        public IEnumerable<SyncPeerAllocation> Allocations
        {
            get
            {
                foreach ((SyncPeerAllocation allocation, _) in _replaceableAllocations)
                {
                    yield return allocation;
                }
            }
        }

        public int PeerCount => _peers.Count;
        public int UsefulPeerCount => UsefulPeers.Count();
        public int PeerMaxCount { get; }

        public void Refresh(PeerInfo peerInfo, Keccak blockHash)
        {
            _peerRefreshQueue.Add(new RefreshTask {PeerInfo = peerInfo, BlockHash = blockHash});
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

            var peerInfo = new PeerInfo(syncPeer);
            _peers.TryAdd(syncPeer.Node.Id, peerInfo);
            Metrics.SyncPeers = _peers.Count;

            if (_logger.IsDebug) _logger.Debug($"Adding {syncPeer.Node:c} to refresh queue");
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportInterestingEvent(peerInfo.SyncPeer.Node.Host, "adding node to refresh queue");
            _peerRefreshQueue.Add(new RefreshTask {PeerInfo = peerInfo});
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
                //possible if sync failed - we remove peer and eventually initiate disconnect, which calls remove peer again
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

            if (_refreshCancelTokens.TryGetValue(id, out CancellationTokenSource initCancelTokenSource))
            {
                initCancelTokenSource?.Cancel();
            }
        }

        private static Random _random = new Random();

        [MethodImpl(MethodImplOptions.Synchronized)]
        private PeerInfo SelectBestPeerForAllocation(SyncPeerAllocation allocation, string reason, PeerSelectionOptions options)
        {
            bool isLowPriority = options.IsLowPriority();
            bool requireHigherDifficulty = options.RequiresHigherDifficulty();
            
            (PeerInfo Info, long TransferSpeed) bestPeer = (null, isLowPriority ? long.MaxValue : -1);
            foreach ((_, PeerInfo info) in _peers.OrderBy(kvp => _random.Next()))
            {
                if (info.IsAllocated && info != allocation.Current)
                {
                    // this means that the peer is already allocated somewhere else
                    continue;
                }
                
                if (!info.IsInitialized)
                {
                    // if peer is not yet initialized then we do not know much about its highest block
                    continue;
                }
                
                if (allocation.MinBlocksAhead.HasValue && info.HeadNumber < (_blockTree.BestSuggestedHeader?.Number ?? 0) + allocation.MinBlocksAhead.Value)
                {
                    // in some sync modes we need to be able to download some blocks ahead
                    continue;
                }

                UInt256 localTotalDiff = _blockTree.BestSuggestedHeader?.TotalDifficulty ?? UInt256.Zero;
                if (requireHigherDifficulty && info.TotalDifficulty <= localTotalDiff)
                {
                    // if we require higher difficulty then we need to discard peers with same diff as ours
                    continue;
                }
                
                if (!requireHigherDifficulty && info.TotalDifficulty < localTotalDiff)
                {
                    // if we do not require higher difficulty then we discard only peers with lower diff than ours
                    continue;
                }

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
                {
                    // if (_logger.IsWarn) _logger.Warn($"[{reason}] parity diff");
                    // Parity advertises a better block but never sends it back and then it disconnects after a few conversations like this
                    // Geth responds all fine here
                    // note this is only 2 difficulty difference which means that is just for the POA / Clique chains
                    continue;
                }

                long averageTransferSpeed = _stats.GetOrAdd(info.SyncPeer.Node).GetAverageTransferSpeed() ?? 0;

                if (isLowPriority ? (averageTransferSpeed <= bestPeer.TransferSpeed) : (averageTransferSpeed > bestPeer.TransferSpeed))
                {
                    bestPeer = (info, averageTransferSpeed);
                }
            }

            if (_peers.Count == 0)
            {
                if (_logger.IsTrace) _logger.Trace($"[{reason}] count 0");
            }

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

        private void ReplaceIfWorthReplacing(SyncPeerAllocation allocation, PeerInfo peerInfo)
        {
            if (!allocation.CanBeReplaced)
            {
                return;
            }

            if (peerInfo == null)
            {
                return;
            }

            if (allocation.Current == null)
            {
                allocation.ReplaceCurrent(peerInfo);
                return;
            }

            if (peerInfo == allocation.Current)
            {
                if (_logger.IsTrace) _logger.Trace($"{allocation} is already syncing with best peer {peerInfo}");
                return;
            }

            long currentSpeed = _stats.GetOrAdd(allocation.Current?.SyncPeer.Node)?.GetAverageTransferSpeed() ?? 0;
            long newSpeed = _stats.GetOrAdd(peerInfo.SyncPeer.Node)?.GetAverageTransferSpeed() ?? 0;

            if (newSpeed / (decimal) Math.Max(1L, currentSpeed) > 1m + MinDiffPercentageForSpeedSwitch
                && newSpeed > currentSpeed + MinDiffForSpeedSwitch)
            {
                if (_logger.IsInfo) _logger.Info($"Sync peer substitution{Environment.NewLine}  OUT: {allocation.Current}[{currentSpeed}]{Environment.NewLine}  IN : {peerInfo}[{newSpeed}]");
                allocation.ReplaceCurrent(peerInfo);
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Staying with current peer {allocation.Current}[{currentSpeed}] (ignoring {peerInfo}[{newSpeed}])");
            }
        }

        private void UpdateAllocations(string reason)
        {
            foreach ((SyncPeerAllocation allocation, _) in _replaceableAllocations)
            {
                if (!allocation.CanBeReplaced)
                {
                    throw new InvalidOperationException("Unreplaceable allocation in the replacable allocations collection");
                }

                PeerInfo bestPeer = SelectBestPeerForAllocation(allocation, reason, PeerSelectionOptions.None);
                if (bestPeer != allocation.Current)
                {
                    ReplaceIfWorthReplacing(allocation, bestPeer);
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"No better peer to sync with when updating allocations");
                }
            }
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
                if (timeoutReached)
                {
                    return allocation;
                }

                int waitTime = 10 * tryCount++;

                await _signals.WaitOneAsync(waitTime, CancellationToken.None);
                _signals.Reset(); // without this we have no delay
            }
        }

        private ManualResetEvent _signals = new ManualResetEvent(true);

        /// <summary>
        /// Frees the allocation space borrowed earlier for some sync consumer.
        /// </summary>
        /// <param name="syncPeerAllocation">Allocation to free</param>
        public void Free(SyncPeerAllocation syncPeerAllocation)
        {
            if (_logger.IsTrace) _logger.Trace($"Returning {syncPeerAllocation}");

            PeerInfo peerInfo = syncPeerAllocation.Current;
            if (peerInfo != null && !syncPeerAllocation.CanBeReplaced)
            {
                _peerBadness.TryRemove(peerInfo, out _);
            }

            _replaceableAllocations.TryRemove(syncPeerAllocation, out _);
            syncPeerAllocation.Cancel();

            if (_replaceableAllocations.Count > 1024 * 16)
            {
                _logger.Warn($"Peer allocations leakage - {_replaceableAllocations.Count}");
            }

            _signals.Set();
        }
    }
}