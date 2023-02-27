// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Synchronization
{
    public class Synchronizer : ISynchronizer
    {
        private const int FeedsTerminationTimeout = 5_000;

        private readonly ISpecProvider _specProvider;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockDownloaderFactory _blockDownloaderFactory;
        private readonly INodeStatsManager _nodeStatsManager;

        protected readonly ILogger _logger;
        protected readonly IBlockTree _blockTree;
        protected readonly ISyncConfig _syncConfig;
        protected readonly ISnapProvider _snapProvider;
        protected readonly ISyncPeerPool _syncPeerPool;
        protected readonly ILogManager _logManager;
        protected readonly ISyncReport _syncReport;
        protected readonly IPivot _pivot;

        protected CancellationTokenSource? _syncCancellation = new();

        /* sync events are used mainly for managing sync peers reputation */
        public event EventHandler<SyncEventArgs>? SyncEvent;

        private readonly IDbProvider _dbProvider;
        protected readonly ISyncModeSelector _syncMode;
        private FastSyncFeed? _fastSyncFeed;
        private StateSyncFeed? _stateSyncFeed;
        private SnapSyncFeed? _snapSyncFeed;
        private FullSyncFeed? _fullSyncFeed;
        private HeadersSyncFeed? _headersFeed;
        private BodiesSyncFeed? _bodiesFeed;
        private ReceiptsSyncFeed? _receiptsFeed;

        public Synchronizer(
            IDbProvider dbProvider,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ISyncPeerPool peerPool,
            INodeStatsManager nodeStatsManager,
            ISyncModeSelector syncModeSelector,
            ISyncConfig syncConfig,
            ISnapProvider snapProvider,
            IBlockDownloaderFactory blockDownloaderFactory,
            IPivot pivot,
            ISyncReport syncReport,
            ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _syncMode = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _snapProvider = snapProvider ?? throw new ArgumentNullException(nameof(snapProvider));
            _blockDownloaderFactory = blockDownloaderFactory ?? throw new ArgumentNullException(nameof(blockDownloaderFactory));
            _pivot = pivot ?? throw new ArgumentNullException(nameof(pivot));
            _syncPeerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
        }

        public virtual void Start()
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

                if (_syncConfig.SnapSync)
                {
                    StartSnapSyncComponents();
                }

                StartStateSyncComponents();
            }
        }

        private void StartFullSyncComponents()
        {
            _fullSyncFeed = new FullSyncFeed(_syncMode, LimboLogs.Instance);
            BlockDownloader fullSyncBlockDownloader = _blockDownloaderFactory.Create(_fullSyncFeed);
            fullSyncBlockDownloader.SyncEvent += DownloaderOnSyncEvent;
            fullSyncBlockDownloader.Start(_syncCancellation!.Token).ContinueWith(t =>
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
            TreeSync treeSync = new(SyncMode.StateNodes, _dbProvider.CodeDb, _dbProvider.StateDb, _blockTree, _logManager);
            _stateSyncFeed = new StateSyncFeed(_syncMode, treeSync, _logManager);
            StateSyncDispatcher stateSyncDispatcher = new(_stateSyncFeed!, _syncPeerPool, new StateSyncAllocationStrategyFactory(), _logManager);
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

        private void StartSnapSyncComponents()
        {
            _snapSyncFeed = new SnapSyncFeed(_syncMode, _snapProvider, _logManager);
            SnapSyncDispatcher dispatcher = new(_snapSyncFeed!, _syncPeerPool, new SnapSyncAllocationStrategyFactory(), _logManager);

            Task _ = dispatcher.Start(_syncCancellation!.Token).ContinueWith(t =>
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
            FastBlocksPeerAllocationStrategyFactory fastFactory = new();

            _headersFeed = new HeadersSyncFeed(_syncMode, _blockTree, _syncPeerPool, _syncConfig, _syncReport, _logManager);
            HeadersSyncDispatcher headersDispatcher = new(_headersFeed!, _syncPeerPool, fastFactory, _logManager);
            Task headersTask = headersDispatcher.Start(_syncCancellation!.Token).ContinueWith(t =>
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
                    _bodiesFeed = new BodiesSyncFeed(_syncMode, _blockTree, _syncPeerPool, _syncConfig, _syncReport, _specProvider, _logManager);
                    BodiesSyncDispatcher bodiesDispatcher = new(_bodiesFeed!, _syncPeerPool, fastFactory, _logManager);
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
                    _receiptsFeed = new ReceiptsSyncFeed(_syncMode, _specProvider, _blockTree, _receiptStorage, _syncPeerPool, _syncConfig, _syncReport, _logManager);
                    ReceiptsSyncDispatcher receiptsDispatcher = new(_receiptsFeed!, _syncPeerPool, fastFactory, _logManager);
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
            BlockDownloader downloader = _blockDownloaderFactory.Create(_fastSyncFeed);
            downloader.SyncEvent += DownloaderOnSyncEvent;

            downloader.Start(_syncCancellation!.Token).ContinueWith(t =>
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

        private void DownloaderOnSyncEvent(object? sender, SyncEventArgs e)
        {
            _nodeStatsManager.ReportSyncEvent(e.Peer.Node, Convert(e.SyncEvent));
            SyncEvent?.Invoke(this, e);
        }

        public Task StopAsync()
        {
            _syncCancellation?.Cancel();

            return Task.WhenAny(
                Task.Delay(FeedsTerminationTimeout),
                Task.WhenAll(
                    _fastSyncFeed?.FeedTask ?? Task.CompletedTask,
                    _stateSyncFeed?.FeedTask ?? Task.CompletedTask,
                    _snapSyncFeed?.FeedTask ?? Task.CompletedTask,
                    _fullSyncFeed?.FeedTask ?? Task.CompletedTask,
                    _headersFeed?.FeedTask ?? Task.CompletedTask,
                    _bodiesFeed?.FeedTask ?? Task.CompletedTask,
                    _receiptsFeed?.FeedTask ?? Task.CompletedTask));
        }

        public void Dispose()
        {
            CancellationTokenExtensions.CancelDisposeAndClear(ref _syncCancellation);
            _syncReport.Dispose();
            _fastSyncFeed?.Dispose();
            _stateSyncFeed?.Dispose();
            _snapSyncFeed?.Dispose();
            _fullSyncFeed?.Dispose();
            _headersFeed?.Dispose();
            _bodiesFeed?.Dispose();
            _receiptsFeed?.Dispose();
        }
    }
}
