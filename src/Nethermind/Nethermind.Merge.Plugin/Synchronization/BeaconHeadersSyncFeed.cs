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
    private readonly IPivot _pivot;
    private readonly IMergeConfig _mergeConfig;
    private readonly ILogger _logger;

    private bool _mergedChain;

    protected override long HeadersDestinationNumber => _syncConfig.PivotNumberParsed;
    protected override bool AllHeadersDownloaded => _mergedChain 
        || (_blockTree.LowestInsertedBeaconHeader?.Number ?? long.MaxValue) <= _syncConfig.PivotNumberParsed + 1;
    protected override BlockHeader? LowestInsertedBlockHeader => _blockTree.LowestInsertedBeaconHeader;
    protected override MeasuredProgress HeadersSyncProgressReport => _syncReport.BeaconHeaders;
    public BeaconHeadersSyncFeed(
        ISyncModeSelector syncModeSelector,
        IBlockTree? blockTree,
        ISyncPeerPool? syncPeerPool,
        ISyncConfig? syncConfig,
        ISyncReport? syncReport,
        IPivot? pivot,
        IMergeConfig? mergeConfig,
        ILogManager logManager) 
        : base(syncModeSelector, blockTree, syncPeerPool, syncConfig, syncReport, logManager, true) // alwaysStartHeaderSync = true => for the merge we're forcing header sync start. It doesn't matter if it is archive sync or fast sync
    {
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
        _blockTree.LoadLowestInsertedBeaconHeader();
        _pivotNumber = _pivot.PivotNumber;
        
        BlockHeader? lowestInserted = LowestInsertedBlockHeader;
        long startNumber = LowestInsertedBlockHeader?.Number ?? _pivotNumber;
        Keccak? startHeaderHash = lowestInserted?.Hash ?? _pivot.PivotHash;
        // TODO: beaconsync TD should not be final total difficulty if pivot destination < TDD block
        UInt256? startTotalDifficulty = lowestInserted?.TotalDifficulty ?? _pivot.PivotTotalDifficulty 
            ?? _mergeConfig.FinalTotalDifficultyParsed;
        
        _nextHeaderHash = startHeaderHash;
        _nextHeaderDiff = startTotalDifficulty;
        
        _lowestRequestedHeaderNumber = startNumber + 1;   
    }

    protected override void FinishAndCleanUp()
    {
        if (_mergeConfig.FinalTotalDifficultyParsed == null)
        {
            // set total difficulty as beacon pivot does not provide total difficulty
            _blockTree.BackFillTotalDifficulty(LowestInsertedBlockHeader?.Number ?? 0, _pivotNumber);   
        }
        // make feed dormant as there may be more header syncs when there is a new beacon pivot
        FallAsleep();
        PostFinishCleanUp();
    }
    
    protected override AddBlockResult InsertToBlockTree(BlockHeader header)
    {
        _logger.Info($"Adding new header in beacon headers sync {header.ToString(BlockHeader.Format.FullHashAndNumber)}"); 
        BlockTreeInsertOptions options = _nextHeaderDiff is null
            ? BlockTreeInsertOptions.TotalDifficultyNotNeeded | BlockTreeInsertOptions.SkipUpdateBestPointers
            : BlockTreeInsertOptions.None;

        AddBlockResult insertOutcome = _blockTree.IsKnownBlock(header.Number, header.Hash)
            ? AddBlockResult.AlreadyKnown
            : _blockTree.Insert(header, options);
        // Found existing block in the block tree
        if (insertOutcome == AddBlockResult.AlreadyKnown)
        {
            if ((_blockTree.LowestInsertedHeader?.Number ?? _syncConfig.PivotNumberParsed) > 0)
            {
                if (_blockTree.LowestInsertedHeader != null
                    && _blockTree.LowestInsertedHeader.Number < (_blockTree.LowestInsertedBeaconHeader?.Number ?? long.MaxValue))
                {
                    if (_logger.IsInfo)
                        _logger.Info(
                            " BeaconHeader LowestInsertedBeaconHeader found existing chain in fast sync," +
                            $"old: {_blockTree.LowestInsertedBeaconHeader?.Number}, new: {_blockTree.LowestInsertedHeader.Number}");
                    _blockTree.LowestInsertedBeaconHeader = _blockTree.LowestInsertedHeader;
                }
            }
            else
            {
                // lowest block header in chain in archive sync
                if (_logger.IsInfo)
                    _logger.Info(
                        " BeaconHeader LowestInsertedBeaconHeader found existing chain in archive sync," +
                        $"old: {_blockTree.LowestInsertedBeaconHeader?.Number}, new: {_blockTree.Genesis ?.Number}");
                _blockTree.LowestInsertedBeaconHeader = _blockTree.Genesis;
            }
            
            _mergedChain = true;
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
                _nextHeaderDiff = header.TotalDifficulty != null && header.TotalDifficulty >= header.Difficulty ? header.TotalDifficulty - header.Difficulty : null;
            }
        }

        _logger.Info($"New header {header.ToString(BlockHeader.Format.FullHashAndNumber)} in beacon headers sync. InsertOutcome: {insertOutcome}");
        return insertOutcome;
    }
}
