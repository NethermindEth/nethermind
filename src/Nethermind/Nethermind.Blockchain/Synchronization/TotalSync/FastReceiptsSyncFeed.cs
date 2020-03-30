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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization.FastBlocks;
using Nethermind.Blockchain.Synchronization.SyncLimits;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State.Proofs;

namespace Nethermind.Blockchain.Synchronization.TotalSync
{
    public class FastReceiptsSyncFeed : SyncFeed<FastBlocksBatch>
    {
        private int ReceiptsRequestSize = GethSyncLimits.MaxReceiptFetch;

        private ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly object _handlerLock = new object();

        private ConcurrentDictionary<long, List<(Block, TxReceipt[])>> _receiptDependencies = new ConcurrentDictionary<long, List<(Block, TxReceipt[])>>();
        private ConcurrentDictionary<FastBlocksBatch, object> _sentBatches = new ConcurrentDictionary<FastBlocksBatch, object>();
        private ConcurrentStack<FastBlocksBatch> _pendingBatches = new ConcurrentStack<FastBlocksBatch>();

        private object _empty = new object();

        private Keccak _startReceiptsHash;

        private Keccak _lowestRequestedReceiptsHash;

        private long _pivotNumber;
        private Keccak _pivotHash;

        public bool IsFinished =>
            _pendingBatches.Count
            + _sentBatches.Count
            + _receiptDependencies.Count == 0;

        public FastReceiptsSyncFeed(ISpecProvider specProvider, IBlockTree blockTree, IReceiptStorage receiptStorage, IEthSyncPeerPool syncPeerPool, ISyncConfig syncConfig, ISyncReport syncReport, ILogManager logManager)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private bool _isMoreLikelyToBeHandlingDependenciesNow;

        private FastBlocksBatchType ResolveBatchType()
        {
            if (_syncConfig.BeamSync && _blockTree.LowestInsertedHeader != null)
            {
                ChangeState(SyncFeedState.Finished);
                return FastBlocksBatchType.None;
            }

            bool receiptsDownloaded = _receiptStorage.LowestInsertedReceiptBlock == 1;

            if (!receiptsDownloaded
                && _syncConfig.DownloadReceiptsInFastSync)
            {
                return _lowestRequestedReceiptsHash == _blockTree.Genesis.Hash
                    ? FastBlocksBatchType.None
                    : FastBlocksBatchType.Receipts;
            }

            _syncReport.FastBlocksReceipts.Update(_pivotNumber);
            _syncReport.FastBlocksReceipts.MarkEnd();
            ChangeState(SyncFeedState.Finished);

            return FastBlocksBatchType.None;
        }

        public override Task<FastBlocksBatch> PrepareRequest()
        {
            if (!_isMoreLikelyToBeHandlingDependenciesNow)
            {
                _isMoreLikelyToBeHandlingDependenciesNow = true;
                try
                {
                    HandleDependentBatches();
                }
                finally
                {
                    _isMoreLikelyToBeHandlingDependenciesNow = false;
                }
            }

            FastBlocksBatch batch;
            if (_pendingBatches.Any())
            {
                _pendingBatches.TryPop(out batch);
                batch.MarkRetry();
            }
            else
            {
                FastBlocksBatchType fastBlocksBatchType = ResolveBatchType();
                switch (fastBlocksBatchType)
                {
                    case FastBlocksBatchType.None:
                    {
                        /* finish this sync round
                           possibly continue in the next sync round */
                        return Task.FromResult((FastBlocksBatch)null);
                    }

                    case FastBlocksBatchType.Receipts:
                    {
                        Keccak hash = _lowestRequestedReceiptsHash;
                        Block predecessorBlock = _blockTree.FindBlock(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                        Block block = predecessorBlock;
                        if (block == null)
                        {
                            return Task.FromResult((FastBlocksBatch)null);
                        }

                        if (_lowestRequestedReceiptsHash != _pivotHash)
                        {
                            if (block.ParentHash == _blockTree.Genesis.Hash)
                            {
                                return Task.FromResult((FastBlocksBatch)null);
                            }

                            block = _blockTree.FindParent(predecessorBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                            if (block == null)
                            {
                                return Task.FromResult((FastBlocksBatch)null);
                            }
                        }
                        else
                        {
                            predecessorBlock = null;
                        }

                        int requestSize = (int) Math.Min(block.Number, ReceiptsRequestSize);
                        batch = new FastBlocksBatch();
                        batch.Receipts = new ReceiptsSyncBatch();
                        batch.Receipts.Predecessors = new long?[requestSize];
                        batch.Receipts.Blocks = new Block[requestSize];
                        batch.Receipts.Request = new Keccak[requestSize];
                        batch.MinNumber = block.Number;
                        if (_receiptStorage.LowestInsertedReceiptBlock - block.Number < 1024)
                        {
                            batch.Prioritized = true;
                        }

                        int collectedRequests = 0;
                        while (collectedRequests < requestSize)
                        {
                            _lowestRequestedReceiptsHash = block.Hash;
                            if (block.Transactions.Length > 0)
                            {
                                batch.Receipts.Predecessors[collectedRequests] = predecessorBlock?.Number;
                                batch.Receipts.Blocks[collectedRequests] = block;
                                batch.Receipts.Request[collectedRequests] = block.Hash;
                                predecessorBlock = block;
                                collectedRequests++;
                            }

                            block = _blockTree.FindBlock(block.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                            if (block == null || block.IsGenesis)
                            {
                                break;
                            }
                        }

                        if (collectedRequests < requestSize)
                        {
                            Block[] currentBlocks = batch.Receipts.Blocks;
                            Keccak[] currentRequests = batch.Receipts.Request;
                            batch.Receipts.Blocks = new Block[collectedRequests];
                            batch.Receipts.Request = new Keccak[collectedRequests];
                            Array.Copy(currentBlocks, 0, batch.Receipts.Blocks, 0, collectedRequests);
                            Array.Copy(currentRequests, 0, batch.Receipts.Request, 0, collectedRequests);
                            batch.Receipts.IsFinal = true;
                        }

                        if (collectedRequests == 0 && _blockTree.LowestInsertedBody.Number == 1 && (block?.IsGenesis ?? true))
                        {
                            // special finishing call
                            // leaving this the bad way as it may be tricky to confirm that it is not called somewhere else
                            // at least I will add a test for it now...
                            _receiptStorage.LowestInsertedReceiptBlock = 1;
                            return Task.FromResult((FastBlocksBatch)null);
                        }

                        break;
                    }

                    default:
                        throw new NotSupportedException($"{nameof(FastBlocksBatchType)}.{nameof(fastBlocksBatchType)} not supported");
                }
            }

            _sentBatches.TryAdd(batch, _empty);
            if (batch.Headers != null && batch.Headers.StartNumber >= ((_blockTree.LowestInsertedHeader?.Number ?? 0) - 2048))
            {
                batch.Prioritized = true;
            }

            return Task.FromResult(batch);
        }

        public override SyncBatchResponseHandlingResult HandleResponse(FastBlocksBatch batch)
        {
            if (batch.IsResponseEmpty)
            {
                batch.MarkHandlingStart();
                if (_logger.IsTrace) _logger.Trace($"{batch} - came back EMPTY");
                batch.Allocation = null;
                _pendingBatches.Push(batch);
                batch.MarkHandlingEnd();
                return SyncBatchResponseHandlingResult.NoData; //(BlocksDataHandlerResult.OK, 0);
            }

            try
            {
                switch (batch.BatchType)
                {
                    case FastBlocksBatchType.Receipts:
                    {
                        batch.MarkHandlingStart();
                        int added = InsertReceipts(batch);
                        return SyncBatchResponseHandlingResult.OK; //(BlocksDataHandlerResult.OK, added);
                    }

                    default:
                    {
                        return SyncBatchResponseHandlingResult.InvalidFormat; // (BlocksDataHandlerResult.InvalidFormat, 0);
                    }
                }
            }
            finally
            {
                batch.MarkHandlingEnd();
                _sentBatches.TryRemove(batch, out _);
            }
        }

        public override void Activate()
        {
            if (!_syncConfig.FastBlocks)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = LongConverter.FromString(_syncConfig.PivotNumber ?? "0x0");
            _pivotHash = _syncConfig.PivotHash == null ? null : new Keccak(_syncConfig.PivotHash);

            _startReceiptsHash = _blockTree.FindHash(_receiptStorage.LowestInsertedReceiptBlock ?? long.MaxValue) ?? _pivotHash;

            _lowestRequestedReceiptsHash = _startReceiptsHash;

            _sentBatches.Clear();
            _pendingBatches.Clear();
            _receiptDependencies.Clear();
            
            ChangeState(SyncFeedState.Active);
        }

        private void HandleDependentBatches()
        {
            long? lowestReceiptNumber = _receiptStorage.LowestInsertedReceiptBlock;
            while (lowestReceiptNumber.HasValue && _receiptDependencies.ContainsKey(lowestReceiptNumber.Value))
            {
                InsertReceipts(_receiptDependencies[lowestReceiptNumber.Value]);
                _receiptDependencies.Remove(lowestReceiptNumber.Value, out _);
                lowestReceiptNumber = _receiptStorage.LowestInsertedReceiptBlock;
            }
        }

         private int InsertReceipts(FastBlocksBatch batch)
        {
            ReceiptsSyncBatch receiptSyncBatch = batch.Receipts;
            int added = 0;
            long? lastPredecessor = null;
            List<(Block, TxReceipt[])> validReceipts = new List<(Block, TxReceipt[])>();

            if (receiptSyncBatch.Response.Any() && receiptSyncBatch.Response[0] != null)
            {
                lastPredecessor = receiptSyncBatch.Predecessors[0];
            }

            for (int blockIndex = 0; blockIndex < receiptSyncBatch.Response.Length; blockIndex++)
            {
                TxReceipt[] blockReceipts = receiptSyncBatch.Response[blockIndex];
                if (blockReceipts == null)
                {
                    break;
                }

                Block block = receiptSyncBatch.Blocks[blockIndex];

                bool wasInvalid = false;
                for (int receiptIndex = 0; receiptIndex < blockReceipts.Length; receiptIndex++)
                {
                    TxReceipt receipt = blockReceipts[receiptIndex];
                    if (receipt == null)
                    {
                        wasInvalid = true;
                        break;
                    }

                    receipt.TxHash = block
                        .Transactions[receiptIndex]
                        .Hash;
                }

                if (!wasInvalid)
                {
                    Keccak receiptsRoot = new ReceiptTrie(block.Number, _specProvider, blockReceipts).RootHash;
                    if (receiptsRoot != block.ReceiptsRoot)
                    {
                        if (_logger.IsWarn) _logger.Warn($"{batch} - invalid receipt root");
                        _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource, "invalid receipts root");
                        wasInvalid = true;
                    }
                }

                if (!wasInvalid)
                {
                    validReceipts.Add((block, blockReceipts));
                    added++;
                }
                else
                {
                    break;
                }
            }

            if (added < receiptSyncBatch.Request.Length)
            {
                FastBlocksBatch fillerBatch = PrepareReceiptFiller(added, receiptSyncBatch);
                _pendingBatches.Push(fillerBatch);
            }

            lock (_handlerLock)
            {
                if (added > 0)
                {
                    if (added == receiptSyncBatch.Request.Length && receiptSyncBatch.IsFinal)
                    {
                        if (validReceipts.All(i => i.Item1.Number != 1))
                        {
                            validReceipts.Add((_blockTree.FindBlock(1), Array.Empty<TxReceipt>()));
                        }
                    }
                    
                    if (lastPredecessor.HasValue && lastPredecessor.Value != _receiptStorage.LowestInsertedReceiptBlock)
                    {
                        _receiptDependencies.TryAdd(lastPredecessor.Value, validReceipts);
                    }
                    else
                    {
                        InsertReceipts(validReceipts);
                    }
                }

                if (_receiptStorage.LowestInsertedReceiptBlock != null)
                {
                    _syncReport.FastBlocksPivotNumber = _pivotNumber;
                    _syncReport.FastBlocksReceipts.Update(_pivotNumber - (_receiptStorage.LowestInsertedReceiptBlock ?? _pivotNumber) + 1);
                }

                if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_receiptStorage.LowestInsertedReceiptBlock} | HANDLED {batch}");

                _syncReport.ReceiptsInQueue.Update(_receiptDependencies.Sum(d => d.Value.Count));
                return added;
            }
        }
        
        private void InsertReceipts(List<(Block, TxReceipt[])> receipts)
        {
            for (int i = 0; i < receipts.Count; i++)
            {
                (Block block, var txReceipts) = receipts[i];
                _receiptStorage.Insert(block, txReceipts);
            }
        }

        private static FastBlocksBatch PrepareReceiptFiller(int added, ReceiptsSyncBatch receiptsSyncBatch)
        {
            int requestSize = receiptsSyncBatch.Blocks.Length;
            FastBlocksBatch filler = new FastBlocksBatch();
            filler.Receipts = new ReceiptsSyncBatch();
            filler.Receipts.Predecessors = new long?[requestSize - added];
            filler.Receipts.Blocks = new Block[requestSize - added];
            filler.Receipts.Request = new Keccak[requestSize - added];
            int fillerIndex = 0;
            for (int missingIndex = added; missingIndex < requestSize; missingIndex++)
            {
                filler.Receipts.Predecessors[fillerIndex] = receiptsSyncBatch.Predecessors[missingIndex];
                filler.Receipts.Blocks[fillerIndex] = receiptsSyncBatch.Blocks[missingIndex];
                filler.Receipts.Request[fillerIndex] = receiptsSyncBatch.Request[missingIndex];
                fillerIndex++;
            }

            return filler;
        }
    }
}