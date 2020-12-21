//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Witness
{
    public class WitnessBlockSyncFeed : SyncFeed<WitnessBlockSyncBatch?>
    {
        private readonly IBlockTree _blockTree;
        private readonly IWitnessStateSyncFeed _witnessStateSyncFeed;
        private readonly ConcurrentStack<BlockHeader> _blockHashes = new ConcurrentStack<BlockHeader>();
        private readonly ILogger _logger;

        public WitnessBlockSyncFeed(IBlockTree blockTree, IWitnessStateSyncFeed witnessStateSyncFeed, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _witnessStateSyncFeed = witnessStateSyncFeed ?? throw new ArgumentNullException(nameof(witnessStateSyncFeed));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            blockTree.NewSuggestedBlock += OnNewSuggestedBlock;
        }

        private void OnNewSuggestedBlock(object? sender, BlockEventArgs e)
        {
            // TODO: Check if block not too old?
            _blockHashes.Push(e.Block.Header); 
        }

        public override Task<WitnessBlockSyncBatch?> PrepareRequest()
        {
            if (_blockHashes.TryPop(out var blockHeader) && blockHeader.Hash != null)
            {
                Interlocked.Increment(ref Metrics.WitnessBlockRequests);
                return Task.FromResult<WitnessBlockSyncBatch?>(new WitnessBlockSyncBatch(blockHeader.Hash));
            }
            else
            {
                return Task.FromResult<WitnessBlockSyncBatch?>(null);
            }
        }

        public override SyncResponseHandlingResult HandleResponse(WitnessBlockSyncBatch? batch)
        {
            if (batch == null)
            {
                if(_logger.IsDebug) _logger.Debug($"{nameof(WitnessBlockSyncFeed)} received a NULL batch as a response");
                return SyncResponseHandlingResult.InternalError;
            }

            _witnessStateSyncFeed.AddBlockBatch(batch);
            return SyncResponseHandlingResult.OK;
        }

        public override bool IsMultiFeed => false;
        
        public override AllocationContexts Contexts => AllocationContexts.Blocks; // TODO: ?
    }
}
