// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Merge.Plugin.Synchronization;

public class MergeSynchronizer(
    [KeyFilter(nameof(BeaconHeadersSyncFeed))] FeedComponent<HeadersSyncBatch> beaconHeaderComponent,
    ISyncConfig syncConfig,
    Synchronizer baseSynchronizer,
    ILogManager logManager)
    : ISynchronizer
{
    private readonly CancellationTokenSource? _syncCancellation = new();
    private readonly ILogger _logger = logManager.GetClassLogger<Synchronizer>();

    public static void ConfigureMergeComponent(ContainerBuilder serviceCollection)
    {
        serviceCollection
            .AddSingleton<MergeSynchronizer>()

            .AddSingleton<IChainLevelHelper, ChainLevelHelper>()
            .AddScoped<BlockDownloader, MergeBlockDownloader>()
            .AddScoped<IPeerAllocationStrategyFactory<BlocksRequest>, MergeBlocksSyncPeerAllocationStrategyFactory>()

            .RegisterNamedComponentInItsOwnLifetime<FeedComponent<HeadersSyncBatch>>(nameof(BeaconHeadersSyncFeed),
                scopeConfig => scopeConfig
                    .AddScoped<ISyncFeed<HeadersSyncBatch>, BeaconHeadersSyncFeed>()
                    .AddScoped<ISyncDownloader<HeadersSyncBatch>, BeaconHeadersSyncDownloader>());
    }

    public event EventHandler<SyncEventArgs>? SyncEvent
    {
        add => baseSynchronizer.SyncEvent += value;
        remove => baseSynchronizer.SyncEvent -= value;
    }

    public void Start()
    {
        if (!syncConfig.SynchronizationEnabled)
        {
            return;
        }

        baseSynchronizer.Start();
        StartBeaconHeadersComponents();
        WireMultiSyncModeSelector();
    }

    public Task StopAsync()
    {
        _syncCancellation?.Cancel();
        return baseSynchronizer.StopAsync();
    }

    private void StartBeaconHeadersComponents()
    {
        beaconHeaderComponent.Dispatcher.Start(_syncCancellation!.Token).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                if (_logger.IsError) _logger.Error("Beacon headers downloader failed", t.Exception);
            }
            else
            {
                if (_logger.IsInfo) _logger.Info("Beacon headers task completed.");
            }
        });
    }

    private void WireMultiSyncModeSelector()
    {
        baseSynchronizer.WireFeedWithModeSelector(beaconHeaderComponent.Feed);
    }

    public void Dispose()
    {
        baseSynchronizer.Dispose();
    }
}
