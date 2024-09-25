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

        private readonly INodeStatsManager _nodeStatsManager;

        protected readonly ILogger _logger;
        protected readonly ISyncConfig _syncConfig;
        protected readonly ILogManager _logManager;

        protected CancellationTokenSource? _syncCancellation = new();

        /* sync events are used mainly for managing sync peers reputation */
        public event EventHandler<SyncEventArgs>? SyncEvent;

        private readonly IDbProvider _dbProvider;
        private readonly IProcessExitSource _exitSource;

        public ISyncProgressResolver SyncProgressResolver => _mainScope.GetRequiredService<ISyncProgressResolver>();

        private readonly ServiceProvider _mainScope;
        private readonly ServiceProvider _fastSyncScope;
        private readonly ServiceProvider _fullSyncScope;

        // Used by subclass for beacon header
        protected readonly IServiceCollection _serviceCollection;

        public ISyncModeSelector SyncModeSelector => _mainScope.GetRequiredService<ISyncModeSelector>();

        public Synchronizer(
            IServiceCollection serviceCollection,
            IDbProvider dbProvider,
            INodeStatsManager nodeStatsManager,
            ISyncConfig syncConfig,
            IProcessExitSource processExitSource,
            ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _exitSource = processExitSource ?? throw new ArgumentNullException(nameof(processExitSource));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

            /*
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
                .AddSingleton(beaconSyncStrategy)
                .AddSingleton(blockDownloaderFactory)
                .AddSingleton(syncConfig)
                .AddSingleton(pivot)
                .AddSingleton(nodeStatsManager)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, (sp, _) => _dbProvider.MetadataDb)
                .AddKeyedSingleton<IDb>(DbNames.Code, (sp, _) => _dbProvider.CodeDb)
                .AddKeyedSingleton<IDb>(DbNames.State, (sp, _) => _dbProvider.StateDb)
                .AddKeyedSingleton<IDbMeta>(DbNames.Blocks, (sp, _) => _dbProvider.BlocksDb);
                */

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

            if (_syncConfig.FastSync)
                RegisterStateSyncComponent(serviceCollection);

            _mainScope = serviceCollection.BuildServiceProvider();

            ConfigureBlocksDownloader(serviceCollection);

            ConfigureFastSync(serviceCollection);
            _fastSyncScope = serviceCollection.BuildServiceProvider();

            ConfigureFullSync(serviceCollection);
            _fullSyncScope = serviceCollection.BuildServiceProvider();

            _serviceCollection = serviceCollection;
        }

        private void ConfigureBlocksDownloader(IServiceCollection serviceCollection)
        {
            // Now we configure blocks downloader
            serviceCollection
                // These three line make sure that these components are not duplicated
                .ForwardServiceAsSingleton<ISyncModeSelector>(_mainScope)
                .ForwardServiceAsSingleton<ISyncReport>(_mainScope)
                .ForwardServiceAsSingleton<ISyncProgressResolver>(_mainScope)

                .AddSingleton<BlockDownloader>(sp => sp.GetRequiredService<IBlockDownloaderFactory>()
                    .Create(sp.GetRequiredService<ISyncFeed<BlocksRequest>>(),
                        sp.GetRequiredService<IBlockTree>(),
                        sp.GetRequiredService<IReceiptStorage>(),
                        sp.GetRequiredService<ISyncPeerPool>(),
                        sp.GetRequiredService<ISyncReport>())
                )
                .AddSingleton<ISyncDownloader<BlocksRequest>>(sp => sp.GetRequiredService<BlockDownloader>())
                .AddSingleton(sp => sp .GetRequiredService<IBlockDownloaderFactory>()
                    .CreateAllocationStrategyFactory());
        }

        protected virtual void ConfigureServiceCollection(IServiceCollection serviceCollection)
        {
            serviceCollection
                .AddSingleton<ISyncProgressResolver, SyncProgressResolver>(sp =>
                    new SyncProgressResolver(
                        sp.GetRequiredService<IBlockTree>(),
                        sp.GetRequiredService<IFullStateFinder>(),
                        sp.GetRequiredService<ISyncConfig>(),
                        sp.GetService<ISyncFeed<HeadersSyncBatch?>>(),
                        sp.GetService<ISyncFeed<BodiesSyncBatch?>>(),
                        sp.GetService<ISyncFeed<ReceiptsSyncBatch?>>(),
                        sp.GetService<ISyncFeed<SnapSyncBatch?>>(),
                        sp.GetRequiredService<ILogManager>()
                    ))
                .AddSingleton<ISyncReport, SyncReport>()
                .AddSingleton<IFullStateFinder, FullStateFinder>()
                .AddSingleton<ISyncModeSelector>(sp => sp.GetRequiredService<MultiSyncModeSelector>())
                .AddSingleton<MultiSyncModeSelector>(sp => new MultiSyncModeSelector(
                    sp.GetRequiredService<ISyncProgressResolver>(),
                    sp.GetRequiredService<ISyncPeerPool>(),
                    sp.GetRequiredService<ISyncConfig>(),
                    sp.GetRequiredService<IBeaconSyncStrategy>(),
                    sp.GetRequiredService<IBetterPeerStrategy>(),
                    sp.GetRequiredService<ILogManager>(),
                    // Need to set this
                    sp.GetRequiredService<ChainSpec>()?.SealEngineType == SealEngineType.Clique))
                .AddSingleton<SyncDbTuner>(sp => new SyncDbTuner(
                    sp.GetRequiredService<ISyncConfig>(),

                    // These are all optional so need to set manually
                    sp.GetService<SnapSyncFeed>(),
                    sp.GetService<BodiesSyncFeed>(),
                    sp.GetService<ReceiptsSyncFeed>(),

                    sp.GetRequiredService<IDbProvider>().StateDb as ITunableDb,
                    sp.GetRequiredService<IDbProvider>().CodeDb as ITunableDb,
                    sp.GetRequiredService<IDbProvider>().BlocksDb as ITunableDb,
                    sp.GetRequiredService<IDbProvider>().ReceiptsDb as ITunableDb))
                .AddSingleton<SyncDispatcher<BlocksRequest>>();
        }

        private static void ConfigureFullSync(IServiceCollection serviceCollection)
        {
            serviceCollection
                .AddSingleton<FullSyncFeed>()
                .AddSingleton<ISyncFeed<BlocksRequest>, FullSyncFeed>(sp => sp.GetRequiredService<FullSyncFeed>());
        }

        private static void ConfigureFastSync(IServiceCollection serviceCollection)
        {
            serviceCollection
                .AddSingleton<FastSyncFeed>()
                .AddSingleton<ISyncFeed<BlocksRequest>, FastSyncFeed>(sp => sp.GetRequiredService<FastSyncFeed>());
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
            SyncModeSelector.Changed += _mainScope.GetRequiredService<ISyncReport>().SyncModeSelectorOnChanged;
        }

        private void SetupDbOptimizer()
        {
            s_dbTuner ??= new SyncDbTuner(
                _syncConfig,
                _mainScope.GetService<SnapSyncFeed>(),
                _mainScope.GetService<BodiesSyncFeed>(),
                _mainScope.GetService<ReceiptsSyncFeed>(),
                _dbProvider.StateDb as ITunableDb,
                _dbProvider.CodeDb as ITunableDb,
                _dbProvider.BlocksDb as ITunableDb,
                _dbProvider.ReceiptsDb as ITunableDb);
        }

        private void StartFullSyncComponents()
        {
            BlockDownloader fullSyncBlockDownloader = _fullSyncScope.GetRequiredService<BlockDownloader>();
            fullSyncBlockDownloader.SyncEvent += DownloaderOnSyncEvent;

            SyncDispatcher<BlocksRequest> dispatcher = _fullSyncScope.GetRequiredService<SyncDispatcher<BlocksRequest>>();

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
            BlockDownloader downloader = _fastSyncScope.GetRequiredService<BlockDownloader>();
            downloader.SyncEvent += DownloaderOnSyncEvent;

            SyncDispatcher<BlocksRequest> dispatcher =
                _fastSyncScope.GetRequiredService<SyncDispatcher<BlocksRequest>>();

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
            SyncDispatcher<StateSyncBatch> stateSyncDispatcher = _mainScope.GetRequiredService<SyncDispatcher<StateSyncBatch>>();

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
            SyncDispatcher<SnapSyncBatch> dispatcher = _mainScope.GetRequiredService<SyncDispatcher<SnapSyncBatch>>();

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
            SyncDispatcher<HeadersSyncBatch> headersDispatcher = _mainScope.GetService<SyncDispatcher<HeadersSyncBatch>>();

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
                        _mainScope.GetRequiredService<SyncDispatcher<BodiesSyncBatch>>();

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
                        _mainScope.GetService<SyncDispatcher<ReceiptsSyncBatch>>();

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
                    _fastSyncScope.GetService<FastSyncFeed>()?.FeedTask ?? Task.CompletedTask,
                    _mainScope.GetService<StateSyncFeed>()?.FeedTask ?? Task.CompletedTask,
                    _mainScope.GetService<SnapSyncFeed>()?.FeedTask ?? Task.CompletedTask,
                    _fastSyncScope.GetService<FullSyncFeed>()?.FeedTask ?? Task.CompletedTask,
                    _mainScope.GetService<HeadersSyncFeed>()?.FeedTask ?? Task.CompletedTask,
                    _mainScope.GetService<BodiesSyncFeed>()?.FeedTask ?? Task.CompletedTask,
                    _mainScope.GetService<ReceiptsSyncFeed>()?.FeedTask ?? Task.CompletedTask));
        }

        private void WireMultiSyncModeSelector()
        {
            WireFeedWithModeSelector(_fastSyncScope.GetService<FastSyncFeed>());
            WireFeedWithModeSelector(_mainScope.GetService<StateSyncFeed>());
            WireFeedWithModeSelector(_mainScope.GetService<SnapSyncFeed>());
            WireFeedWithModeSelector(_fullSyncScope.GetService<FullSyncFeed>());
            WireFeedWithModeSelector(_mainScope.GetService<HeadersSyncFeed>());
            WireFeedWithModeSelector(_mainScope.GetService<BodiesSyncFeed>());
            WireFeedWithModeSelector(_mainScope.GetService<ReceiptsSyncFeed>());
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

            _fullSyncScope.Dispose();
            _fastSyncScope.Dispose();
            _mainScope.Dispose();
        }
    }
}
