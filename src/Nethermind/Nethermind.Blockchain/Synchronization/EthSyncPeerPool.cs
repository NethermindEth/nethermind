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
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
        private readonly INodeStatsManager _stats;
        private readonly ISyncConfig _syncConfig;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<PublicKey, PeerInfo> _peers = new ConcurrentDictionary<PublicKey, PeerInfo>();
        private readonly BlockingCollection<PeerInfo> _peerRefreshQueue = new BlockingCollection<PeerInfo>();
        private CancellationTokenSource _mainLoopCancellationTokenSource = new CancellationTokenSource();

        private Task _initPeerLoopTask;
        
        private ConcurrentBag<SyncPeerAllocation> _allocations = new ConcurrentBag<SyncPeerAllocation>();
        private readonly ConcurrentDictionary<PublicKey, CancellationTokenSource> _initCancelTokens = new ConcurrentDictionary<PublicKey, CancellationTokenSource>();
        
        public EthSyncPeerPool(INodeStatsManager nodeStatsManager, ISyncConfig syncConfig, ILogManager logManager)
        {
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        private async Task RunRefreshPeerLoop()
        {
            try
            {
                foreach (PeerInfo peerInfo in _peerRefreshQueue.GetConsumingEnumerable(_mainLoopCancellationTokenSource.Token))
                {
                    if (_logger.IsTrace) _logger.Trace($"Running refresh peer info for {peerInfo}.");
                    var initCancelSource = _initCancelTokens[peerInfo.SyncPeer.Node.Id] = new CancellationTokenSource();
                    var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(initCancelSource.Token, _mainLoopCancellationTokenSource.Token);
                    await RefreshPeerInfo(peerInfo, linkedSource.Token).ContinueWith(t =>
                    {
                        _initCancelTokens.TryRemove(peerInfo.SyncPeer.Node.Id, out _);
                        if (t.IsFaulted)
                        {
                            if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                            {
                                if (_logger.IsTrace) _logger.Trace($"AddPeer failed due to timeout: {t.Exception.Message}");
                            }
                            else if (_logger.IsDebug) _logger.Debug($"AddPeer failed {t.Exception}");
                        }
                        else if (t.IsCanceled)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Init peer info canceled: {peerInfo.SyncPeer.Node:s}");
                        }
                        else
                        {
                            CancelCurrentPeerSyncIfWorse(peerInfo, ComparedPeerType.New);
                            if (peerInfo.TotalDifficulty > _blockTree.BestSuggested.TotalDifficulty)
                            {
                                _syncRequested.Set();
                            }
                            else if (peerInfo.TotalDifficulty == _blockTree.BestSuggested.TotalDifficulty
                                     && peerInfo.HeadHash != _blockTree.BestSuggested.Hash)
                            {
                                Block block = _blockTree.FindBlock(_blockTree.BestSuggested.Hash, false);
                                peerInfo.SyncPeer.SendNewBlock(block);
                                if (_logger.IsDebug) _logger.Debug($"Sending my best block {block} to {peerInfo}");
                            }
                        }

                        initCancelSource.Dispose();
                        linkedSource.Dispose();
                    });
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                if (_logger.IsError) _logger.Error($"Init peer loop encountered an exception {e}");
            }

            if (_logger.IsError) _logger.Error($"Exiting the peer loop");
        }
        
        public void Start()
        {
            _initPeerLoopTask = Task.Factory.StartNew(
                RunRefreshPeerLoop,
                _mainLoopCancellationTokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ContinueWith(t =>
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
                    if (_logger.IsDebug) _logger.Debug("Init peer loop complete.");
                }
            });
        }

        public async Task StopAsync()
        {
            await (_initPeerLoopTask ?? Task.CompletedTask);
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
                        if (_logger.IsTrace) _logger.Trace($"InitPeerInfo failed for node: {syncPeer.Node:s}{Environment.NewLine}{t.Exception}");
                        RemovePeer(syncPeer);
                        SyncEvent?.Invoke(this, new SyncEventArgs(syncPeer, peerInfo.IsInitialized ? SyncStatus.Failed : SyncStatus.InitFailed));
                    }
                    else if (firstToComplete.IsCanceled)
                    {
                        RemovePeer(syncPeer);
                        SyncEvent?.Invoke(this, new SyncEventArgs(syncPeer, peerInfo.IsInitialized ? SyncStatus.Cancelled : SyncStatus.InitCancelled));
                        token.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"Received head block info from {syncPeer.Node:s} with head block numer {getHeadHeaderTask.Result}");
                        if (!peerInfo.IsInitialized)
                        {
                            SyncEvent?.Invoke(
                                this,
                                new SyncEventArgs(syncPeer, SyncStatus.InitCompleted)
                                {
                                    NodeBestBlockNumber = getHeadHeaderTask.Result.Number,
                                    OurBestBlockNumber = _blockTree.BestKnownNumber
                                });
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

        public int PeerCount => _peers.Count;

        public void Refresh(PeerInfo peerInfo)
        {
            _peerRefreshQueue.Add(peerInfo);
        }

        public void AddPeer(ISyncPeer syncPeer)
        {
            if (_logger.IsDebug) _logger.Debug($"|NetworkTrace| Adding synchronization peer {syncPeer.Node:f}");


            if (_peers.ContainsKey(syncPeer.Node.Id))
            {
                if (_logger.IsDebug) _logger.Debug($"Sync peer already in peers collection: {syncPeer.Node:c}");
                return;
            }

            var peerInfo = new PeerInfo(syncPeer);
            _peers.TryAdd(syncPeer.Node.Id, peerInfo);
            Metrics.SyncPeers = _peers.Count;

            _peerRefreshQueue.Add(peerInfo);
        }

        public void RemovePeer(ISyncPeer syncPeer)
        {
            if (_logger.IsDebug) _logger.Debug($"Removing synchronization peer {syncPeer.Node:c}");
            if (!_isInitialized)
            {
                if (_logger.IsDebug) _logger.Debug($"Synchronization is disabled, removing peer is blocked: {syncPeer.Node:s}");
                return;
            }

            if (!_peers.TryRemove(syncPeer.Node.Id, out var peerInfo))
            {
                //possible if sync failed - we remove peer and eventually initiate disconnect, which calls remove peer again
                return;
            }

            Metrics.SyncPeers = _peers.Count;

            if (_currentSyncingPeerInfo?.SyncPeer.Node.Id.Equals(syncPeer.Node.Id) ?? false)
            {
                if (_logger.IsTrace) _logger.Trace($"Requesting peer cancel with: {syncPeer.Node:s}");
                _peerSyncCancellationTokenSource?.Cancel();
            }

            if (_initCancelTokens.TryGetValue(syncPeer.Node.Id, out CancellationTokenSource initCancelTokenSource))
            {
                initCancelTokenSource?.Cancel();
            }
        }

        private PeerInfo SelectBestPeerForSync(UInt256 totalDifficultyThreshold)
        {
            (PeerInfo Info, long Latency) bestPeer = (null, 100000);
            foreach ((_, PeerInfo info)in _peers)
            {
                if (!info.IsInitialized || info.TotalDifficulty <= totalDifficultyThreshold)
                {
                    continue;
                }

                long latency = _stats.GetOrAdd(info.SyncPeer.Node).GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100000;
                if (_logger.IsDebug) _logger.Debug($"Candidate for sync: {info} | BlockHeaderAvLatency: {latency.ToString() ?? "none"}");

                if (latency <= bestPeer.Latency)
                {
                    bestPeer = (info, latency);
                }
            }

            if (bestPeer.Info?.SyncPeer.Node.Id == _currentSyncingPeerInfo?.SyncPeer?.Node.Id)
            {
                if (_logger.IsDebug) _logger.Debug($"Potential error, selecting same peer for sync as prev sync peer, id: {bestPeer.Info}");
            }

            return bestPeer.Info;
        }

        private void CheckIfSyncingWithFastestPeer()
        {
            (PeerInfo Info, long Latency) bestPeer = (null, 100000);
            foreach ((_, PeerInfo info) in _peers)
            {
                if (!info.IsInitialized || info.TotalDifficulty <= _blockTree.BestSuggested.TotalDifficulty)
                {
                    continue;
                }

                long latency = _stats.GetOrAdd(info.SyncPeer.Node).GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100000;
                if (latency <= bestPeer.Latency)
                {
                    bestPeer = (info, latency);
                }
            }

            if (bestPeer.Info != null && _currentSyncingPeerInfo != null && _currentSyncingPeerInfo.SyncPeer?.Node.Id != bestPeer.Info.SyncPeer?.Node.Id)
            {
                if (_logger.IsTrace) _logger.Trace("Checking if any available peer is faster than current sync peer");
                CancelCurrentPeerSyncIfWorse(bestPeer.Info, ComparedPeerType.Existing);
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"NotSyncing or Syncing with fastest peer: bestLatencyPeer: {bestPeer.Info?.ToString() ?? "none"}, currentSyncingPeer: {_currentSyncingPeerInfo?.ToString() ?? "none"}");
            }
        }
        
        private enum ComparedPeerType
        {
            New,
            Existing
        }

        private void CancelCurrentPeerSyncIfWorse(PeerInfo peerInfo, ComparedPeerType comparedPeerType)
        {
            // todo - if not any that are syncing now? just replace anyway
            if(!_allocations.Any())
            {
                return;
            }

            //As we deal with UInt256 if we subtract bigger value from smaller value we get very big value as a result (overflow) which is incorrect (unsigned)
            BigInteger chainLengthDiff = peerInfo.HeadNumber > _blockTree.BestKnownNumber ? peerInfo.HeadNumber - _blockTree.BestKnownNumber : 0;
            chainLengthDiff = BigInteger.Max(chainLengthDiff, (peerInfo.TotalDifficulty - (BigInteger) (_blockTree.BestSuggested?.TotalDifficulty ?? UInt256.Zero)) / (_blockTree.BestSuggested?.Difficulty ?? 1));
            if (chainLengthDiff < _syncConfig.MinAvailableBlockDiffForSyncSwitch)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping latency switch due to lower chain length diff than threshold - chain length diff: {chainLengthDiff}, threshold: {_syncConfig.MinAvailableBlockDiffForSyncSwitch}");
                return;
            }


            var currentSyncPeerLatency = _stats.GetOrAdd(_currentSyncingPeerInfo?.SyncPeer?.Node).GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100000;
            var newPeerLatency = _stats.GetOrAdd(peerInfo.SyncPeer.Node).GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100001;
            if (currentSyncPeerLatency - newPeerLatency >= _syncConfig.MinLatencyDiffForSyncSwitch)
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"{comparedPeerType} peer with better latency, requesting cancel for current sync process{Environment.NewLine}" +
                                  $"{comparedPeerType} {peerInfo}, Latency: {newPeerLatency}{Environment.NewLine}" +
                                  $"Current peer: {_currentSyncingPeerInfo}, Latency: {currentSyncPeerLatency}, Best Known: {_blockTree.BestKnownNumber}, Available @ Peer: {peerInfo.HeadNumber}");
                }

                _requestedSyncCancelDueToBetterPeer = true;
                _peerSyncCancellationTokenSource?.Cancel();
            }
            else
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"{comparedPeerType} peer with worse latency{Environment.NewLine}" +
                                  $"{comparedPeerType} {peerInfo}, Latency: {newPeerLatency}{Environment.NewLine}" +
                                  $"Current {_currentSyncingPeerInfo}, Latency: {currentSyncPeerLatency}");
                }
            }
        }

        private bool HasAnyPeersToSyncWith(UInt256 difficultyThreshold)
        {
            foreach (KeyValuePair<PublicKey, PeerInfo> peer in _peers)
            {
                if (peer.Value.TotalDifficulty > difficultyThreshold)
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryFind(PublicKey nodeId, out PeerInfo peerInfo)
        {
            return _peers.TryGetValue(nodeId, out peerInfo);
        }

        public SyncPeerAllocation GetBest(bool exclusive = false)
        {
            throw new NotImplementedException();
        }

        public void Return(SyncPeerAllocation syncPeerAllocation)
        {
            throw new NotImplementedException();
        }
        
        public event EventHandler<SyncEventArgs> SyncEvent;
    }
}