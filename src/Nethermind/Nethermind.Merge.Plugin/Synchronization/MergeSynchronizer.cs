// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Merge.Plugin.Synchronization;

public class MergeSynchronizer : Synchronizer
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IMergeConfig _mergeConfig;
    private readonly IInvalidChainTracker _invalidChainTracker;

    public MergeSynchronizer(
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
        IPoSSwitcher poSSwitcher,
        IMergeConfig mergeConfig,
        IInvalidChainTracker invalidChainTracker,
        IProcessExitSource exitSource,
        ILogManager logManager,
        ISyncReport syncReport)
        : base(
            dbProvider,
            specProvider,
            blockTree,
            receiptStorage,
            peerPool,
            nodeStatsManager,
            syncModeSelector,
            syncConfig,
            snapProvider,
            blockDownloaderFactory,
            pivot,
            syncReport,
            exitSource,
            logManager)
    {
        _invalidChainTracker = invalidChainTracker;
        _poSSwitcher = poSSwitcher;
        _mergeConfig = mergeConfig;
    }

    public override void Start()
    {
        if (!_syncConfig.SynchronizationEnabled)
        {
            return;
        }

        base.Start();
        StartBeaconHeadersComponents();
    }

    private void StartBeaconHeadersComponents()
    {
        FastBlocksPeerAllocationStrategyFactory fastFactory = new();
        BeaconHeadersSyncFeed beaconHeadersFeed =
            new(_poSSwitcher, _syncMode, _blockTree, _syncPeerPool, _syncConfig, _syncReport, _pivot, _mergeConfig, _invalidChainTracker, _logManager);
        BeaconHeadersSyncDownloader beaconHeadersDownloader = new(_logManager);

        SyncDispatcher<HeadersSyncBatch> dispatcher = CreateDispatcher(
            beaconHeadersFeed!,
            beaconHeadersDownloader,
            fastFactory
        );

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
}
