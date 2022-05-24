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

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Merge.Plugin.Synchronization;

public sealed class BeaconHeadersSyncFeed : HeadersSyncFeed
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IPivot _pivot;
    private readonly IMergeConfig _mergeConfig;
    private readonly ILogger _logger;
    protected override long HeadersDestinationNumber => _pivot.PivotDestinationNumber;

    protected override bool AllHeadersDownloaded => (_blockTree.LowestInsertedBeaconHeader?.Number ?? long.MaxValue) <=
                                                    _pivot.PivotDestinationNumber;

    protected override BlockHeader? LowestInsertedBlockHeader => _blockTree.LowestInsertedBeaconHeader;
    protected override MeasuredProgress HeadersSyncProgressReport => _syncReport.BeaconHeaders;

    public BeaconHeadersSyncFeed(
        IPoSSwitcher poSSwitcher,
        ISyncModeSelector syncModeSelector,
        IBlockTree? blockTree,
        ISyncPeerPool? syncPeerPool,
        ISyncConfig? syncConfig,
        ISyncReport? syncReport,
        IPivot? pivot,
        IMergeConfig? mergeConfig,
        ILogManager logManager)
        : base(syncModeSelector, blockTree, syncPeerPool, syncConfig, syncReport, logManager,
            true) // alwaysStartHeaderSync = true => for the merge we're forcing header sync start. It doesn't matter if it is archive sync or fast sync
    {
        _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
        _pivot = pivot ?? throw new ArgumentNullException(nameof(pivot));
        _mergeConfig = mergeConfig ?? throw new ArgumentNullException(nameof(mergeConfig));
        _logger = logManager.GetClassLogger();
    }

    protected override SyncMode ActivationSyncModes { get; }
        = SyncMode.BeaconHeaders;

    public override bool IsMultiFeed => true;

    public override AllocationContexts Contexts => AllocationContexts.Headers;

    public override void InitializeFeed()
    {
        _pivotNumber = _pivot.PivotNumber;

        BlockHeader? lowestInserted = LowestInsertedBlockHeader;
        long startNumber = LowestInsertedBlockHeader?.Number ?? _pivotNumber;
        Keccak? startHeaderHash = lowestInserted?.Hash ?? _pivot.PivotHash;
        UInt256? startTotalDifficulty =
            lowestInserted?.TotalDifficulty ?? _poSSwitcher.FinalTotalDifficulty ?? null;

        _nextHeaderHash = startHeaderHash;
        _nextHeaderDiff = startTotalDifficulty;

        _lowestRequestedHeaderNumber = startNumber + 1;

        _logger.Info($"Initialized beacon headers sync. lowestRequestedHeaderNumber: {_lowestRequestedHeaderNumber}," +
                     $"lowestInsertedBlockHeader: {LowestInsertedBlockHeader?.ToString(BlockHeader.Format.FullHashAndNumber)}, pivotNumber: {_pivotNumber}, pivotDestination: {_pivot.PivotDestinationNumber}");
    }

    protected override void FinishAndCleanUp()
    {
        // make feed dormant as there may be more header syncs when there is a new beacon pivot
        FallAsleep();
        PostFinishCleanUp();
    }

    protected override AddBlockResult InsertToBlockTree(BlockHeader header)
    {
        if (_logger.IsTrace)
            _logger.Trace(
                $"Adding new header in beacon headers sync {header.ToString(BlockHeader.Format.FullHashAndNumber)}");
        BlockTreeInsertOptions options = BlockTreeInsertOptions.SkipUpdateBestPointers |
                                         BlockTreeInsertOptions.UpdateBeaconPointers |
                                         BlockTreeInsertOptions.AddBeaconMetadata |
                                         BlockTreeInsertOptions.MoveToBeaconMainChain |
                                         BlockTreeInsertOptions.NotOnMainChain;
        if (_nextHeaderDiff is null)
        {
            options |= BlockTreeInsertOptions.TotalDifficultyNotNeeded;
        }

        AddBlockResult insertOutcome = _blockTree.IsKnownBlock(header.Number, header.Hash)
            ? AddBlockResult.AlreadyKnown
            : _blockTree.Insert(header, options);
        // Found existing block in the block tree
        if (insertOutcome == AddBlockResult.AlreadyKnown)
        {
            // if (_blockTree.LowestInsertedHeader != null
            //     && _blockTree.LowestInsertedHeader.Number < (_blockTree.LowestInsertedBeaconHeader?.Number ?? long.MaxValue))
            // {
            if (_logger.IsTrace)
                _logger.Trace(
                    " BeaconHeader LowestInsertedBeaconHeader found existing chain in fast sync," +
                    $"old: {_blockTree.LowestInsertedBeaconHeader?.Number}, new: {_blockTree.LowestInsertedHeader.Number}");
            // beacon header set to (global) lowest inserted header
            //   _blockTree.LowestInsertedBeaconHeader = _blockTree.LowestInsertedHeader;
            if (header.Number < (_blockTree.LowestInsertedBeaconHeader?.Number ?? long.MaxValue))
            {
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"LowestInsertedBeaconHeader AlreadyKnown changed, old: {_blockTree.LowestInsertedBeaconHeader?.Number}, new: {header?.Number}");
                _blockTree.LowestInsertedBeaconHeader = header;
            }
            //}
        }


        if (insertOutcome == AddBlockResult.Added || insertOutcome == AddBlockResult.AlreadyKnown)
        {
            _nextHeaderHash = header.ParentHash!;
            if (_expectedDifficultyOverride?.TryGetValue(header.Number, out ulong nextHeaderDiff) == true)
            {
                _nextHeaderDiff = nextHeaderDiff;
            }
            else
            {
                _nextHeaderDiff = header.TotalDifficulty != null && header.TotalDifficulty >= header.Difficulty
                    ? header.TotalDifficulty - header.Difficulty
                    : null;
            }
        }

        if (_logger.IsTrace)
            _logger.Trace(
                $"New header {header.ToString(BlockHeader.Format.FullHashAndNumber)} in beacon headers sync. InsertOutcome: {insertOutcome}");
        return insertOutcome;
    }
}
