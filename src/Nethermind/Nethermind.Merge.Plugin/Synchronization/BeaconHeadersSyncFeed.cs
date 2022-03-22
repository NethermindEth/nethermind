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
    
    protected override BlockHeader? LowestInsertedBlockHeader => _blockTree.LowestInsertedBeaconHeader;
    protected override long HeadersDestinationBlockNumber => _pivot.PivotDestinationNumber;
    protected override MeasuredProgress HeadersSyncProgressReport => _syncReport.BeaconHeaders;
    public BeaconHeadersSyncFeed(
        ISyncModeSelector syncModeSelector,
        IBlockTree? blockTree,
        ISyncPeerPool? syncPeerPool,
        ISyncConfig? syncConfig,
        ISyncReport? syncReport,
        IPivot? pivot,
        IMergeConfig? mergeConfig,
        ILogManager? logManager) 
        : base(syncModeSelector, blockTree, syncPeerPool, syncConfig, syncReport, logManager, true) // alwaysStartHeaderSync = true => for the merge we're forcing header sync start. It doesn't matter if it is archive sync or fast sync
    {
        _pivot = pivot ?? throw new ArgumentNullException(nameof(pivot));
        _mergeConfig = mergeConfig ?? throw new ArgumentNullException(nameof(mergeConfig));
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
        UInt256? startTotalDifficulty = lowestInserted?.TotalDifficulty ?? _pivot.PivotTotalDifficulty 
            ?? _mergeConfig.FinalTotalDifficultyParsed;
        
        _nextHeaderHash = startHeaderHash;
        _nextHeaderDiff = startTotalDifficulty;
        
        _lowestRequestedHeaderNumber = startNumber + 1;   
    }

    protected override void PostFinishCleanUp()
    {
        base.PostFinishCleanUp();
        // TODO: beaconsync additional checks on total difficulty
        if (_mergeConfig.FinalTotalDifficultyParsed == null)
        {
            // set total difficulty as beacon pivot does not provide total difficulty
            _blockTree.BackFillTotalDifficulty(HeadersDestinationBlockNumber, _pivotNumber);   
        }
    }
    
    protected override AddBlockResult InsertToBlockTree(BlockHeader header)
    {
        BlockTreeInsertOptions options = _nextHeaderDiff is null
            ? BlockTreeInsertOptions.TotalDifficultyNotNeeded | BlockTreeInsertOptions.SkipUpdateBestPointers
            : BlockTreeInsertOptions.None;
        AddBlockResult insertOutcome = _blockTree.Insert(header, options);
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

        return insertOutcome;
    }
}
