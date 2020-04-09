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
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.TotalSync;

namespace Nethermind.Synchronization
{
    public class Synchronizer : ISynchronizer
    {
        private const int SyncTimerInterval = 1000;

        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly ISyncConfig _syncConfig;
        private readonly INodeDataFeed _nodeDataSyncFeed;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly ILogManager _logManager;
        private readonly ISyncReport _syncReport;

        private BlockDownloaderFeed _fullSyncBlockDownloaderFeed;
        private BlockDownloader _fullSyncBlockDownloader;

        private BlockDownloaderFeed _fastSyncBlockDownloaderFeed;
        private BlockDownloader _fastSyncBlockDownloader;

        private CancellationTokenSource _syncCancellation = new CancellationTokenSource();
        private System.Timers.Timer _syncTimer;

        /* sync events are used mainly for managing sync peers reputation */
        public event EventHandler<SyncEventArgs> SyncEvent;

        private ISyncModeSelector _syncMode;
        private ISyncExecutor<StateSyncBatch> _nodeDataSyncExecutor;

        public Synchronizer(ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ISyncPeerPool peerPool,
            ISyncConfig syncConfig,
            INodeDataFeed stateSyncFeed,
            ISyncExecutor<StateSyncBatch> nodeDataSyncExecutor,
            INodeStatsManager nodeStatsManager,
            ISyncModeSelector syncModeSelector,
            ILogManager logManager)
        {
            _syncMode = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _nodeDataSyncExecutor = nodeDataSyncExecutor ?? throw new ArgumentNullException(nameof(nodeDataSyncExecutor));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _nodeDataSyncFeed = stateSyncFeed ?? throw new ArgumentNullException(nameof(stateSyncFeed));
            _syncPeerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _logManager = logManager;

            _syncReport = new SyncReport(_syncPeerPool, _nodeStatsManager, _syncMode, syncConfig, logManager);
            _syncPeerPool.PeerAdded += (sender, args) => RequestSynchronization(SyncTriggerType.PeerAdded);
        }

        private void StartFullSyncComponents()
        {
            _fullSyncBlockDownloaderFeed = new BlockDownloaderFeed(DownloaderOptions.WithBodies);
            _fullSyncBlockDownloader = new BlockDownloader(_fullSyncBlockDownloaderFeed, _syncPeerPool, _blockTree, _blockValidator, _sealValidator, _syncReport, _receiptStorage, _specProvider, _logManager);
            _fullSyncBlockDownloader.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("Full sync block downloader failed", t.Exception);
                }
            });
        }

        public void Start()
        {
            // StartFullSyncComponents();
            if (_syncConfig.FastBlocks)
            {
                StartFastBlocksComponents();
            }

            if (_syncConfig.FastSync)
            {
                StartFastSyncComponents();
                StartStateSyncComponents();
            }

            StartSyncTimer();
        }

        private void StartStateSyncComponents()
        {
            Task nodeDataSyncTask = _nodeDataSyncExecutor.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("State sync failed", t.Exception);
                }
            });
        }

        private void StartFastBlocksComponents()
        {
            FastBlockPeerSelectionStrategyFactory fastFactory = new FastBlockPeerSelectionStrategyFactory();

            FastHeadersSyncFeed headersSyncFeed = new FastHeadersSyncFeed(_blockTree, _syncPeerPool, _syncConfig, _syncReport, _logManager);
            var fastBlockDownloader = new HeadersSyncExecutor(headersSyncFeed, _syncPeerPool, fastFactory, _logManager);
            Task headersTask = fastBlockDownloader.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("Fast blocks headers downloader failed", t.Exception);
                }
            });

            headersSyncFeed.Activate();

            FastBodiesSyncFeed bodiesSyncFeed = new FastBodiesSyncFeed(_blockTree, _syncPeerPool, _syncConfig, _syncReport, _logManager);
            var bodiesDownloader = new BodiesSyncExecutor(bodiesSyncFeed, _syncPeerPool, fastFactory, _logManager);
            Task bodiesTask = bodiesDownloader.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("Fast blocks bodies downloader failed", t.Exception);
                }
            });

            bodiesSyncFeed.Activate();

            FastReceiptsSyncFeed receiptsSyncFeed = new FastReceiptsSyncFeed(_specProvider, _blockTree, _receiptStorage, _syncPeerPool, _syncConfig, _syncReport, _logManager);
            var receiptsDownloader = new ReceiptsSyncExecutor(receiptsSyncFeed, _syncPeerPool, fastFactory, _logManager);
            Task receiptsTask = receiptsDownloader.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("Fast blocks receipts downloader failed", t.Exception);
                }
            });

            receiptsSyncFeed.Activate();
        }

        private void StartFastSyncComponents()
        {
            DownloaderOptions options = BuildFastSyncOptions();
            _fastSyncBlockDownloaderFeed = new BlockDownloaderFeed(options, _syncConfig.BeamSync ? 0 : SyncModeSelector.FullSyncThreshold);
            _fastSyncBlockDownloader = new BlockDownloader(_fastSyncBlockDownloaderFeed, _syncPeerPool, _blockTree, _blockValidator, _sealValidator, _syncReport, _receiptStorage, _specProvider, _logManager);
            _fastSyncBlockDownloader.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("Fast sync block downloader failed", t.Exception);
                }
            });
        }

        private DownloaderOptions BuildFastSyncOptions()
        {
            DownloaderOptions options = DownloaderOptions.MoveToMain;
            if (_syncConfig.DownloadReceiptsInFastSync)
            {
                options |= DownloaderOptions.WithReceipts;
            }

            if (_syncConfig.DownloadBodiesInFastSync)
            {
                options |= DownloaderOptions.WithBodies;
            }

            return options;
        }

        public async Task StopAsync()
        {
            StopSyncTimer();

            _syncCancellation?.Cancel();

            if (_logger.IsInfo) _logger.Info("Sync stopped");
            await Task.CompletedTask;
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

            _fastSyncBlockDownloaderFeed?.Activate();
            // _fullSyncBlockDownloaderFeed?.Activate();

            // DownloadStateNodes(_syncCancellation.Token);
        }

        private void StartSyncTimer()
        {
            if (_logger.IsDebug) _logger.Debug($"Starting sync timer with interval of {SyncTimerInterval}ms");
            _syncTimer = new System.Timers.Timer(SyncTimerInterval);
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
                _syncMode.Update();
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

        private void DownloadStateNodes(CancellationToken cancellation)
        {
            BlockHeader bestSuggested = _blockTree.BestSuggestedHeader;
            if (bestSuggested == null || bestSuggested.Number == 0)
            {
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Starting the node data sync from the {bestSuggested.ToString(BlockHeader.Format.Short)} {bestSuggested.StateRoot} root");
            _nodeDataSyncFeed.SetNewStateRoot(bestSuggested.Number, bestSuggested.StateRoot);
            _nodeDataSyncFeed.Activate();
        }

        public void Dispose()
        {
            _syncTimer?.Dispose();
            _syncCancellation?.Dispose();
            _syncReport?.Dispose();
        }
    }
}