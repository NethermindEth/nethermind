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

        return SyncResponseHandlingResult.OK;
    }

    public override bool IsMultiFeed => false;
    public override AllocationContexts Contexts => AllocationContexts.Blocks;
    protected override SyncMode ActivationSyncModes { get; } = SyncMode.BeaconFullSync;
}
