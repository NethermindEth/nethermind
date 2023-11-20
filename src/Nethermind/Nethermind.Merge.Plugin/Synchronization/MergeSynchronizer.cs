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
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie.Pruning;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie.ByPath;

namespace Nethermind.Merge.Plugin.Synchronization;

public class MergeSynchronizer : Synchronizer
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IMergeConfig _mergeConfig;
    private readonly IInvalidChainTracker _invalidChainTracker;
    private BeaconHeadersSyncFeed _beaconHeadersFeed = null!;
    private readonly IBeaconSyncStrategy _beaconSync;

    public override ISyncModeSelector SyncModeSelector => _syncModeSelector ??= new MultiSyncModeSelector(
        SyncProgressResolver,
        _syncPeerPool,
        _syncConfig,
        _beaconSync,
        _betterPeerStrategy!,
        _logManager);

    public MergeSynchronizer(
        IDbProvider dbProvider,
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
        IReadOnlyTrieStore readOnlyTrieStore,
        IBetterPeerStrategy betterPeerStrategy,
        ChainSpec chainSpec,
        IBeaconSyncStrategy beaconSync,
        ILogManager logManager)
        : base(
            dbProvider,
            specProvider,
            blockTree,
            receiptStorage,
            peerPool,
            nodeStatsManager,
            syncConfig,
            blockDownloaderFactory,
            pivot,
            exitSource,
            readOnlyTrieStore,
            betterPeerStrategy,
            chainSpec,
            logManager)
    {
        _invalidChainTracker = invalidChainTracker;
        _poSSwitcher = poSSwitcher;
        _mergeConfig = mergeConfig;
        _beaconSync = beaconSync;
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
        FastBlocksPeerAllocationStrategyFactory fastFactory = new();
        _beaconHeadersFeed =
            new(_poSSwitcher, _blockTree, _syncPeerPool, _syncConfig, _syncReport, _pivot, _mergeConfig, _invalidChainTracker, _logManager);
        BeaconHeadersSyncDownloader beaconHeadersDownloader = new(_logManager);

        SyncDispatcher<HeadersSyncBatch> dispatcher = CreateDispatcher(
            _beaconHeadersFeed!,
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

    private void WireMultiSyncModeSelector()
    {
        WireFeedWithModeSelector(_beaconHeadersFeed);
    }
}
