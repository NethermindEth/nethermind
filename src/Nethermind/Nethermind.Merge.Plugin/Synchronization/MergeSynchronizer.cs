// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class MergeSynchronizer : Synchronizer
{
    private readonly IBeaconSyncStrategy _beaconSync;
    private readonly ServiceProvider _beaconServiceProvider;

    public override ISyncModeSelector SyncModeSelector => _syncModeSelector ??= new MultiSyncModeSelector(
        SyncProgressResolver,
        _syncPeerPool,
        _syncConfig,
        _beaconSync,
        _betterPeerStrategy!,
        _logManager);

    public MergeSynchronizer(
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
        IPoSSwitcher poSSwitcher,
        IMergeConfig mergeConfig,
        IInvalidChainTracker invalidChainTracker,
        IProcessExitSource exitSource,
        IBetterPeerStrategy betterPeerStrategy,
        ChainSpec chainSpec,
        IBeaconSyncStrategy beaconSync,
        IStateReader stateReader,
        ILogManager logManager)
        : base(
            dbProvider,
            nodeStorage,
            specProvider,
            blockTree,
            receiptStorage,
            peerPool,
            nodeStatsManager,
            syncConfig,
            blockDownloaderFactory,
            pivot,
            exitSource,
            betterPeerStrategy,
            chainSpec,
            stateReader,
            logManager)
    {
        _beaconSync = beaconSync;

        IServiceCollection beaconServiceCollection = new ServiceCollection();
        beaconServiceCollection
            .AddSingleton(blockTree)
            .AddSingleton(invalidChainTracker)
            .AddSingleton(poSSwitcher)
            .AddSingleton(mergeConfig)
            .AddSingleton(dbProvider)
            .AddSingleton(nodeStorage)
            .AddSingleton(peerPool)
            .AddSingleton(logManager)
            .AddSingleton(specProvider)
            .AddSingleton(receiptStorage)
            .AddSingleton(pivot)
            .AddKeyedSingleton<IDb>(DbNames.Metadata, (sp, _) => dbProvider.MetadataDb)
            .AddKeyedSingleton<IDb>(DbNames.Code, (sp, _) => dbProvider.CodeDb)
            .AddSingleton(_syncReport)
            .AddSingleton(syncConfig);

        RegisterBeaconHeaderSyncComponent(beaconServiceCollection);
        _beaconServiceProvider = beaconServiceCollection.BuildServiceProvider();
    }

    private static void RegisterBeaconHeaderSyncComponent(IServiceCollection serviceCollection)
    {
        serviceCollection
            .AddSingleton<ISyncFeed<HeadersSyncBatch?>, BeaconHeadersSyncFeed>()
            .AddSingleton<ISyncDownloader<HeadersSyncBatch>, BeaconHeadersSyncDownloader>()
            .AddSingleton<IPeerAllocationStrategyFactory<HeadersSyncBatch>, FastBlocksPeerAllocationStrategyFactory>();

        RegisterDispatcher<HeadersSyncBatch>(serviceCollection);
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
            _beaconServiceProvider.GetRequiredService<SyncDispatcher<HeadersSyncBatch>>();

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
        WireFeedWithModeSelector(_beaconServiceProvider.GetRequiredService<ISyncFeed<HeadersSyncBatch>>());
    }
}
