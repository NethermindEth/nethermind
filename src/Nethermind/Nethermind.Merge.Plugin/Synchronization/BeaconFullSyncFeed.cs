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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class BeaconFullSyncFeed : ActivatedSyncFeed<BlockHeader?>
{
    private readonly IBlockCacheService _blockCacheService;
    private readonly IBlockTree _blockTree;
    private readonly ISyncProgressResolver _syncProgressResolver;
    private readonly IBlockValidator _blockValidator;
    private readonly ILogger _logger;

    public BeaconFullSyncFeed(
        ISyncModeSelector syncModeSelector,
        IBlockCacheService blockCacheService,
        IBlockTree blockTree,
        ISyncProgressResolver syncProgressResolver,
        IBlockValidator blockValidator,
        ILogManager logManager)
        : base(syncModeSelector)
    {
        _blockCacheService = blockCacheService;
        _blockTree = blockTree;
        _syncProgressResolver = syncProgressResolver;
        _blockValidator = blockValidator;
        _logger = logManager.GetClassLogger();
    }

    public override void InitializeFeed()
    {
        // BlockHeader? blockHeader = _blockCacheService.Peek();
        // long header = _syncProgressResolver.FindBestHeader();
        // while (blockHeader != null && header >= blockHeader.Number)
        // {
        //     blockHeader = _blockCacheService.DequeueBlockHeader();
        // }
    }

    public override Task<BlockHeader?> PrepareRequest()
    {
        // if (_blockCacheService.IsEmpty)
        // {
        //     Finish();
        // }
        
        return Task.FromResult(_blockCacheService.DequeueBlockHeader());
    }

    public override SyncResponseHandlingResult HandleResponse(BlockHeader? response)
    {
        if (response is null)
        {
            return SyncResponseHandlingResult.Emptish;
        }
        
        long state = _syncProgressResolver.FindBestFullState();
        bool shouldProcess = response.Number > state && state != 0;

        BlockHeader header = _blockTree.FindHeader(response.Hash);
        if (header == null)
        {
            _blockTree.SuggestHeader(response);
        }
        Block? block = _blockTree.FindBlock(response.Hash);
        if (block == null)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Could not find block {response.ToString(BlockHeader.Format.FullHashAndNumber)} for beacon full sync");
            return SyncResponseHandlingResult.InternalError;
        }

        if (!_blockValidator.ValidateSuggestedBlock(block))
        {
            if (_logger.IsWarn)
                _logger.Warn($"Block validator rejected the block {block.ToString(Block.Format.FullHashAndNumber)}");
            return SyncResponseHandlingResult.InternalError;
        }
        
        _blockTree.SuggestBlock(block, shouldProcess);
        return SyncResponseHandlingResult.OK;
    }

    public override bool IsMultiFeed => false;
    public override AllocationContexts Contexts => AllocationContexts.Blocks;
    protected override SyncMode ActivationSyncModes { get; } = SyncMode.BeaconFullSync;
}
