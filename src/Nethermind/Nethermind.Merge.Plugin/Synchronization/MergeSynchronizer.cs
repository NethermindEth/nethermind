// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class MergeSynchronizer : Synchronizer
{
    private readonly ServiceProvider _beaconScope;

    public MergeSynchronizer(IServiceCollection serviceCollection, ISyncConfig syncConfig) : base(serviceCollection, syncConfig)
    {
        RegisterBeaconHeaderSyncComponent(_serviceCollection);
        _beaconScope = _serviceCollection.BuildServiceProvider();
    }

    protected override void ConfigureSynchronizerServiceCollection(IServiceCollection serviceCollection)
    {
        base.ConfigureSynchronizerServiceCollection(serviceCollection);

        serviceCollection
            // It does not set the last parameter
            .AddSingleton<MultiSyncModeSelector>(sp => new MultiSyncModeSelector(
                sp.GetRequiredService<ISyncProgressResolver>(),
                sp.GetRequiredService<ISyncPeerPool>(),
                sp.GetRequiredService<ISyncConfig>(),
                sp.GetRequiredService<IBeaconSyncStrategy>(),
                sp.GetRequiredService<IBetterPeerStrategy>(),
                sp.GetRequiredService<ILogManager>()))

            // Replace block downloader
            .AddSingleton<IChainLevelHelper, ChainLevelHelper>()
            .AddSingleton<BlockDownloader, MergeBlockDownloader>()
            .AddSingleton<IPeerAllocationStrategyFactory<Nethermind.Synchronization.Blocks.BlocksRequest>, MergeBlocksSyncPeerAllocationStrategyFactory>();
    }

    private static void RegisterBeaconHeaderSyncComponent(IServiceCollection serviceCollection)
    {
        serviceCollection
            .AddSingleton<ISyncFeed<HeadersSyncBatch?>, BeaconHeadersSyncFeed>()
            .AddSingleton<ISyncDownloader<HeadersSyncBatch>, BeaconHeadersSyncDownloader>()
            .AddSingleton<IPeerAllocationStrategyFactory<HeadersSyncBatch>, FastBlocksPeerAllocationStrategyFactory>()
            .AddSingleton<SyncDispatcher<HeadersSyncBatch>>();
    }

    public override void Start()
    {
        if (!_syncConfig.SynchronizationEnabled)
        {
            return;
        }

        base.Start();
        StartBeaconHeadersComponents();
        WireMultiSyncModeSelector();
    }

    private void StartBeaconHeadersComponents()
    {
        SyncDispatcher<HeadersSyncBatch> dispatcher =
            _beaconScope.GetRequiredService<SyncDispatcher<HeadersSyncBatch>>();

        dispatcher.Start(_syncCancellation!.Token).ContinueWith(t =>
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
        WireFeedWithModeSelector(_beaconScope.GetRequiredService<ISyncFeed<HeadersSyncBatch>>());
    }
}
