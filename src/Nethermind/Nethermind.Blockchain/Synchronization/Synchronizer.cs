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
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Mining;
using Nethermind.Stats.Model;

namespace Nethermind.Blockchain.Synchronization
{
    public class Synchronizer : ISynchronizer
    {
        private readonly ILogger _logger;
        private readonly ISyncConfig _syncConfig;
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly INodeDataDownloader _nodeDataDownloader;
        private readonly IBlockTree _blockTree;
        private readonly BlockDownloader _blockDownloader;

        private System.Timers.Timer _syncTimer;
        private SyncPeerAllocation _blocksSyncAllocation;
        private Task _syncLoopTask;
        private CancellationTokenSource _syncLoopCancellation = new CancellationTokenSource();
        private CancellationTokenSource _peerSyncCancellation;
        private bool _requestedSyncCancelDueToBetterPeer;
        private readonly ManualResetEventSlim _syncRequested = new ManualResetEventSlim(false);
        private SyncModeSelector _syncMode;
        private long _bestSuggestedNumber => _blockTree.BestSuggested?.Number ?? 0;

        /* sync events are used mainly for managing sync peers reputation */
        public event EventHandler<SyncEventArgs> SyncEvent;

        public Synchronizer(IBlockTree blockTree,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            IEthSyncPeerPool peerPool,
            ISyncConfig syncConfig,
            INodeDataDownloader nodeDataDownloader,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _nodeDataDownloader = nodeDataDownloader ?? throw new ArgumentNullException(nameof(nodeDataDownloader));
            _syncPeerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));

            _syncMode = new SyncModeSelector(_syncPeerPool, _syncConfig, logManager);
            _syncMode.Changed += (s, e) => RequestSynchronization(SyncTriggerType.SyncModeChange);
            _syncMode.Changed += (s, e) =>
            {
                if (_blocksSyncAllocation == null && _syncMode.Current != SyncMode.StateNodes)
                {
                    AllocateBlocksSync();
                }

                if (_syncMode.Current == SyncMode.StateNodes)
                {
                    FreeBlocksSyncAllocation();
                }
            };

            // make ctor parameter?
            _blockDownloader = new BlockDownloader(_blockTree, blockValidator, sealValidator, logManager);
        }

        public SyncMode SyncMode => _syncMode.Current;

        public void Start()
        {
            AllocateBlocksSync();

            // Task.Run may cause trouble - make sure to test it well if planning to uncomment 
            // _syncLoopTask = Task.Run(RunSyncLoop, _syncLoopCancelTokenSource.Token) 
            _syncLoopTask = Task.Factory.StartNew(
                    RunSyncLoop,
                    _syncLoopCancellation.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap()
                .ContinueWith(task =>
                {
                    switch (task)
                    {
                        case Task t when t.IsFaulted:
                            if (_logger.IsError) _logger.Error("Fast sync loop encountered an exception.", t.Exception);
                            break;
                        case Task t when t.IsCanceled:
                            if (_logger.IsInfo) _logger.Info("Fast sync loop canceled.");
                            break;
                        case Task t when t.IsCompletedSuccessfully:
                            if (_logger.IsInfo) _logger.Info("Fast sync loop completed successfully.");
                            break;
                        default:
                            if (_logger.IsInfo) _logger.Info("Fast sync loop completed.");
                            break;
                    }
                });

            StartSyncTimer();
        }

        public async Task StopAsync()
        {
            StopSyncTimer();

            _peerSyncCancellation?.Cancel(); // not needed since we are using linked source now?
            _syncLoopCancellation?.Cancel();

            await (_syncLoopTask ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Fast sync stopped");
        }

        /// <summary>
        /// Notifies synchronizer that an event occured that should trigger synchronization
        /// at the nearest convenient time.
        /// </summary>
        /// <param name="syncTriggerType">Reason for the synchronization request for logging</param>
        public void RequestSynchronization(SyncTriggerType syncTriggerType)
        {
            if (!_blockTree.CanAcceptNewBlocks)
            {
                return;
            }

            if (_logger.IsDebug)
            {
                string message = $"Requesting synchronization [{syncTriggerType.ToString().ToUpperInvariant()}";
                if (syncTriggerType == SyncTriggerType.SyncTimer)
                {
                    _logger.Trace(message);
                }
                else
                {
                    _logger.Debug(message);
                }
            }

            _syncRequested.Set();
        }

        private void StartSyncTimer()
        {
            if (_logger.IsDebug) _logger.Debug($"Starting sync timer with interval of {_syncConfig.SyncTimerInterval}ms");
            _syncTimer = new System.Timers.Timer(_syncConfig.SyncTimerInterval);
            _syncTimer.Elapsed += SyncTimerOnElapsed;
            _syncTimer.Start();
        }

        private void StopSyncTimer()
        {
            try
            {
                if (_logger.IsDebug) _logger.Debug("Stopping sync timer");
                _syncTimer?.Stop();
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Error during the sync timer stop", e);
            }
        }

        private void SyncTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            _syncTimer.Enabled = false;

            try
            {
                RequestSynchronization(SyncTriggerType.SyncTimer);
            }
            catch (Exception ex)
            {
                if (_logger.IsDebug) _logger.Error("Sync timer failed", ex);
            }
            finally
            {
                _syncTimer.Enabled = true;
            }
        }

        private async Task RunSyncLoop()
        {
            while (true)
            {
                if (_logger.IsTrace) _logger.Trace("Fast sync loop - WAIT.");
                _syncRequested.Wait(_syncLoopCancellation.Token);
                if (_syncLoopCancellation.IsCancellationRequested)
                {
                    if (_logger.IsTrace) _logger.Trace("Sync loop cancellation requested - leaving the main sync loop.");
                    break;
                }

                _syncRequested.Reset();
                if (_logger.IsTrace) _logger.Trace("Sync loop - IN.");

                if (!_blockTree.CanAcceptNewBlocks) continue;

                PeerInfo bestPeer = null;
                if (_blocksSyncAllocation != null)
                {
                    UInt256 ourTotalDifficulty = _blockTree.BestSuggested?.TotalDifficulty ?? 0;
                    _syncPeerPool.EnsureBest();
                    bestPeer = _blocksSyncAllocation?.Current;
                    if (bestPeer == null || bestPeer.TotalDifficulty <= ourTotalDifficulty)
                    {
                        if (_logger.IsTrace) _logger.Trace("Skipping sync - no peer with better chain.");
                        continue;
                    }
                    
                    SyncEvent?.Invoke(this, new SyncEventArgs(bestPeer.SyncPeer, Synchronization.SyncEvent.Started));
                    if (_logger.IsDebug) _logger.Debug($"Starting {_syncMode.Current} sync with {bestPeer} - theirs {bestPeer?.HeadNumber} {bestPeer?.TotalDifficulty} | ours {_bestSuggestedNumber} {_blockTree.BestSuggested?.TotalDifficulty ?? 0}");
                }

                _peerSyncCancellation = new CancellationTokenSource();
                var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_peerSyncCancellation.Token, _syncLoopCancellation.Token);
                Task<long> syncProgressTask;
                switch (_syncMode.Current)
                {
                    case SyncMode.Headers:
                        syncProgressTask = _blockDownloader.DownloadHeaders(bestPeer, SyncModeSelector.FullSyncThreshold, linkedCancellation.Token);
                        break;
                    case SyncMode.StateNodes:
                        syncProgressTask = DownloadStateNodes(_syncLoopCancellation.Token);
                        break;
                    case SyncMode.Full:
                        syncProgressTask = _blockDownloader.DownloadBlocks(bestPeer, linkedCancellation.Token);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                await syncProgressTask.ContinueWith(t => HandleSyncRequestResult(t, bestPeer));
                long bestFullState = FindBestFullState();
                SyncMode beforeUpdate = _syncMode.Current;
                _syncMode.Update(_bestSuggestedNumber, bestFullState);
                if (_syncMode.Current == SyncMode.Headers)
                {
                    _blocksSyncAllocation.MinBlocksAhead = SyncModeSelector.FullSyncThreshold;
                }
                else if (_blocksSyncAllocation != null)
                {
                    _blocksSyncAllocation.MinBlocksAhead = null;
                }
                
                if (syncProgressTask.IsCompletedSuccessfully)
                {
                    long progress = syncProgressTask.Result;
                    if (_syncMode.Current == beforeUpdate && progress == 0 && _blocksSyncAllocation != null)
                    {
                        _syncPeerPool.ReportNoSyncProgress(_blocksSyncAllocation); // not very fair here - allocation may have changed
                    }
                }

                _blocksSyncAllocation?.FinishSync();

                linkedCancellation.Dispose();
                var source = _peerSyncCancellation;
                _peerSyncCancellation = null;
                source?.Dispose();
            }
        }

        private long FindBestFullState()
        {
            /* There is an interesting scenario (unlikely) here where we download more than 'full sync threshold'
             blocks in full sync but they are not processed immediately so we switch to node sync
             and the blocks that we downloaded are processed from their respective roots
             and the next full sync will be after a leap.
             This scenario is still correct. It may be worth to analyze what happens
             when it causes a full sync vs node sync race at every block.*/

            BlockHeader bestSuggested = _blockTree.BestSuggested;
            BlockHeader head = _blockTree.Head;
            long bestFullState = head?.Number ?? 0;
            long maxLookup = Math.Min(SyncModeSelector.FullSyncThreshold * 2, bestSuggested?.Number ?? 0L - bestFullState);

            for (int i = 0; i < maxLookup; i++)
            {
                if (bestSuggested == null)
                {
                    break;
                }

                Keccak stateRoot = bestSuggested.StateRoot;
                if (_nodeDataDownloader.IsFullySynced(stateRoot ?? Keccak.EmptyTreeHash))
                {
                    bestFullState = bestSuggested.Number;
                    break;
                }

                bestSuggested = _blockTree.FindHeader(bestSuggested.ParentHash);
            }

            return bestFullState;
        }

        private void HandleSyncRequestResult(Task task, PeerInfo peerInfo)
        {
            switch (task)
            {
                case Task t when t.IsFaulted:
                    string reason;
                    if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x is TimeoutException))
                    {
                        if (_logger.IsDebug) _logger.Debug($"{_syncMode.Current} sync with {peerInfo} timed out. {t.Exception?.Message}");
                        reason = "timeout";
                    }
                    else
                    {
                        if (_logger.IsDebug) _logger.Debug($"{_syncMode.Current} sync with {peerInfo} failed. {t.Exception}");
                        reason = "sync fault";
                    }

                    if (peerInfo != null) // fix this for node data sync
                    {
                        peerInfo.SyncPeer.Disconnect(DisconnectReason.DisconnectRequested, reason);
                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, Synchronization.SyncEvent.Failed));
                    }
                    
                    break;
                case Task t when t.IsCanceled:
                    if (_requestedSyncCancelDueToBetterPeer)
                    {
                        _requestedSyncCancelDueToBetterPeer = false;
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"{_syncMode.Current} sync with {peerInfo} canceled. Removing node from sync peers.");
                        if (peerInfo != null) // fix this for node data sync
                        {
                            SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, Synchronization.SyncEvent.Cancelled));
                        }
                    }

                    break;
                case Task t when t.IsCompletedSuccessfully:
                    if (_logger.IsDebug) _logger.Debug($"{_syncMode.Current} sync with {peerInfo} completed.");
                    if (peerInfo != null) // fix this for node data sync
                    {
                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, Synchronization.SyncEvent.Completed));
                    }

                    break;
            }
        }

        private void AllocateBlocksSync()
        {
            if (_blocksSyncAllocation == null)
            {
                if (_logger.IsDebug) _logger.Debug("Allocating block sync.");
                _blocksSyncAllocation = _syncPeerPool.Borrow("synchronizer");
                _blocksSyncAllocation.Replaced += AllocationOnReplaced;
                _blocksSyncAllocation.Cancelled += AllocationOnCancelled;
                _blocksSyncAllocation.Refreshed += AllocationOnRefreshed;
            }
        }

        private void AllocationOnRefreshed(object sender, EventArgs e)
        {
            RequestSynchronization(SyncTriggerType.PeerRefresh);
        }

        private void AllocationOnCancelled(object sender, AllocationChangeEventArgs e)
        {
            if (_logger.IsDebug) _logger.Debug($"Cancelling {e.Previous} on {_blocksSyncAllocation}.");
            _peerSyncCancellation?.Cancel();
        }

        private void AllocationOnReplaced(object sender, AllocationChangeEventArgs e)
        {
            if (e.Previous == null)
            {
                if (_logger.IsDebug) _logger.Debug($"Allocating {e.Current} on {_blocksSyncAllocation}.");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Replacing {e.Previous} with {e.Current} on {_blocksSyncAllocation}.");
            }

            if (e.Previous != null)
            {
                _requestedSyncCancelDueToBetterPeer = true;
                _peerSyncCancellation?.Cancel();
            }

            PeerInfo newPeer = e.Current;
            BlockHeader bestSuggested = _blockTree.BestSuggested;
            if (newPeer.TotalDifficulty > bestSuggested.TotalDifficulty)
            {
                RequestSynchronization(SyncTriggerType.PeerChange);
            }
        }

        private async Task<long> DownloadStateNodes(CancellationToken cancellation)
        {
            BlockHeader bestSuggested = _blockTree.BestSuggested;
            if (bestSuggested == null)
            {
                return 0;
            }

            if (_logger.IsInfo) _logger.Info($"Starting the node data sync from the {bestSuggested.ToString(BlockHeader.Format.Short)} {bestSuggested.StateRoot} root");
            return await _nodeDataDownloader.SyncNodeData(cancellation, bestSuggested.StateRoot);
        }

        public int HintMaxConcurrentRequests()
        {
            return _syncPeerPool.PeerCount;
        }

        public void Dispose()
        {
            FreeBlocksSyncAllocation();

            _syncTimer?.Dispose();
            _syncLoopTask?.Dispose();
            _syncLoopCancellation?.Dispose();
            _peerSyncCancellation?.Dispose();
            _syncRequested?.Dispose();
        }

        private void FreeBlocksSyncAllocation()
        {
            if (_blocksSyncAllocation != null)
            {
                _blocksSyncAllocation.Cancelled -= AllocationOnCancelled;
                _blocksSyncAllocation.Replaced -= AllocationOnReplaced;
                _blocksSyncAllocation.Refreshed -= AllocationOnRefreshed;
                _syncPeerPool.Free(_blocksSyncAllocation);
                _blocksSyncAllocation = null;
            }
        }
    }
}