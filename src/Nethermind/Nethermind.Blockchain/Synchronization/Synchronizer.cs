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
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization.FastBlocks;
using Nethermind.Blockchain.Synchronization.FastSync;
using Nethermind.Blockchain.Synchronization.TotalSync;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Blockchain.Synchronization
{
    public class Synchronizer : ISynchronizer
    {
        private const int _syncTimerInterval = 1000;

        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISyncConfig _syncConfig;
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly INodeDataDownloader _nodeDataDownloader;
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly ILogManager _logManager;
        private readonly ISyncReport _syncReport;

        private readonly BlockDownloader _blockDownloader;
        private readonly BlockDownloader _fastSyncBlockDownloader;

        private ManualResetEventSlim _syncRequested = new ManualResetEventSlim(false);
        private CancellationTokenSource _syncLoopCancellation = new CancellationTokenSource();
        private SyncPeerAllocation _blocksSyncAllocation;
        private System.Timers.Timer _syncTimer;
        private ISyncModeSelector _syncMode;
        private bool _cancelDueToBetterPeer;
        private Task _syncLoopTask;

        /* sync events are used mainly for managing sync peers reputation */
        public event EventHandler<SyncEventArgs> SyncEvent;

        private CancellationTokenSource _fastBlocksCancellation;

        public Synchronizer(
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            IEthSyncPeerPool peerPool,
            ISyncConfig syncConfig,
            INodeDataDownloader nodeDataDownloader,
            INodeStatsManager nodeStatsManager,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncPeerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _nodeDataDownloader = nodeDataDownloader ?? throw new ArgumentNullException(nameof(nodeDataDownloader));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _logManager = logManager;

            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(_blockTree, receiptStorage, _nodeDataDownloader, syncConfig, logManager);
            _syncMode = new SyncModeSelector(syncProgressResolver, _syncPeerPool, _syncConfig, logManager);
            _syncReport = new SyncReport(_syncPeerPool, _nodeStatsManager, syncConfig, syncProgressResolver, _syncMode, logManager);
            _syncPeerPool.PeerAdded += (sender, args) => RequestSynchronization(SyncTriggerType.PeerAdded);

            if (syncConfig.FastBlocks)
            {
                _fastBlocksCancellation = new CancellationTokenSource();

                BlockDownloaderOptions options = BlockDownloaderOptions.MoveToMain;
                if (_syncConfig.DownloadReceiptsInFastSync)
                {
                    options |= BlockDownloaderOptions.DownloadReceipts;
                }

                _fastSyncBlockDownloaderFeed = new BlockDownloaderFeed(options, _syncConfig.DownloadBodiesInFastSync, _syncConfig.BeamSync ? 0 : SyncModeSelector.FullSyncThreshold);
                _fastSyncBlockDownloader = new BlockDownloader(_fastSyncBlockDownloaderFeed, peerPool, _blockTree, blockValidator, sealValidator, _syncReport, receiptStorage, specProvider, logManager);
                _fastSyncBlockDownloader.Start(_syncLoopCancellation.Token);
            }

            _fullSyncBlockDownloaderFeed = new BlockDownloaderFeed(BlockDownloaderOptions.None, true, 0);
            _blockDownloader = new BlockDownloader(_fullSyncBlockDownloaderFeed, peerPool, _blockTree, blockValidator, sealValidator, _syncReport, receiptStorage, specProvider, logManager);
            _blockDownloader.Start(_syncLoopCancellation.Token);
        }

        public SyncMode SyncMode => _syncMode.Current;

        public event EventHandler<SyncModeChangedEventArgs> SyncModeChanged
        {
            add => _syncMode.Changed += value;
            remove => _syncMode.Changed -= value;
        }

        public void Start()
        {
            var fastFactory = new FastBlockPeerSelectionStrategyFactory();

            FastHeadersSyncFeed feed = new FastHeadersSyncFeed(_blockTree, _syncPeerPool, _syncConfig, _syncReport, _logManager);
            var fastBlockDownloader = new HeadersSyncExecutor(feed, _syncPeerPool, fastFactory, _logManager);
            Task headersTask = fastBlockDownloader.Start(_fastBlocksCancellation.Token);

            FastBodiesSyncFeed bodiesFeed = new FastBodiesSyncFeed(_blockTree, _syncPeerPool, _syncConfig, _syncReport, _logManager);
            var bodiesDownloader = new BodiesSyncExecutor(bodiesFeed, _syncPeerPool, fastFactory, _logManager);
            Task bodiesTask = bodiesDownloader.Start(_fastBlocksCancellation.Token);

            FastReceiptsSyncFeed receiptsFeed = new FastReceiptsSyncFeed(_specProvider, _blockTree, _receiptStorage, _syncPeerPool, _syncConfig, _syncReport, _logManager);
            var receiptsDownloader = new ReceiptsSyncExecutor(receiptsFeed, _syncPeerPool, fastFactory, _logManager);
            Task receiptsTask = receiptsDownloader.Start(_fastBlocksCancellation.Token);

            feed.Activate();
            bodiesFeed.Activate();
            receiptsFeed.Activate();
            
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
                        case { } t when t.IsFaulted:
                            if (_logger.IsError) _logger.Error("Sync loop encountered an exception.", t.Exception);
                            break;
                        case { } t when t.IsCanceled:
                            if (_logger.IsInfo) _logger.Info("Sync loop canceled.");
                            break;
                        case { } t when t.IsCompletedSuccessfully:
                            if (_logger.IsInfo) _logger.Info("Sync loop completed successfully.");
                            break;
                        default:
                            if (_logger.IsInfo) _logger.Info("Sync loop completed.");
                            break;
                    }
                });

            StartSyncTimer();
        }

        public async Task StopAsync()
        {
            StopSyncTimer();

            _syncLoopCancellation?.Cancel();

            await (_syncLoopTask ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Sync stopped");
        }

        private BlockDownloaderFeed _fastSyncBlockDownloaderFeed;
        private BlockDownloaderFeed _fullSyncBlockDownloaderFeed;

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
                string message = $"Requesting synchronization [{syncTriggerType.ToString().ToUpperInvariant()}]";
                if (syncTriggerType == SyncTriggerType.SyncTimer)
                {
                    _logger.Trace(message);
                }
                else
                {
                    _logger.Debug(message);
                }
            }

            // do this depending on the SyncMode!
            _fastSyncBlockDownloaderFeed?.Activate();
            _fullSyncBlockDownloaderFeed?.Activate();
            _syncRequested.Set();
        }

        private void StartSyncTimer()
        {
            if (_logger.IsDebug) _logger.Debug($"Starting sync timer with interval of {_syncTimerInterval}ms");
            _syncTimer = new System.Timers.Timer(_syncTimerInterval);
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

        private static bool RequiresBlocksSyncAllocation(SyncMode syncMode)
        {
            return syncMode == SyncMode.Beam || syncMode == SyncMode.Full || syncMode == SyncMode.FastSync;
        }

        private async Task RunSyncLoop()
        {
            while (true)
            {
                if (_logger.IsTrace) _logger.Trace("Sync loop - WAIT.");
                _syncRequested.Wait(_syncLoopCancellation.Token);
                _syncRequested.Reset();
                FreeBlocksSyncAllocation();

                if (_logger.IsTrace) _logger.Trace("Sync loop - IN.");
                if (_syncLoopCancellation.IsCancellationRequested)
                {
                    if (_logger.IsTrace) _logger.Trace("Sync loop cancellation requested - leaving the main sync loop.");
                    break;
                }

                if (!_blockTree.CanAcceptNewBlocks) continue;

                _syncMode.Update();

                if (RequiresBlocksSyncAllocation(_syncMode.Current))
                {
                    if (_blocksSyncAllocation == null)
                    {
                        long? minBlocksAhead = _syncMode.Current == SyncMode.FastSync ? SyncModeSelector.FullSyncThreshold : (long?) null;
                        await AllocateBlocksSync(minBlocksAhead);
                    }
                }

                PeerInfo bestPeer = null;
                if (_blocksSyncAllocation != null)
                {
                    UInt256 ourTotalDifficulty = _blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0;
                    bestPeer = _blocksSyncAllocation?.Current;
                    if (bestPeer == null || bestPeer.TotalDifficulty <= ourTotalDifficulty)
                    {
                        if (_logger.IsTrace) _logger.Trace("Skipping sync - no peer with better chain.");
                        continue;
                    }

                    SyncEvent?.Invoke(this, new SyncEventArgs(bestPeer.SyncPeer, Synchronization.SyncEvent.Started));
                    if (_logger.IsDebug) _logger.Debug($"Starting {_syncMode.Current} sync with {bestPeer} - theirs {bestPeer?.HeadNumber} {bestPeer?.TotalDifficulty} | ours {_blockTree.BestSuggestedHeader?.Number ?? 0} {_blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0}");
                }

                Task<long> syncProgressTask;
                switch (_syncMode.Current)
                {
                    case SyncMode.FastBlocks:
                        // fast blocks task
                        break;
                    case SyncMode.FastSync:
                        // block downloader
                        break;
                    case SyncMode.StateNodes:
                        syncProgressTask = DownloadStateNodes(_syncLoopCancellation.Token);
                        break;
                    case SyncMode.WaitForProcessor:
                        syncProgressTask = Task.Delay(5000).ContinueWith(_ => 0L);
                        break;
                    case SyncMode.Full:
                        // block downloader
                        break;
                    case SyncMode.Beam:
                        // block downloader
                        break;
                    case SyncMode.NotStarted:
                        syncProgressTask = Task.Delay(1000).ContinueWith(_ => 0L);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                // switch (_syncMode.Current)
                // {
                //     case SyncMode.WaitForProcessor:
                //         if (previousSyncMode != _syncMode.Current)
                //         {
                //             if (_logger.IsInfo) _logger.Info("Waiting for the block processor to catch up before the next sync round...");
                //         }
                //
                //         await syncProgressTask;
                //         break;
                //     case SyncMode.NotStarted:
                //         if (previousSyncMode != _syncMode.Current)
                //         {
                //             if (_logger.IsInfo) _logger.Info("Waiting for peers to connect before selecting the sync mode...");
                //         }
                //
                //         await syncProgressTask;
                //         break;
                //     default:
                //         await syncProgressTask.ContinueWith(t => HandleSyncRequestResult(t, bestPeer));
                //         break;
                // }
                //
                // if (syncProgressTask.IsCompletedSuccessfully)
                // {
                //     long progress = syncProgressTask.Result;
                //     if (progress == 0 && _blocksSyncAllocation != null)
                //     {
                //         _syncPeerPool.ReportNoSyncProgress(_blocksSyncAllocation); // not very fair here - allocation may have changed
                //     }
                // }
                //
                // FreeBlocksSyncAllocation();
                //
                // var source = _peerSyncCancellation;
                // _peerSyncCancellation = null;
                // source?.Dispose();
            }
        }

        private void HandleSyncRequestResult(Task<long> task, PeerInfo peerInfo)
        {
            switch (task)
            {
                case { } t when t.IsFaulted:
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
                case { } t when t.IsCanceled:
                    if (_cancelDueToBetterPeer)
                    {
                        _cancelDueToBetterPeer = false;
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
                case { } t when t.IsCompletedSuccessfully:
                    if (_logger.IsDebug) _logger.Debug($"{_syncMode.Current} sync with {peerInfo} completed with progress {t.Result}.");
                    if (peerInfo != null) // fix this for node data sync
                    {
                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, Synchronization.SyncEvent.Completed));
                    }

                    break;
            }
        }

        private async Task AllocateBlocksSync(long? minBlocksAhead)
        {
            if (_blocksSyncAllocation == null)
            {
                if (_logger.IsDebug) _logger.Debug("Allocating block sync.");
                _blocksSyncAllocation = await _syncPeerPool.Borrow(new BlocksSyncPeerSelectionStrategy(minBlocksAhead));
                _blocksSyncAllocation.Replaced += AllocationOnReplaced;
                _blocksSyncAllocation.Refreshed += AllocationOnRefreshed;
            }
        }

        private void AllocationOnRefreshed(object sender, EventArgs e)
        {
            RequestSynchronization(SyncTriggerType.PeerRefresh);
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
                _cancelDueToBetterPeer = true;
                // _peerSyncCancellation?.Cancel();
            }

            PeerInfo newPeer = e.Current;
            BlockHeader bestSuggested = _blockTree.BestSuggestedHeader;
            if (newPeer.TotalDifficulty > bestSuggested.TotalDifficulty)
            {
                RequestSynchronization(SyncTriggerType.PeerChange);
            }
        }

        private async Task<long> DownloadStateNodes(CancellationToken cancellation)
        {
            BlockHeader bestSuggested = _blockTree.BestSuggestedHeader;
            if (bestSuggested == null)
            {
                return 0;
            }

            if (_logger.IsInfo) _logger.Info($"Starting the node data sync from the {bestSuggested.ToString(BlockHeader.Format.Short)} {bestSuggested.StateRoot} root");
            return await _nodeDataDownloader.SyncNodeData(cancellation, bestSuggested.Number, bestSuggested.StateRoot);
        }

        public void Dispose()
        {
            _fastBlocksCancellation.Cancel();
            FreeBlocksSyncAllocation();

            _syncTimer?.Dispose();
            _syncLoopTask?.Dispose();
            _syncLoopCancellation?.Dispose();
            _syncRequested?.Dispose();
            _syncReport?.Dispose();
        }

        private void FreeBlocksSyncAllocation()
        {
            if (_blocksSyncAllocation != null)
            {
                // _blocksSyncAllocation.Cancelled -= AllocationOnCancelled;
                _blocksSyncAllocation.Replaced -= AllocationOnReplaced;
                _blocksSyncAllocation.Refreshed -= AllocationOnRefreshed;
                _syncPeerPool.Free(_blocksSyncAllocation);
                _blocksSyncAllocation = null;
            }
        }
    }
}