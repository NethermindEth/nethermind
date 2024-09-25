// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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

namespace Nethermind.Synchronization
{
    public class Synchronizer : ISynchronizer
    {
        private const int FeedsTerminationTimeout = 5_000;

        private static MallocTrimmer? s_trimmer;
        private static SyncDbTuner? s_dbTuner;

        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockDownloaderFactory _blockDownloaderFactory;
        private readonly INodeStatsManager _nodeStatsManager;

        protected readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        protected readonly ISyncConfig _syncConfig;
        protected readonly ISyncPeerPool _syncPeerPool;
        protected readonly ILogManager _logManager;
        protected readonly ISyncReport _syncReport;
        private readonly IPivot _pivot;

        protected CancellationTokenSource? _syncCancellation = new();

        /* sync events are used mainly for managing sync peers reputation */
        public event EventHandler<SyncEventArgs>? SyncEvent;

        private readonly IDbProvider _dbProvider;
        private FastSyncFeed? _fastSyncFeed;
        private FullSyncFeed? _fullSyncFeed;
        private readonly IProcessExitSource _exitSource;

        public ISyncProgressResolver SyncProgressResolver => _serviceProvider.GetRequiredService<ISyncProgressResolver>();

        private readonly IStateReader _stateReader;
        private readonly ServiceProvider _serviceProvider;

        public ISyncModeSelector SyncModeSelector => _serviceProvider.GetRequiredService<ISyncModeSelector>();

        public Synchronizer(
            IDbProvider dbProvider,
            INodeStorage nodeStorage,
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
            IBeaconSyncStrategy beaconSyncStrategy,
            ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _blockDownloaderFactory = blockDownloaderFactory ?? throw new ArgumentNullException(nameof(blockDownloaderFactory));
            _pivot = pivot ?? throw new ArgumentNullException(nameof(pivot));
            _syncPeerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _exitSource = processExitSource ?? throw new ArgumentNullException(nameof(processExitSource));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(_stateReader));

            _syncReport = new SyncReport(_syncPeerPool!, nodeStatsManager!, _syncConfig, _pivot, logManager);

            var serviceCollection = new ServiceCollection()
                .AddSingleton(blockTree)
                .AddSingleton(dbProvider)
                .AddSingleton(nodeStorage)
                .AddSingleton(peerPool)
                .AddSingleton(logManager)
                .AddSingleton(specProvider)
                .AddSingleton(receiptStorage)
                .AddSingleton(stateReader)
                .AddSingleton(chainSpec)
                .AddSingleton(betterPeerStrategy)
                // .AddSingleton<IBeaconSyncStrategy>(No.BeaconSync)
                .AddSingleton(beaconSyncStrategy)
                .AddSingleton<ISyncProgressResolver, SyncProgressResolver>()
                .AddSingleton<IFullStateFinder, FullStateFinder>()
                .AddKeyedSingleton<IDb>(DbNames.Metadata, (sp, _) => _dbProvider.MetadataDb)
                .AddKeyedSingleton<IDb>(DbNames.Code, (sp, _) => _dbProvider.CodeDb)
                .AddKeyedSingleton<IDb>(DbNames.State, (sp, _) => _dbProvider.StateDb)
                .AddKeyedSingleton<IDbMeta>(DbNames.Blocks, (sp, _) => _dbProvider.BlocksDb)
                .AddSingleton(_syncReport)
                .AddSingleton(syncConfig);

            ConfigureServiceCollection(serviceCollection);

            if (_syncConfig.FastSync && _syncConfig.SnapSync)
                RegisterSnapComponent(serviceCollection);

            if (_syncConfig.FastSync && _syncConfig.DownloadHeadersInFastSync)
                RegisterHeaderSyncComponent(serviceCollection);

            if (_syncConfig.FastSync && _syncConfig.DownloadHeadersInFastSync && _syncConfig.DownloadBodiesInFastSync &&
                _syncConfig.DownloadReceiptsInFastSync)
                RegisterReceiptSyncComponent(serviceCollection);

            if (_syncConfig.FastSync && _syncConfig.DownloadHeadersInFastSync && _syncConfig.DownloadBodiesInFastSync)
                RegisterBodiesSyncComponent(serviceCollection);

            RegisterStateSyncComponent(serviceCollection);
            _serviceProvider = serviceCollection.BuildServiceProvider();
        }

        protected virtual void ConfigureServiceCollection(IServiceCollection serviceCollection)
        {
            serviceCollection
                .AddSingleton<ISyncProgressResolver, SyncProgressResolver>()
                .AddSingleton<IFullStateFinder, FullStateFinder>()
                .AddKeyedSingleton<IDb>(DbNames.Metadata, (sp, _) => _dbProvider.MetadataDb)
                .AddKeyedSingleton<IDb>(DbNames.Code, (sp, _) => _dbProvider.CodeDb)
                .AddKeyedSingleton<IDb>(DbNames.State, (sp, _) => _dbProvider.StateDb)
                .AddKeyedSingleton<IDbMeta>(DbNames.Blocks, (sp, _) => _dbProvider.BlocksDb)
                .AddSingleton<IBeaconSyncStrategy>(No.BeaconSync)
                .AddSingleton<ISyncModeSelector>(sp => sp.GetRequiredService<MultiSyncModeSelector>())
                .AddSingleton<MultiSyncModeSelector>(sp => new MultiSyncModeSelector(
                    sp.GetRequiredService<ISyncProgressResolver>(),
                    sp.GetRequiredService<ISyncPeerPool>(),
                    sp.GetRequiredService<ISyncConfig>(),
                    sp.GetRequiredService<IBeaconSyncStrategy>(),
                    sp.GetRequiredService<IBetterPeerStrategy>(),
                    sp.GetRequiredService<ILogManager>(),
                    sp.GetRequiredService<ChainSpec>()?.SealEngineType == SealEngineType.Clique));
        }

        protected static void RegisterDispatcher<T>(IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton<SyncDispatcher<T>>();
        }

        private static void RegisterSnapComponent(IServiceCollection serviceCollection)
        {
            serviceCollection
                .AddSingleton<ProgressTracker>()
                .AddSingleton<ISnapProvider, SnapProvider>();

            RegisterSyncFeed<SnapSyncBatch, SnapSyncFeed, SnapSyncDownloader, SnapSyncAllocationStrategyFactory>(serviceCollection);
        }

        private static void RegisterHeaderSyncComponent(IServiceCollection serviceCollection)
        {
            RegisterSyncFeed<HeadersSyncBatch, HeadersSyncFeed, HeadersSyncDownloader, FastBlocksPeerAllocationStrategyFactory>(serviceCollection);
        }

        private static void RegisterReceiptSyncComponent(IServiceCollection serviceCollection)
        {
            RegisterSyncFeed<ReceiptsSyncBatch, ReceiptsSyncFeed, ReceiptsSyncDispatcher, FastBlocksPeerAllocationStrategyFactory>(serviceCollection);
        }

        private static void RegisterBodiesSyncComponent(IServiceCollection serviceCollection)
        {
            RegisterSyncFeed<BodiesSyncBatch, BodiesSyncFeed, BodiesSyncDownloader, FastBlocksPeerAllocationStrategyFactory>(serviceCollection);
        }

        private static void RegisterSyncFeed<TBatch, TFeed, TDownloader, TAllocationStrategy>(IServiceCollection serviceCollection) where TFeed : class, ISyncFeed<TBatch> where TDownloader : class, ISyncDownloader<TBatch> where TAllocationStrategy : class, IPeerAllocationStrategyFactory<TBatch>
        {
            serviceCollection
                .AddSingleton<TFeed>()
                .AddSingleton<ISyncFeed<TBatch?>>(sp => sp.GetRequiredService<TFeed>())
                .AddSingleton<ISyncDownloader<TBatch>, TDownloader>()
                .AddSingleton<IPeerAllocationStrategyFactory<TBatch>, TAllocationStrategy>()
                .AddSingleton<SyncDispatcher<TBatch>>();
        }

        private static void RegisterStateSyncComponent(IServiceCollection serviceCollection)
        {
            serviceCollection
                .AddSingleton<TreeSync>();

            RegisterSyncFeed<StateSyncBatch, StateSyncFeed, StateSyncDownloader, StateSyncAllocationStrategyFactory>(serviceCollection);
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
                StartFastBlocksComponents();

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

            s_trimmer ??= new MallocTrimmer(SyncModeSelector, TimeSpan.FromSeconds(_syncConfig.MallocTrimIntervalSec), _logManager);
            SyncModeSelector.Changed += _syncReport.SyncModeSelectorOnChanged;
        }

        private void SetupDbOptimizer()
        {
            s_dbTuner ??= new SyncDbTuner(
                _syncConfig,
                _serviceProvider.GetService<SnapSyncFeed>(),
                _serviceProvider.GetService<BodiesSyncFeed>(),
                _serviceProvider.GetService<ReceiptsSyncFeed>(),
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
            SyncDispatcher<StateSyncBatch> stateSyncDispatcher = _serviceProvider.GetRequiredService<SyncDispatcher<StateSyncBatch>>();

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
            SyncDispatcher<SnapSyncBatch> dispatcher = _serviceProvider.GetRequiredService<SyncDispatcher<SnapSyncBatch>>();

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
            SyncDispatcher<HeadersSyncBatch> headersDispatcher = _serviceProvider.GetService<SyncDispatcher<HeadersSyncBatch>>();

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
                    SyncDispatcher<BodiesSyncBatch> bodiesDispatcher =
                        _serviceProvider.GetRequiredService<SyncDispatcher<BodiesSyncBatch>>();

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
                    SyncDispatcher<ReceiptsSyncBatch> receiptsDispatcher =
                        _serviceProvider.GetService<SyncDispatcher<ReceiptsSyncBatch>>();

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

        private SyncDispatcher<T> CreateDispatcher<T>(ISyncFeed<T> feed, ISyncDownloader<T> downloader, IPeerAllocationStrategyFactory<T> peerAllocationStrategyFactory)
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
                    _serviceProvider.GetService<StateSyncFeed>()?.FeedTask ?? Task.CompletedTask,
                    _serviceProvider.GetService<SnapSyncFeed>()?.FeedTask ?? Task.CompletedTask,
                    _fullSyncFeed?.FeedTask ?? Task.CompletedTask,
                    _serviceProvider.GetService<HeadersSyncFeed>()?.FeedTask ?? Task.CompletedTask,
                    _serviceProvider.GetService<BodiesSyncFeed>()?.FeedTask ?? Task.CompletedTask,
                    _serviceProvider.GetService<ReceiptsSyncFeed>()?.FeedTask ?? Task.CompletedTask));
        }

        private void WireMultiSyncModeSelector()
        {
            WireFeedWithModeSelector(_fastSyncFeed);
            WireFeedWithModeSelector(_serviceProvider.GetService<StateSyncFeed>());
            WireFeedWithModeSelector(_serviceProvider.GetService<SnapSyncFeed>());
            WireFeedWithModeSelector(_fullSyncFeed);
            WireFeedWithModeSelector(_serviceProvider.GetService<HeadersSyncFeed>());
            WireFeedWithModeSelector(_serviceProvider.GetService<BodiesSyncFeed>());
            WireFeedWithModeSelector(_serviceProvider.GetService<ReceiptsSyncFeed>());
        }

        protected void WireFeedWithModeSelector<T>(ISyncFeed<T>? feed)
        {
            if (feed is null) return;
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
            _fullSyncFeed?.Dispose();

            _serviceProvider.Dispose();
        }
    }
}
