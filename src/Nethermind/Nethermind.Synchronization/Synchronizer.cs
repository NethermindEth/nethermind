// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.DbTuner;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Synchronization
{
    public class Synchronizer(
        ISyncModeSelector syncModeSelector,
        ISyncReport syncReport,
        ISyncConfig syncConfig,
        ILogManager logManager,
        INodeStatsManager nodeStatsManager,
        [KeyFilter(nameof(FullSyncFeed))] FeedComponent<BlocksRequest> fullSyncComponent,
        [KeyFilter(nameof(FastSyncFeed))] FeedComponent<BlocksRequest> fastSyncComponent,
        FeedComponent<StateSyncBatch> stateSyncComponent,
        FeedComponent<SnapSyncBatch> snapSyncComponent,
        [KeyFilter(nameof(HeadersSyncFeed))] FeedComponent<HeadersSyncBatch> fastHeaderComponent,
        FeedComponent<BodiesSyncBatch> oldBodiesComponent,
        FeedComponent<ReceiptsSyncBatch> oldReceiptsComponent,
#pragma warning disable CS9113 // Parameter is unread. But it need to be instantiated to function
        SyncDbTuner syncDbTuner,
        MallocTrimmer mallocTrimmer,
#pragma warning restore CS9113 // Parameter is unread.
        IProcessExitSource exitSource)
         : ISynchronizer
    {
        private const int FeedsTerminationTimeout = 5_000;

        private readonly ILogger _logger = logManager.GetClassLogger<Synchronizer>();

        private CancellationTokenSource? _syncCancellation = new();

        /* sync events are used mainly for managing sync peers reputation */
        public event EventHandler<SyncEventArgs>? SyncEvent;

        public ISyncModeSelector SyncModeSelector => syncModeSelector;

        public static void ConfigureContainerBuilder(ContainerBuilder builder, ISyncConfig syncConfig)
        {
            builder
                .AddSingleton<Synchronizer>()

                .AddSingleton<ISyncModeSelector, MultiSyncModeSelector>()
                .AddSingleton<ISyncProgressResolver, SyncProgressResolver>()
                .AddSingleton<ISyncReport, SyncReport>()
                .AddSingleton<IFullStateFinder, FullStateFinder>()
                .AddSingleton<SyncDbTuner>()
                .AddSingleton<MallocTrimmer>()

            // For blocks. There are two block scope, Fast and Full
                .AddScoped<FeedComponent<BlocksRequest>>()
                .AddScoped<ISyncDownloader<BlocksRequest>, BlockDownloader>()
                .AddScoped<IPeerAllocationStrategyFactory<BlocksRequest>, BlocksSyncPeerAllocationStrategyFactory>()
                .AddScoped<SyncDispatcher<BlocksRequest>>()

            // For headers. There are two header scope, Fast and Beacon
                .AddScoped<FeedComponent<HeadersSyncBatch>>()
                .AddScoped<ISyncDownloader<HeadersSyncBatch>, HeadersSyncDownloader>()
                .AddScoped<IPeerAllocationStrategyFactory<HeadersSyncBatch>, FastBlocksPeerAllocationStrategyFactory>()
                .AddScoped<SyncDispatcher<HeadersSyncBatch>>()

            // SyncProgress resolver need one header sync batch feed, which is the fast header one.
                .Register(ctx => ctx
                    .ResolveNamed<FeedComponent<HeadersSyncBatch>>(nameof(HeadersSyncFeed))
                    .Feed)
                .Named<ISyncFeed<HeadersSyncBatch>>(nameof(HeadersSyncFeed));

            ConfigureSnapComponent(builder, syncConfig);
            ConfigureReceiptSyncComponent(builder, syncConfig);
            ConfigureBodiesSyncComponent(builder, syncConfig);
            ConfigureStateSyncComponent(builder, syncConfig);

            builder
                .RegisterNamedComponentInItsOwnLifetime<FeedComponent<HeadersSyncBatch>>(nameof(HeadersSyncFeed),
                    scopeConfig =>
                    {
                        if (!syncConfig.FastSync || !syncConfig.DownloadHeadersInFastSync)
                        {
                            scopeConfig.AddScoped<ISyncFeed<HeadersSyncBatch>, NoopSyncFeed<HeadersSyncBatch>>();
                        }
                        else
                        {
                            scopeConfig.AddScoped<ISyncFeed<HeadersSyncBatch>, HeadersSyncFeed>();
                        }
                    })
                .RegisterNamedComponentInItsOwnLifetime<FeedComponent<BlocksRequest>>(nameof(FastSyncFeed),
                scopeConfig => scopeConfig.AddScoped<ISyncFeed<BlocksRequest>, FastSyncFeed>())
                .RegisterNamedComponentInItsOwnLifetime<FeedComponent<BlocksRequest>>(nameof(FullSyncFeed),
                    scopeConfig => scopeConfig.AddScoped<ISyncFeed<BlocksRequest>, FullSyncFeed>());
        }

        private static void ConfigureSnapComponent(ContainerBuilder serviceCollection, ISyncConfig syncConfig)
        {
            serviceCollection
                .AddSingleton<ProgressTracker>()
                .AddSingleton<ISnapProvider, SnapProvider>();

            ConfigureSingletonSyncFeed<SnapSyncBatch, SnapSyncFeed, SnapSyncDownloader, SnapSyncAllocationStrategyFactory>(serviceCollection);

            if (!syncConfig.FastSync || !syncConfig.SnapSync)
            {
                serviceCollection.AddSingleton<ISyncFeed<SnapSyncBatch>, NoopSyncFeed<SnapSyncBatch>>();
            }
        }

        private static void ConfigureReceiptSyncComponent(ContainerBuilder serviceCollection, ISyncConfig syncConfig)
        {
            ConfigureSingletonSyncFeed<ReceiptsSyncBatch, ReceiptsSyncFeed, ReceiptsSyncDispatcher, FastBlocksPeerAllocationStrategyFactory>(serviceCollection);

            if (!syncConfig.FastSync || !syncConfig.DownloadHeadersInFastSync ||
                !syncConfig.DownloadBodiesInFastSync ||
                !syncConfig.DownloadReceiptsInFastSync)
            {
                serviceCollection.AddSingleton<ISyncFeed<ReceiptsSyncBatch>, NoopSyncFeed<ReceiptsSyncBatch>>();
            }
        }

        private static void ConfigureBodiesSyncComponent(ContainerBuilder serviceCollection, ISyncConfig syncConfig)
        {
            ConfigureSingletonSyncFeed<BodiesSyncBatch, BodiesSyncFeed, BodiesSyncDownloader, FastBlocksPeerAllocationStrategyFactory>(serviceCollection);

            if (!syncConfig.FastSync || !syncConfig.DownloadHeadersInFastSync ||
                !syncConfig.DownloadBodiesInFastSync)
            {
                serviceCollection.AddSingleton<ISyncFeed<BodiesSyncBatch>, NoopSyncFeed<BodiesSyncBatch>>();
            }

        }

        private static void ConfigureStateSyncComponent(ContainerBuilder serviceCollection, ISyncConfig syncConfig)
        {
            serviceCollection
                .AddSingleton<TreeSync>();

            ConfigureSingletonSyncFeed<StateSyncBatch, StateSyncFeed, StateSyncDownloader, StateSyncAllocationStrategyFactory>(serviceCollection);

            // Disable it by setting noop
            if (!syncConfig.FastSync) serviceCollection.AddSingleton<ISyncFeed<StateSyncBatch>, NoopSyncFeed<StateSyncBatch>>();
        }

        private static void ConfigureSingletonSyncFeed<TBatch, TFeed, TDownloader, TAllocationStrategy>(ContainerBuilder serviceCollection) where TFeed : class, ISyncFeed<TBatch> where TDownloader : class, ISyncDownloader<TBatch> where TAllocationStrategy : class, IPeerAllocationStrategyFactory<TBatch>
        {
            serviceCollection
                .AddSingleton<ISyncFeed<TBatch>, TFeed>()
                .AddSingleton<FeedComponent<TBatch>>()
                .AddSingleton<ISyncDownloader<TBatch>, TDownloader>()
                .AddSingleton<IPeerAllocationStrategyFactory<TBatch>, TAllocationStrategy>()
                .AddSingleton<SyncDispatcher<TBatch>>();
        }

        public virtual void Start()
        {
            if (!syncConfig.SynchronizationEnabled)
            {
                return;
            }

            StartFullSyncComponents();

            if (syncConfig.FastSync)
            {
                StartFastBlocksComponents();

                StartFastSyncComponents();

                if (syncConfig.SnapSync)
                {
                    StartSnapSyncComponents();
                }

                StartStateSyncComponents();
            }

            if (syncConfig.ExitOnSynced)
            {
                exitSource.WatchForExit(SyncModeSelector, logManager, TimeSpan.FromSeconds(syncConfig.ExitOnSyncedWaitTimeSec));
            }

            WireMultiSyncModeSelector();

            SyncModeSelector.Changed += syncReport.SyncModeSelectorOnChanged;
        }

        private void StartFullSyncComponents()
        {
            fullSyncComponent.BlockDownloader.SyncEvent += DownloaderOnSyncEvent;
            fullSyncComponent.Dispatcher.Start(_syncCancellation!.Token).ContinueWith(t =>
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
            fastSyncComponent.BlockDownloader.SyncEvent += DownloaderOnSyncEvent;
            fastSyncComponent.Dispatcher.Start(_syncCancellation!.Token).ContinueWith(t =>
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
            Task syncDispatcherTask = stateSyncComponent.Dispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
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
            Task _ = snapSyncComponent.Dispatcher.Start(_syncCancellation!.Token).ContinueWith(t =>
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
            Task headersTask = fastHeaderComponent.Dispatcher.Start(_syncCancellation!.Token).ContinueWith(t =>
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

            if (syncConfig.DownloadHeadersInFastSync)
            {
                if (syncConfig.DownloadBodiesInFastSync)
                {
                    Task bodiesTask = oldBodiesComponent.Dispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
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

                if (syncConfig.DownloadReceiptsInFastSync)
                {
                    Task receiptsTask = oldReceiptsComponent.Dispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
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
            nodeStatsManager.ReportSyncEvent(e.Peer.Node, Convert(e.SyncEvent));
            SyncEvent?.Invoke(this, e);
        }

        public Task StopAsync()
        {
            _syncCancellation?.Cancel();

            return Task.WhenAny(
                Task.Delay(FeedsTerminationTimeout),
                Task.WhenAll(
                    fullSyncComponent.Feed.FeedTask,
                    fastSyncComponent.Feed.FeedTask,
                    stateSyncComponent.Feed.FeedTask,
                    snapSyncComponent.Feed.FeedTask,
                    fastHeaderComponent.Feed.FeedTask,
                    oldBodiesComponent.Feed.FeedTask,
                    oldReceiptsComponent.Feed.FeedTask));
        }

        private void WireMultiSyncModeSelector()
        {
            WireFeedWithModeSelector(fullSyncComponent.Feed);
            WireFeedWithModeSelector(fastSyncComponent.Feed);
            WireFeedWithModeSelector(stateSyncComponent.Feed);
            WireFeedWithModeSelector(snapSyncComponent.Feed);
            WireFeedWithModeSelector(fastHeaderComponent.Feed);
            WireFeedWithModeSelector(oldBodiesComponent.Feed);
            WireFeedWithModeSelector(oldReceiptsComponent.Feed);
        }

        public void WireFeedWithModeSelector<T>(ISyncFeed<T>? feed)
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
        }
    }
}
