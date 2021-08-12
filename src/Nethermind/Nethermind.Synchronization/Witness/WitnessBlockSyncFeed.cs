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
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly SortedSet<WitnessBlockSyncBatch> _blockHashes = new(new WitnessBlockSyncBatchComparer());
        private readonly ILogger _logger;
        private const int FollowDelta = 256;
        private static readonly TimeSpan _minRetryDelay = TimeSpan.FromMilliseconds(100);
        private const int MaxRetries = 6;
        private const int MaxBlocksInQueue = 10;

        public WitnessBlockSyncFeed(IBlockTree blockTree, IWitnessStateSyncFeed witnessStateSyncFeed, ISyncModeSelector syncModeSelector, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _witnessStateSyncFeed = witnessStateSyncFeed ?? throw new ArgumentNullException(nameof(witnessStateSyncFeed));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            blockTree.NewSuggestedBlock += OnNewSuggestedBlock;
        }

        private void OnNewSuggestedBlock(object? sender, BlockEventArgs e)
        {
            Block block = e.Block;
            bool isBeamSync = (_syncModeSelector.Current & SyncMode.Beam) != 0;
            bool blockNotToOld = (_blockTree.Head?.Number ?? 0) - block.Number < FollowDelta;
            bool blockHasWitness = block.Transactions.Length > 0;
            if (block.Hash != null && isBeamSync && blockNotToOld && blockHasWitness)
            {
                lock (_blockHashes)
                {
                    _blockHashes.Add(new WitnessBlockSyncBatch(block.Hash, block.Number, DateTime.MinValue));
                }
                
                Activate();
                if (_logger.IsTrace) _logger.Trace($"Registered block {block.ToString(Block.Format.FullHashAndNumber)} for downloading witness. {_blockHashes.Count} blocks in queue.");
            }
        }

        public override Task<WitnessBlockSyncBatch?> PrepareRequest()
        {
            WitnessBlockSyncBatch? witnessBlockSyncBatch = null;
            if (_blockHashes.Count > 0)
            {
                lock (_blockHashes)
                {
                    witnessBlockSyncBatch = _blockHashes.Min;
                    if (witnessBlockSyncBatch != null)
                    {
                        TimeSpan timeSinceLastTried = DateTime.UtcNow - witnessBlockSyncBatch.Timestamp;
                        if (timeSinceLastTried >= _minRetryDelay)
                        {
                            _blockHashes.Remove(witnessBlockSyncBatch);
                        }
                        else
                        {
                            witnessBlockSyncBatch = null;
                        }
                    }
                }
            }

            if (witnessBlockSyncBatch != null)
            {
                Interlocked.Increment(ref Metrics.WitnessBlockRequests);
                if (_logger.IsTrace) _logger.Trace($"Preparing to download witness for block {witnessBlockSyncBatch.BlockNumber} ({witnessBlockSyncBatch.BlockHash}). {_blockHashes.Count} blocks in queue.");
                return Task.FromResult<WitnessBlockSyncBatch?>(witnessBlockSyncBatch);
            }
            else
            {
                FallAsleep();
                return Task.FromResult<WitnessBlockSyncBatch?>(null);
            }
        }

        public override SyncResponseHandlingResult HandleResponse(WitnessBlockSyncBatch? batch)
        {
            void Retry(int retryForward)
            {
                bool blockAfterOrHead = _blockTree.Head?.Number <= batch.BlockNumber;
                bool isMainChain = _blockTree.IsMainChain(batch.BlockHash);
                bool isBeamSync = (_syncModeSelector.Current & SyncMode.Beam) != 0;
                if (batch.Retry < MaxRetries && (blockAfterOrHead || isMainChain) && isBeamSync && _blockHashes.Count < MaxBlocksInQueue)
                {
                    batch.Retry += retryForward;
                    batch.Timestamp = DateTime.UtcNow;

                    lock (_blockHashes)
                    {
                        _blockHashes.Add(batch);
                    }
                }
            }

            if (batch == null)
            {
                if(_logger.IsDebug) _logger.Debug($"{nameof(WitnessBlockSyncFeed)} received a NULL batch as a response");
                return SyncResponseHandlingResult.InternalError;
            }
            
            if (batch.Response == null)
            {
                if(_logger.IsDebug) _logger.Debug($"{nameof(WitnessBlockSyncFeed)} received a batch with NULL response for block {batch.BlockNumber} ({batch.BlockHash})");
                Retry(1);
                
                // not assigned peer
                return SyncResponseHandlingResult.NotAssigned;
            }

            if (batch.Response.Value.Length == 0)
            {
                if(_logger.IsDebug) _logger.Debug($"{nameof(WitnessBlockSyncFeed)} received a batch with 0 elements for block {batch.BlockNumber} ({batch.BlockHash})");
                Retry(2);
                
                // this can be ok if this block is not canonical
                // we don't want to punish peer
                return SyncResponseHandlingResult.OK;
            }

            if (_logger.IsTrace) _logger.Trace($"Downloaded witness for block {batch.BlockNumber} ({batch.BlockHash}) with {batch.Response.Value.Length} elements.");
            _witnessStateSyncFeed.AddBlockBatch(batch);
            return SyncResponseHandlingResult.OK;
        }

        public override bool IsMultiFeed => false;
        
        public override AllocationContexts Contexts => AllocationContexts.Witness;

        private class WitnessBlockSyncBatchComparer : IComparer<WitnessBlockSyncBatch>
        {
            public int Compare(WitnessBlockSyncBatch? x, WitnessBlockSyncBatch? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (ReferenceEquals(null, y)) return 1;
                if (ReferenceEquals(null, x)) return -1;
                
                // move retried for later
                int retryComparison = x.Retry.CompareTo(y.Retry);
                if (retryComparison != 0) return retryComparison;

                // higher block number first
                int blockNumberComparison = y.BlockNumber.CompareTo(x.BlockNumber);
                if (blockNumberComparison != 0) return blockNumberComparison;
                
                // earlier retries first
                return x.Timestamp.CompareTo(y.Timestamp);
            }
        }
    }
}
