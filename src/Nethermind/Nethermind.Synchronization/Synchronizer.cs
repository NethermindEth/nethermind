// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.DbTuner;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.StateSync;
using Nethermind.Trie.Pruning;

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
        protected readonly ISyncPeerPool _syncPeerPool;
        protected readonly ILogManager _logManager;
        protected readonly ISyncReport _syncReport;
        protected readonly IPivot _pivot;

        protected CancellationTokenSource? _syncCancellation = new();

        /* sync events are used mainly for managing sync peers reputation */
        public event EventHandler<SyncEventArgs>? SyncEvent;

        private readonly IDbProvider _dbProvider;
        private FastSyncFeed? _fastSyncFeed;
        private StateSyncFeed? _stateSyncFeed;
        private FullSyncFeed? _fullSyncFeed;
        private readonly IProcessExitSource _exitSource;
        protected IBetterPeerStrategy _betterPeerStrategy;
        private readonly ChainSpec _chainSpec;

        public ISnapProvider SnapProvider { get; }

        private HeadersSyncFeed? _headersSyncFeed;
        private HeadersSyncFeed? HeadersSyncFeed => _headersSyncFeed ??= CreateHeadersSyncFeed();

        private ReceiptsSyncFeed? _receiptsSyncFeed;
        private ReceiptsSyncFeed? ReceiptsSyncFeed => _receiptsSyncFeed ??= CreateReceiptsSyncFeed();

        private BodiesSyncFeed? _bodiesSyncFeed;
        private BodiesSyncFeed? BodiesSyncFeed => _bodiesSyncFeed ??= CreateBodiesSyncFeed();

        private SnapSyncFeed? _snapSyncFeed;
        private SnapSyncFeed? SnapSyncFeed => _snapSyncFeed ??= CreateSnapSyncFeed();

        private ISyncProgressResolver? _syncProgressResolver;
        public ISyncProgressResolver SyncProgressResolver => _syncProgressResolver ??= new SyncProgressResolver(
            _blockTree,
            new FullStateFinder(_blockTree, _stateReader),
            _syncConfig,
            HeadersSyncFeed,
            BodiesSyncFeed,
            ReceiptsSyncFeed,
            SnapSyncFeed,
            _logManager);

        protected ISyncModeSelector? _syncModeSelector;
        private readonly IStateReader _stateReader;

        public virtual ISyncModeSelector SyncModeSelector => _syncModeSelector ??= new MultiSyncModeSelector(
            SyncProgressResolver,
            _syncPeerPool!,
            _syncConfig,
            No.BeaconSync,
            _betterPeerStrategy!,
            _logManager,
            _chainSpec?.SealEngineType == SealEngineType.Clique);

        public Synchronizer(
            IDbProvider dbProvider,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ISyncPeerPool peerPool,
            INodeStatsManager nodeStatsManager,
            ISyncConfig syncConfig,
            IBlockDownloaderFactory blockDownloaderFactory,
            IPivot pivot,
            IProcessExitSource processExitSource,
            IBetterPeerStrategy betterPeerStrategy,
            ChainSpec chainSpec,
            IStateReader stateReader,
            ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _blockDownloaderFactory = blockDownloaderFactory ?? throw new ArgumentNullException(nameof(blockDownloaderFactory));
            _pivot = pivot ?? throw new ArgumentNullException(nameof(pivot));
            _syncPeerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _exitSource = processExitSource ?? throw new ArgumentNullException(nameof(processExitSource));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _betterPeerStrategy = betterPeerStrategy ?? throw new ArgumentNullException(nameof(betterPeerStrategy));
            _chainSpec = chainSpec ?? throw new ArgumentNullException(nameof(chainSpec));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(_stateReader));

            _syncReport = new SyncReport(_syncPeerPool!, nodeStatsManager!, _syncConfig, _pivot, logManager);

            ProgressTracker progressTracker = new(
                blockTree,
                dbProvider.StateDb,
                logManager,
                _syncConfig.SnapSyncAccountRangePartitionCount);
            SnapProvider = new SnapProvider(progressTracker, dbProvider, logManager);
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

            if (_syncConfig.TuneDbMode != ITunableDb.TuneType.Default || _syncConfig.BlocksDbTuneDbMode != ITunableDb.TuneType.Default)
            {
                SetupDbOptimizer();
            }

            if (_syncConfig.ExitOnSynced)
            {
                _exitSource.WatchForExit(SyncModeSelector, _logManager, TimeSpan.FromSeconds(_syncConfig.ExitOnSyncedWaitTimeSec));
            }

            WireMultiSyncModeSelector();

            new MallocTrimmer(SyncModeSelector, TimeSpan.FromSeconds(_syncConfig.MallocTrimIntervalSec), _logManager);
            SyncModeSelector.Changed += _syncReport.SyncModeSelectorOnChanged;
        }

        private HeadersSyncFeed? CreateHeadersSyncFeed()
        {
            if (!_syncConfig.FastSync || !_syncConfig.FastBlocks || !_syncConfig.DownloadHeadersInFastSync) return null;
            return new HeadersSyncFeed(_blockTree, _syncPeerPool, _syncConfig, _syncReport, _logManager);
        }

        private BodiesSyncFeed? CreateBodiesSyncFeed()
        {
            if (!_syncConfig.FastSync || !_syncConfig.FastBlocks || !_syncConfig.DownloadHeadersInFastSync || !_syncConfig.DownloadBodiesInFastSync) return null;
            return new BodiesSyncFeed(_specProvider, _blockTree, _syncPeerPool, _syncConfig, _syncReport, _dbProvider.BlocksDb, _dbProvider.MetadataDb, _logManager);
        }

        private ReceiptsSyncFeed? CreateReceiptsSyncFeed()
        {
            if (!_syncConfig.FastSync || !_syncConfig.FastBlocks || !_syncConfig.DownloadHeadersInFastSync || !_syncConfig.DownloadBodiesInFastSync || !_syncConfig.DownloadReceiptsInFastSync) return null;
            return new ReceiptsSyncFeed(_specProvider, _blockTree, _receiptStorage, _syncPeerPool, _syncConfig, _syncReport, _dbProvider.MetadataDb, _logManager);
        }

        private SnapSyncFeed? CreateSnapSyncFeed()
        {
            if (!_syncConfig.FastSync || !_syncConfig.SnapSync) return null;
            return new SnapSyncFeed(SnapProvider, _logManager);
        }

        private void SetupDbOptimizer()
        {
            new SyncDbTuner(
                _syncConfig,
                SnapSyncFeed,
                BodiesSyncFeed,
                ReceiptsSyncFeed,
                _dbProvider.StateDb as ITunableDb,
                _dbProvider.CodeDb as ITunableDb,
                _dbProvider.BlocksDb as ITunableDb,
                _dbProvider.ReceiptsDb as ITunableDb);
        }

        private void StartFullSyncComponents()
        {
            _fullSyncFeed = new FullSyncFeed();
            BlockDownloader fullSyncBlockDownloader = _blockDownloaderFactory.Create(_fullSyncFeed, _blockTree, _receiptStorage, _syncPeerPool, _syncReport);
            fullSyncBlockDownloader.SyncEvent += DownloaderOnSyncEvent;

            SyncDispatcher<BlocksRequest> dispatcher = CreateDispatcher(
                _fullSyncFeed,
                fullSyncBlockDownloader,
                _blockDownloaderFactory.CreateAllocationStrategyFactory()
            );

            dispatcher.Start(_syncCancellation!.Token).ContinueWith(t =>
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

        private void StartFastSyncComponents()
        {
            _fastSyncFeed = new FastSyncFeed(_syncConfig);
            BlockDownloader downloader = _blockDownloaderFactory.Create(_fastSyncFeed, _blockTree, _receiptStorage, _syncPeerPool, _syncReport);
            downloader.SyncEvent += DownloaderOnSyncEvent;

            SyncDispatcher<BlocksRequest> dispatcher = CreateDispatcher(
                _fastSyncFeed,
                downloader,
                _blockDownloaderFactory.CreateAllocationStrategyFactory()
            );

            dispatcher.Start(_syncCancellation!.Token).ContinueWith(t =>
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

        private void StartStateSyncComponents()
        {
            TreeSync treeSync = new(SyncMode.StateNodes, _dbProvider.CodeDb, _dbProvider.StateDb, _blockTree, _logManager);
            _stateSyncFeed = new StateSyncFeed(treeSync, _logManager);
            SyncDispatcher<StateSyncBatch> stateSyncDispatcher = CreateDispatcher(
                _stateSyncFeed,
                new StateSyncDownloader(_logManager),
                new StateSyncAllocationStrategyFactory()
            );

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
            SyncDispatcher<SnapSyncBatch> dispatcher = CreateDispatcher(
                SnapSyncFeed,
                new SnapSyncDownloader(_logManager),
                new SnapSyncAllocationStrategyFactory()
            );

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
            SyncDispatcher<HeadersSyncBatch> headersDispatcher = CreateDispatcher(
                HeadersSyncFeed,
                new HeadersSyncDownloader(_logManager),
                fastFactory
            );

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

                    SyncDispatcher<BodiesSyncBatch> bodiesDispatcher = CreateDispatcher(
                        BodiesSyncFeed!,
                        new BodiesSyncDownloader(_logManager),
                        fastFactory
                    );

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
                    SyncDispatcher<ReceiptsSyncBatch> receiptsDispatcher = CreateDispatcher(
                        ReceiptsSyncFeed!,
                        new ReceiptsSyncDispatcher(_logManager),
                        fastFactory
                    );

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

        protected SyncDispatcher<T> CreateDispatcher<T>(ISyncFeed<T> feed, ISyncDownloader<T> downloader, IPeerAllocationStrategyFactory<T> peerAllocationStrategyFactory)
        {
            return new(
                _syncConfig.MaxProcessingThreads,
                feed!,
                downloader,
                _syncPeerPool,
                peerAllocationStrategyFactory,
                _logManager);
        }

        private static NodeStatsEventType Convert(SyncEvent syncEvent)
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
                    SnapSyncFeed?.FeedTask ?? Task.CompletedTask,
                    _fullSyncFeed?.FeedTask ?? Task.CompletedTask,
                    HeadersSyncFeed?.FeedTask ?? Task.CompletedTask,
                    BodiesSyncFeed?.FeedTask ?? Task.CompletedTask,
                    ReceiptsSyncFeed?.FeedTask ?? Task.CompletedTask));
        }

        private void WireMultiSyncModeSelector()
        {
            WireFeedWithModeSelector(_fastSyncFeed);
            WireFeedWithModeSelector(_stateSyncFeed);
            WireFeedWithModeSelector(SnapSyncFeed);
            WireFeedWithModeSelector(_fullSyncFeed);
            WireFeedWithModeSelector(HeadersSyncFeed);
            WireFeedWithModeSelector(BodiesSyncFeed);
            WireFeedWithModeSelector(ReceiptsSyncFeed);
        }

        protected void WireFeedWithModeSelector<T>(ISyncFeed<T>? feed)
        {
            if (feed == null) return;
            SyncModeSelector.Changed += ((sender, args) =>
            {
                feed?.SyncModeSelectorOnChanged(args.Current);
            });
            feed?.SyncModeSelectorOnChanged(SyncModeSelector.Current);
        }

        public void Dispose()
        {
            CancellationTokenExtensions.CancelDisposeAndClear(ref _syncCancellation);
            _syncReport.Dispose();
            _fastSyncFeed?.Dispose();
            _stateSyncFeed?.Dispose();
            SnapSyncFeed?.Dispose();
            _fullSyncFeed?.Dispose();
            HeadersSyncFeed?.Dispose();
            BodiesSyncFeed?.Dispose();
            ReceiptsSyncFeed?.Dispose();
        }
    }
}
