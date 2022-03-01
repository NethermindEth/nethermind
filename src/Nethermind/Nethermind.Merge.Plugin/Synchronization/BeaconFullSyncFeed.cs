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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
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
    private readonly IBlockProcessingQueue _blockProcessingQueue;
    private readonly ILogger _logger;
    
    public BeaconFullSyncFeed(
        ISyncModeSelector syncModeSelector,
        IBlockCacheService blockCacheService,
        IBlockTree blockTree,
        ISyncProgressResolver syncProgressResolver,
        IBlockValidator blockValidator,
        IBlockProcessingQueue blockProcessingQueue,
        ILogManager logManager)
        : base(syncModeSelector)
    {
        _blockCacheService = blockCacheService;
        _blockTree = blockTree;
        _syncProgressResolver = syncProgressResolver;
        _blockValidator = blockValidator;
        _blockProcessingQueue = blockProcessingQueue;
        _logger = logManager.GetClassLogger();
    }

    public override void InitializeFeed()
    {
        _blockTree.BackFillTotalDifficulty(_blockCacheService.Peek()?.Number ?? 0, _blockCacheService.ProcessDestination.Number);
        // TODO: beaconsync use block ref and handle null headers / blocks
        Stack<Block> stack = new();
        Block? current = _blockTree.FindBlock(_blockCacheService.ProcessDestination.Hash ?? _blockCacheService.ProcessDestination.CalculateHash(), BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        BlockHeader parentHeader = _blockTree.FindHeader(_blockCacheService.ProcessDestination.ParentHash);
        current.Header.TotalDifficulty = parentHeader.TotalDifficulty + current.Difficulty;
        long state = _syncProgressResolver.FindBestFullState();
        bool shouldProcess = _blockCacheService.ProcessDestination.Number > state && state != 0;
        if (shouldProcess)
        {
            while (current.Number != state)
            {
                stack.Push(current);
                current = _blockTree.FindBlock(parentHeader.Hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                parentHeader = _blockTree.FindHeader(current.ParentHash);
                current.Header.TotalDifficulty = parentHeader.TotalDifficulty + current.Difficulty;
            }

            while (stack.TryPop(out Block block))
            {
                _blockProcessingQueue.Enqueue(block, ProcessingOptions.None);
            }
        }
    }

    public override Task<BlockHeader?> PrepareRequest()
    {
        return Task.FromResult(_blockCacheService.DequeueBlockHeader());
    }

    public override SyncResponseHandlingResult HandleResponse(BlockHeader? response)
    {
        if (response is null)
        {
            return SyncResponseHandlingResult.Emptish;
        }
        
        // long state = _syncProgressResolver.FindBestFullState();
        // bool shouldProcess = response.Number > state && state != 0;
        // if (shouldProcess)
        // {
        //     Block? block = _blockTree.FindBlock(response.Hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        //     if (block == null)
        //     {
        //         if (_logger.IsWarn)
        //             _logger.Warn(
        //                 $"Could not find block {response.ToString(BlockHeader.Format.FullHashAndNumber)} for beacon full sync");
        //         return SyncResponseHandlingResult.InternalError;
        //     }
        //     BlockHeader parentHeader = _blockTree.FindHeader(response.ParentHash);
        //     block.Header.TotalDifficulty = parentHeader.TotalDifficulty + block.Difficulty;
        //     _blockProcessingQueue.Enqueue(block, ProcessingOptions.None);
        // }

        return SyncResponseHandlingResult.OK;
    }

    public override bool IsMultiFeed => false;
    public override AllocationContexts Contexts => AllocationContexts.Blocks;
    protected override SyncMode ActivationSyncModes { get; } = SyncMode.BeaconFullSync;
}
