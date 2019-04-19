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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Blockchain.Synchronization
{
    public class EthSyncPeerPool : IEthSyncPeerPool
    {
        private readonly IBlockTree _blockTree;
        private readonly INodeStatsManager _stats;
        private readonly ISyncConfig _syncConfig;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<PublicKey, PeerInfo> _peers = new ConcurrentDictionary<PublicKey, PeerInfo>();

        private ConcurrentDictionary<SyncPeerAllocation, object> _allocations = new ConcurrentDictionary<SyncPeerAllocation, object>();
        private const int AllocationsUpgradeInterval = 1000;
        private System.Timers.Timer _upgradeTimer;

        private readonly BlockingCollection<PeerInfo> _peerRefreshQueue = new BlockingCollection<PeerInfo>();
        private Task _refreshLoopTask;
        private CancellationTokenSource _refreshLoopCancellation = new CancellationTokenSource();
        private readonly ConcurrentDictionary<PublicKey, CancellationTokenSource> _refreshCancelTokens = new ConcurrentDictionary<PublicKey, CancellationTokenSource>();
        public event EventHandler<SyncEventArgs> SyncEvent;

        private ConcurrentDictionary<PeerInfo, DateTime> _sleepingPeers = new ConcurrentDictionary<PeerInfo, DateTime>();
        private TimeSpan _timeBeforeWakingPeerUp = TimeSpan.FromSeconds(3);

        public EthSyncPeerPool(IBlockTree blockTree, INodeStatsManager nodeStatsManager, ISyncConfig syncConfig, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private async Task RunRefreshPeerLoop()
        {
            foreach (PeerInfo peerInfo in _peerRefreshQueue.GetConsumingEnumerable(_refreshLoopCancellation.Token))
            {
                try
                {
                    if (_logger.IsDebug) _logger.Debug($"Running refresh peer info for {peerInfo}.");
                    var initCancelSource = _refreshCancelTokens[peerInfo.SyncPeer.Node.Id] = new CancellationTokenSource();
                    var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(initCancelSource.Token, _refreshLoopCancellation.Token);
                    await RefreshPeerInfo(peerInfo, linkedSource.Token).ContinueWith(t =>
                    {
                        _refreshCancelTokens.TryRemove(peerInfo.SyncPeer.Node.Id, out _);
                        if (t.IsFaulted)
                        {
                            if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                            {
                                if (_logger.IsTrace) _logger.Trace($"Refreshing {peerInfo} failed due to timeout: {t.Exception.Message}");
                            }
                            else if (_logger.IsDebug) _logger.Debug($"Refreshing {peerInfo} failed {t.Exception}");
                        }
                        else if (t.IsCanceled)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Refresh peer info canceled: {peerInfo.SyncPeer.Node:s}");
                        }
                        else
                        {
                            UpdateAllocations("REFRESH");
                            // cases when we want other nodes to resolve the impasse (check Goerli discussion on 5 out of 9 validators)
                            if (peerInfo.TotalDifficulty == _blockTree.BestSuggested?.TotalDifficulty && peerInfo.HeadHash != _blockTree.BestSuggested?.Hash)
                            {
                                Block block = _blockTree.FindBlock(_blockTree.BestSuggested.Hash, false);
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
                catch (Exception e)
                {
                    if (_logger.IsDebug) _logger.Debug($"Failed to refresh {peerInfo} {e}");
                }
            }

            if (_logger.IsInfo) _logger.Info($"Exiting sync peer refresh loop");
        }

        private bool _isStarted;

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

            _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
        }

        private void BlockTreeOnNewHeadBlock(object sender, BlockEventArgs e)
        {
            foreach ((SyncPeerAllocation allocation, _) in _allocations)
            {
                if (allocation.Current == null)
                {
                    continue;
                }

                if (allocation.Current.TotalDifficulty < (e.Block.TotalDifficulty ?? 0))
                {
                    allocation.Cancel();
                }
            }
        }

        private void StartUpgradeTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting eth sync peer upgrade timer");
            _upgradeTimer = new System.Timers.Timer(AllocationsUpgradeInterval);
            _upgradeTimer.Elapsed += (s, e) =>
            {
                try
                {
                    _upgradeTimer.Enabled = false;
                    UpdateAllocations("TIMER");
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

        public async Task StopAsync()
        {
            _isStarted = false;
            _refreshLoopCancellation.Cancel();
            await (_refreshLoopTask ?? Task.CompletedTask);
        }

        public void EnsureBest()
        {
            UpdateAllocations("ENSURE BEST");
        }

        private static int InitTimeout = 10000;

        private async Task RefreshPeerInfo(PeerInfo peerInfo, CancellationToken token)
        {
            if (_logger.IsTrace) _logger.Trace($"Requesting head block info from {peerInfo.SyncPeer.Node:s}");

            ISyncPeer syncPeer = peerInfo.SyncPeer;
            Task<BlockHeader> getHeadHeaderTask = peerInfo.SyncPeer.GetHeadBlockHeader(peerInfo.HeadHash, token);
            Task delayTask = Task.Delay(InitTimeout, token);
            Task firstToComplete = await Task.WhenAny(getHeadHeaderTask, delayTask);
            await firstToComplete.ContinueWith(
                t =>
                {
                    if (firstToComplete.IsFaulted || firstToComplete == delayTask)
                    {
                        if (_logger.IsDebug) _logger.Debug($"InitPeerInfo failed for node: {syncPeer.Node:s}{Environment.NewLine}{t.Exception}");
                        syncPeer.Disconnect(DisconnectReason.DisconnectRequested, "refresh peer info fault");
                        SyncEvent?.Invoke(this, new SyncEventArgs(syncPeer, peerInfo.IsInitialized ? Synchronization.SyncEvent.Failed : Synchronization.SyncEvent.InitFailed));
                    }
                    else if (firstToComplete.IsCanceled)
                    {
                        if (_logger.IsTrace) _logger.Trace($"InitPeerInfo canceled for node: {syncPeer.Node:s}{Environment.NewLine}{t.Exception}");
                        SyncEvent?.Invoke(this, new SyncEventArgs(syncPeer, peerInfo.IsInitialized ? Synchronization.SyncEvent.Cancelled : Synchronization.SyncEvent.InitCancelled));
                        token.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"Received head block info from {syncPeer.Node:s} with head block numer {getHeadHeaderTask.Result}");
                        if (!peerInfo.IsInitialized)
                        {
                            SyncEvent?.Invoke(
                                this,
                                new SyncEventArgs(syncPeer, Synchronization.SyncEvent.InitCompleted));
                        }

                        if (_logger.IsTrace) _logger.Trace($"REFRESH Updating header of {peerInfo} from {peerInfo.HeadNumber} to {getHeadHeaderTask.Result.Number}");
                        peerInfo.HeadNumber = getHeadHeaderTask.Result.Number;
                        peerInfo.HeadHash = getHeadHeaderTask.Result.Hash;

                        BlockHeader parent = _blockTree.FindHeader(getHeadHeaderTask.Result.ParentHash);
                        if (parent != null)
                        {
                            peerInfo.TotalDifficulty = (parent.TotalDifficulty ?? UInt256.Zero) + getHeadHeaderTask.Result.Difficulty;
                        }

                        peerInfo.IsInitialized = true;
                        foreach ((SyncPeerAllocation allocation, object _) in _allocations)
                        {
                            if (allocation.Current == peerInfo)
                            {
                                allocation.Refresh();
                            }
                        }
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

        public IEnumerable<SyncPeerAllocation> Allocations
        {
            get
            {
                foreach ((SyncPeerAllocation allocation, _) in _allocations)
                {
                    yield return allocation;
                }
            }
        }

        public int PeerCount => _peers.Count;
        public int PeerMaxCount => _syncConfig.SyncPeersMaxCount;

        public void Refresh(PublicKey publicKey)
        {
            TryFind(publicKey, out PeerInfo peerInfo);
            if (peerInfo != null)
            {
                _peerRefreshQueue.Add(peerInfo);
            }
        }

        public void AddPeer(ISyncPeer syncPeer)
        {
            if (_logger.IsDebug) _logger.Debug($"|NetworkTrace| Adding synchronization peer {syncPeer.Node:f}");
            if (!_isStarted)
            {
                if (_logger.IsDebug) _logger.Debug($"Sync peer pool not started yet - adding peer is blocked: {syncPeer.Node:s}");
                return;
            }

            if (_peers.ContainsKey(syncPeer.Node.Id))
            {
                if (_logger.IsDebug) _logger.Debug($"Sync peer already in peers collection: {syncPeer.Node:c}");
                return;
            }

            var peerInfo = new PeerInfo(syncPeer);
            _peers.TryAdd(syncPeer.Node.Id, peerInfo);
            Metrics.SyncPeers = _peers.Count;

            if (_logger.IsDebug) _logger.Debug($"Adding to refresh queue");
            _peerRefreshQueue.Add(peerInfo);
        }

        public enum PeerRemoveReason
        {
            SyncFault,
            SessionDisconnected,
        }

        public void RemovePeer(ISyncPeer syncPeer, PeerRemoveReason reason)
        {
            if (_logger.IsInfo) _logger.Info($"Removing synchronization peer {syncPeer.Node:c} - {reason}");
            if (!_isStarted)
            {
                if (_logger.IsDebug) _logger.Debug($"Sync peer pool not started yet - removing peer is blocked: {syncPeer.Node:s}");
                return;
            }

            if (!_peers.TryRemove(syncPeer.Node.Id, out var peerInfo))
            {
                //possible if sync failed - we remove peer and eventually initiate disconnect, which calls remove peer again
                return;
            }

            Metrics.SyncPeers = _peers.Count;
            foreach ((SyncPeerAllocation allocation, _) in _allocations)
            {
                if (allocation.Current?.SyncPeer.Node.Id == syncPeer.Node.Id)
                {
                    if (_logger.IsTrace) _logger.Trace($"Requesting peer cancel with: {syncPeer.Node:s} on {allocation}");
                    allocation.Cancel();
                }
            }

            if (_refreshCancelTokens.TryGetValue(syncPeer.Node.Id, out CancellationTokenSource initCancelTokenSource))
            {
                initCancelTokenSource?.Cancel();
            }
        }

        private PeerInfo SelectBestPeerForAllocation(SyncPeerAllocation allocation, string reason)
        {
            if (_logger.IsTrace) _logger.Trace($"[{reason}] Selecting best peer");
            (PeerInfo Info, long Latency) bestPeer = (null, 100000);
            foreach ((_, PeerInfo info) in _peers)
            {
                if (!info.IsInitialized || info.TotalDifficulty <= (_blockTree.BestSuggested?.TotalDifficulty ?? UInt256.Zero))
                {
                    continue;
                }

                if (info.IsAllocated && info != allocation.Current)
                {
                    continue;
                }

                if (_sleepingPeers.TryGetValue(info, out DateTime sleepingSince))
                {
                    if (DateTime.UtcNow - sleepingSince < _timeBeforeWakingPeerUp)
                    {
                        continue;
                    }

                    _sleepingPeers.TryRemove(info, out _);
                }

                if (info.TotalDifficulty - (_blockTree.BestSuggested?.TotalDifficulty ?? UInt256.Zero) <= 2 && info.SyncPeer.ClientId.Contains("Parity"))
                {
                    // Parity advertises a better block but never sends it back and then it disconnects after a few conversations like this
                    // Geth responds all fine here
                    // note this is only 2 difficulty difference which means that is just for the POA / Clique chains
                    continue;
                }

                long latency = _stats.GetOrAdd(info.SyncPeer.Node).GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100000;

                if (latency <= bestPeer.Latency)
                {
                    bestPeer = (info, latency);
                }
            }

            if (bestPeer.Info == null)
            {
                if (_logger.IsTrace) _logger.Trace($"[{reason}] No peer found for ETH sync");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"[{reason}] Best ETH sync peer: {bestPeer.Info} | BlockHeaderAvLatency: {bestPeer.Latency}");
            }

            return bestPeer.Info;
        }

        private void ReplaceIfWorthReplacing(SyncPeerAllocation allocation, PeerInfo peerInfo)
        {
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

            var currentLatency = _stats.GetOrAdd(allocation.Current?.SyncPeer.Node)?.GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100000;
            var newLatency = _stats.GetOrAdd(peerInfo.SyncPeer.Node)?.GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100001;

            if (newLatency / (decimal) Math.Max(1L, currentLatency) < 1m - _syncConfig.MinDiffPercentageForLatencySwitch / 100m
                && newLatency < currentLatency - _syncConfig.MinDiffForLatencySwitch)
            {
                if (_logger.IsInfo) _logger.Info($"Sync allocation - replacing {allocation.Current} with {peerInfo} - previous latency {currentLatency} vs new latency {newLatency}");
                allocation.ReplaceCurrent(peerInfo);
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Staying with current peer {allocation.Current} (ignoring {peerInfo}) - latency {currentLatency} vs {newLatency}");
            }
        }

        private void UpdateAllocations(string reason)
        {
            foreach ((SyncPeerAllocation allocation, _) in _allocations)
            {
                var bestPeer = SelectBestPeerForAllocation(allocation, reason);
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

        public SyncPeerAllocation Allocate(string description)
        {
            SyncPeerAllocation allocation = new SyncPeerAllocation(description);
            PeerInfo bestPeer = SelectBestPeerForAllocation(allocation, "BORROW");
            if (bestPeer != null)
            {
                allocation.ReplaceCurrent(bestPeer);
            }

            _allocations.TryAdd(allocation, null);
            return allocation;
        }

        public void ReportNoSyncProgress(SyncPeerAllocation allocation)
        {
            PeerInfo peer = allocation?.Current;
            if (peer == null)
            {
                return;
            }

            // this is generally with the strange Parity nodes behaviour
            if (_logger.IsDebug) _logger.Debug($"No sync progress reported with {allocation.Current}");
            _sleepingPeers.TryAdd(peer, DateTime.UtcNow);
        }

        public void Free(SyncPeerAllocation syncPeerAllocation)
        {
            if (_logger.IsTrace) _logger.Trace($"Returning {syncPeerAllocation}");
            _allocations.TryRemove(syncPeerAllocation, out _);
            syncPeerAllocation.Cancel();
        }
    }
}