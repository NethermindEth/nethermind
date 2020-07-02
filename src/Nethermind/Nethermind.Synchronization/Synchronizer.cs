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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Synchronization
{
    public class Synchronizer : ISynchronizer
    {
        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly ILogManager _logManager;
        private readonly ISyncReport _syncReport;

        private readonly CancellationTokenSource _syncCancellation = new CancellationTokenSource();

        /* sync events are used mainly for managing sync peers reputation */
        public event EventHandler<SyncEventArgs> SyncEvent;

        private readonly IDbProvider _dbProvider;
        private readonly ISyncModeSelector _syncMode;
        private FastSyncFeed _fastSyncFeed;
        private StateSyncFeed _stateSyncFeed;
        private FullSyncFeed _fullSyncFeed;
        private FastHeadersSyncFeed _headersFeed;
        private FastBodiesSyncFeed _bodiesFeed;
        private FastReceiptsSyncFeed _receiptsFeed;
        

        public Synchronizer(
            IDbProvider dbProvider,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ISyncPeerPool peerPool,
            INodeStatsManager nodeStatsManager,
            ISyncModeSelector syncModeSelector,
            ISyncConfig syncConfig,
            ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _syncMode = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncPeerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _logManager = logManager;

            _syncReport = new SyncReport(_syncPeerPool, _nodeStatsManager, _syncMode, syncConfig, logManager);
        }

        public void Start()
        {
            if (!_syncConfig.SynchronizationEnabled)
            {
                return;
            }
            
            StartFullSyncComponents();
            if (_syncConfig.FastSync)
            {
                if (_syncConfig.FastBlocks)
                {
                    StartFastBlocksComponents();
                }

                StartFastSyncComponents();
                StartStateSyncComponents();
            }

            if (_syncConfig.FastSync && _syncConfig.BeamSync || _syncConfig.BeamSyncFixMode)
            {
                StartBeamSyncComponents();
            }
        }

        private void StartBeamSyncComponents()
        {
            // so bad
            BeamSyncDbProvider beamSyncDbProvider = _dbProvider as BeamSyncDbProvider;
            ISyncFeed<StateSyncBatch> beamSyncFeed = beamSyncDbProvider.BeamSyncFeed;
            StateSyncDispatcher dispatcher = new StateSyncDispatcher(beamSyncFeed, _syncPeerPool, new StateSyncAllocationStrategyFactory(), _logManager);
            dispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Beam sync failed", t.Exception);
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info("Beam sync completed.");
                }
            });
        }

        public Task StopAsync()
        {
            _syncCancellation?.Cancel();
            return Task.CompletedTask;
        }

        private void StartFullSyncComponents()
        {
            _fullSyncFeed = new FullSyncFeed(_syncMode, LimboLogs.Instance);
            BlockDownloader fullSyncBlockDownloader = new BlockDownloader(_fullSyncFeed, _syncPeerPool, _blockTree, _blockValidator, _sealValidator, _syncReport, _receiptStorage, _specProvider, _logManager);
            fullSyncBlockDownloader.SyncEvent += DownloaderOnSyncEvent;
            fullSyncBlockDownloader.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Full sync block downloader failed", t.Exception);
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info("Full sync block downloader task completed.");
                }
            });
        }

        private void StartStateSyncComponents()
        {
            _stateSyncFeed = new StateSyncFeed(_dbProvider.CodeDb, _dbProvider.StateDb, _dbProvider.BeamStateDb, _syncMode, _blockTree, _logManager, _syncPeerPool.PeerMaxCount * 2);
            StateSyncDispatcher stateSyncDispatcher = new StateSyncDispatcher(_stateSyncFeed, _syncPeerPool, new StateSyncAllocationStrategyFactory(), _logManager);
            Task syncDispatcherTask = stateSyncDispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("State sync failed", t.Exception);
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info("State sync task completed.");
                }
            });
        }

        private void StartFastBlocksComponents()
        {
            FastBlocksPeerAllocationStrategyFactory fastFactory = new FastBlocksPeerAllocationStrategyFactory();

            _headersFeed = new FastHeadersSyncFeed(_blockTree, _syncPeerPool, _syncConfig, _syncReport, _logManager);
            HeadersSyncDispatcher headersDispatcher = new HeadersSyncDispatcher(_headersFeed, _syncPeerPool, fastFactory, _logManager);
            Task headersTask = headersDispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Fast blocks headers downloader failed", t.Exception);
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info("Fast blocks headers task completed.");
                }
            });

            if (_syncConfig.DownloadHeadersInFastSync)
            {
                if (_syncConfig.DownloadBodiesInFastSync)
                {
                    _bodiesFeed = new FastBodiesSyncFeed(_syncMode, _blockTree, _syncPeerPool, _syncConfig, _syncReport, _logManager);
                    BodiesSyncDispatcher bodiesDispatcher = new BodiesSyncDispatcher(_bodiesFeed, _syncPeerPool, fastFactory, _logManager);
                    Task bodiesTask = bodiesDispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            if (_logger.IsError) _logger.Error("Fast bodies sync failed", t.Exception);
                        }
                        else
                        {
                            if (_logger.IsInfo) _logger.Info("Fast blocks bodies task completed.");
                        }
                    });
                }

                if (_syncConfig.DownloadReceiptsInFastSync)
                {
                    _receiptsFeed = new FastReceiptsSyncFeed(_syncMode, _specProvider, _blockTree, _receiptStorage, _syncPeerPool, _syncConfig, _syncReport, _logManager);
                    ReceiptsSyncDispatcher receiptsDispatcher = new ReceiptsSyncDispatcher(_receiptsFeed, _syncPeerPool, fastFactory, _logManager);
                    Task receiptsTask = receiptsDispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            if (_logger.IsError) _logger.Error("Fast receipts sync failed", t.Exception);
                        }
                        else
                        {
                            if (_logger.IsInfo) _logger.Info("Fast blocks receipts task completed.");
                        }
                    });
                }
            }
        }

        private void StartFastSyncComponents()
        {
            _fastSyncFeed = new FastSyncFeed(_syncMode, _syncConfig, _logManager);
            BlockDownloader downloader = new BlockDownloader(_fastSyncFeed, _syncPeerPool, _blockTree, _blockValidator, _sealValidator, _syncReport, _receiptStorage, _specProvider, _logManager);
            downloader.SyncEvent += DownloaderOnSyncEvent;

            downloader.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Fast sync failed", t.Exception);
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info("Fast sync blocks downloader task completed.");
                }
            });
        }

        private NodeStatsEventType Convert(SyncEvent syncEvent)
        {
            return syncEvent switch
            {
                Synchronization.SyncEvent.Started => NodeStatsEventType.SyncStarted,
                Synchronization.SyncEvent.Failed => NodeStatsEventType.SyncFailed,
                Synchronization.SyncEvent.Cancelled => NodeStatsEventType.SyncCancelled,
                Synchronization.SyncEvent.Completed => NodeStatsEventType.SyncCompleted,
                _ => throw new ArgumentOutOfRangeException(nameof(syncEvent))
            };
        }

        private void DownloaderOnSyncEvent(object sender, SyncEventArgs e)
        {
            _nodeStatsManager.ReportSyncEvent(e.Peer.Node, Convert(e.SyncEvent));
            SyncEvent?.Invoke(this, e);
        }

        public void Dispose()
        {
            _syncCancellation?.Cancel();
            _syncCancellation?.Dispose();
            _syncReport?.Dispose();
            
            _fastSyncFeed?.Dispose();
            _stateSyncFeed?.Dispose();
            _fullSyncFeed?.Dispose();
            _bodiesFeed?.Dispose();
            _receiptsFeed?.Dispose();
        }
    }
}
