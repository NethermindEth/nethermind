//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
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
            logManager)
    {
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
            new(_poSSwitcher, _syncMode, _blockTree, _syncPeerPool, _syncConfig, _syncReport, _pivot, _mergeConfig, _logManager);
        BeaconHeadersSyncDispatcher beaconHeadersDispatcher =
            new(beaconHeadersFeed!, _syncPeerPool, fastFactory, _logManager);
        beaconHeadersDispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
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
