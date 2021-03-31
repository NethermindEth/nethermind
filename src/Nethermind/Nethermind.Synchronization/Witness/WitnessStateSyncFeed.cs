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
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Witness
{
    public class WitnessStateSyncFeed : SyncFeed<StateSyncBatch?>, IWitnessStateSyncFeed
    {
        private readonly IDb _db;
        private readonly int _maxRequestSize;
        private readonly ConcurrentStack<WitnessBlockSyncBatch> _blockSyncBatches = new();
        private readonly ConcurrentQueue<StateSyncBatch> _retryBatches = new();
        private readonly ILogger _logger;
        private const int RetryCount = 10;

        public WitnessStateSyncFeed(IDb db, ILogManager logManager, int maxRequestSize = StateSyncFeed.MaxRequestSize)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _maxRequestSize = maxRequestSize;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public override Task<StateSyncBatch?> PrepareRequest()
        {
            List<StateSyncItem> batchElements = new(_maxRequestSize);
            int freeSpace = _maxRequestSize;
            
            while (freeSpace > 0 && _blockSyncBatches.TryPop(out var batch))
            {
                Memory<Keccak> items = batch.Response!.Value;
                int itemsToTake = Math.Min(freeSpace, items.Length);
                batchElements.AddRange(MemoryMarshal.ToEnumerable<Keccak>(items.Slice(0, itemsToTake))
                    .Select(GetStateSyncItem));

                if (itemsToTake != items.Length) // still items left
                {
                    batch.Response = items.Slice(itemsToTake); 
                    _blockSyncBatches.Push(batch);
                }
                
                freeSpace = _maxRequestSize - batchElements.Count;
                
                if (_logger.IsTrace) _logger.Trace($"Preparing to download state with witness for block {batch.BlockNumber} ({batch.BlockHash}) with {itemsToTake} elements.");
            }

            if (batchElements.Count != 0)
            {
                Interlocked.Increment(ref Metrics.WitnessStateRequests);
                return Task.FromResult<StateSyncBatch?>(CreateBatch(batchElements.ToArray()));
            }
            else if (_retryBatches.TryDequeue(out var retryBatch))
            {
                return Task.FromResult<StateSyncBatch?>(retryBatch);
            }
            else
            {
                FallAsleep();
                return Task.FromResult<StateSyncBatch?>(null);
            }
        }

        private StateSyncBatch CreateBatch(StateSyncItem[] batchElements) => new StateSyncBatchWithRetries(batchElements) {ConsumerId = FeedId};

        private static StateSyncItem GetStateSyncItem(Keccak key) => new(key, NodeDataType.State);

        public override SyncResponseHandlingResult HandleResponse(StateSyncBatch? batch)
        {
            IEnumerable<StateSyncItem> Consume(byte[]?[] bytes)
            {
                for (int i = 0; i < Math.Min(bytes.Length, batch.RequestedNodes.Length); i++)
                {
                    Keccak key = batch.RequestedNodes[i].Hash;
                    if (bytes[i] != null)
                    {
                        if (Keccak.Compute(bytes[i]) == key)
                        {
                            _db.Set(key, bytes[i]);
                        }
                        else
                        {
                            // return missing keys
                            yield return GetStateSyncItem(key);
                        }
                    }
                }
            }

            if (batch == null)
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(WitnessStateSyncFeed)} received a NULL batch as a response.");
                return SyncResponseHandlingResult.InternalError;
            }

            if (batch.ConsumerId != FeedId)
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(WitnessStateSyncFeed)} response sent by feed {batch.ConsumerId} came back to feed {FeedId}");
                return SyncResponseHandlingResult.InternalError;
            }

            int consumed = 0;
            byte[]?[]? data = batch.Responses;
            StateSyncBatch? retryBatch = null;
            if (data != null)
            {
                StateSyncItem[] missing = Consume(data).ToArray();
                consumed = data.Length - missing.Length;
                if (missing.Length > 0)
                {
                    // this can happen if we ask for outdated block? 
                    if (_logger.IsTrace) _logger.Trace($"Received {missing.Length} nodes data which does not match the hashes. Retrying...");
                    
                    retryBatch = CreateBatch(missing);
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Failed to receive nodes data which does not match the hashes. Retrying...");
                retryBatch = batch;
            }
            
            if (retryBatch is StateSyncBatchWithRetries batchWithRetries)
            {
                if (batchWithRetries.Retry++ > RetryCount)
                {
                    retryBatch = null;
                }
            }

            if (retryBatch != null)
            {
                _retryBatches.Enqueue(retryBatch);
                Activate();
            }

            if (_logger.IsTrace) _logger.Trace($"Downloaded state with witness. Received {consumed}/{batch.RequestedNodes.Length} elements.");
            return consumed == 0 ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.OK;
        }

        public override bool IsMultiFeed => true;
        
        public override AllocationContexts Contexts => AllocationContexts.State;
        
        public void AddBlockBatch(WitnessBlockSyncBatch batch)
        {
            if (batch.Response?.Length > 0)
            {
                _blockSyncBatches.Push(batch);
                Activate();
                if (_logger.IsTrace) _logger.Trace($"Registered block {batch.BlockNumber} ({batch.BlockHash}) for downloading witness state.");
            }
        }

        private class StateSyncBatchWithRetries : StateSyncBatch
        {
            public StateSyncBatchWithRetries(StateSyncItem[] requestedNodes) : base(requestedNodes)
            {
            }

            public int Retry { get; set; }
        }
    }
}
