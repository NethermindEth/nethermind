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
using System.Text;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SyncLimits;

namespace Nethermind.Synchronization.FastBlocks
{
    public class FastReceiptsSyncFeed : SyncFeed<ReceiptsSyncBatch>
    {
        private int _requestSize = GethSyncLimits.MaxReceiptFetch;

        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private readonly ISpecProvider _specProvider;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly object _handlerLock = new object();

        private ConcurrentDictionary<long, List<(Block, TxReceipt[])>> _dependencies =
            new ConcurrentDictionary<long, List<(Block, TxReceipt[])>>();

        private ConcurrentDictionary<ReceiptsSyncBatch, object> _sent =
            new ConcurrentDictionary<ReceiptsSyncBatch, object>();

        private ConcurrentQueue<ReceiptsSyncBatch> _pending =
            new ConcurrentQueue<ReceiptsSyncBatch>();

        private object _dummyObject = new object();

        private bool _hasRequestedFinalBatch;
        private Keccak _startHash;
        private Keccak _lowestRequestedHash;

        private long _pivotNumber;
        private Keccak _pivotHash;

        private bool ShouldFinish => !_syncConfig.DownloadReceiptsInFastSync || _receiptStorage.LowestInsertedReceiptBlock == 1;

        /// <summary>
        /// Not it is meant to be counting blocks and not receipts
        /// </summary>
        private long ReceiptsInQueue => _dependencies.Sum(d => d.Value.Count);

        public FastReceiptsSyncFeed(ISpecProvider specProvider, IBlockTree blockTree, IReceiptStorage receiptStorage, ISyncPeerPool syncPeerPool, ISyncConfig syncConfig, ISyncReport syncReport, ILogManager logManager)
            : base(logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));

            if (!_syncConfig.FastBlocks)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            if (!_syncConfig.UseGethLimitsInFastBlocks)
            {
                _requestSize = NethermindSyncLimits.MaxReceiptFetch;
            }

            _pivotNumber = _syncConfig.PivotNumberParsed;
            _pivotHash = _syncConfig.PivotHashParsed;

            long lowestInserted = _receiptStorage.LowestInsertedReceiptBlock ?? long.MaxValue;
            if (lowestInserted < _pivotNumber)
            {
                _startHash = _blockTree.FindHash(lowestInserted) ?? _pivotHash;
            }

            _startHash ??= _pivotHash;
            _lowestRequestedHash = _startHash;

            Activate();
        }

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.Receipts;

        private bool ShouldBuildANewBatch()
        {
            bool shouldDownloadReceipts = _syncConfig.DownloadReceiptsInFastSync;
            bool allReceiptsDownloaded = _receiptStorage.LowestInsertedReceiptBlock == 1;

            bool noBatchesLeft = !shouldDownloadReceipts
                                 || allReceiptsDownloaded
                                 || ReceiptsInQueue >= FastBlocksQueueLimits.ForReceipts;

            if (noBatchesLeft)
            {
                if (ShouldFinish)
                {
                    Finish();
                    PostFinishCleanUp();
                }

                return false;
            }

            return !_hasRequestedFinalBatch;
        }

        private void PostFinishCleanUp()
        {
            _syncReport.FastBlocksReceipts.Update(_pivotNumber);
            _syncReport.FastBlocksReceipts.MarkEnd();
            _sent.Clear();
            _pending.Clear();
            _dependencies.Clear();
            _syncReport.ReceiptsInQueue.Update(0L);
            _syncReport.ReceiptsInQueue.MarkEnd();
        }

        private ReceiptsSyncBatch BuildNewBatch()
        {
            long? lowestInsertedBody = _blockTree.LowestInsertedBody?.Number;
            long? lowestInsertedReceipt = _receiptStorage.LowestInsertedReceiptBlock;
            if (lowestInsertedBody != 1 &&
                (lowestInsertedBody ?? _pivotNumber) > (lowestInsertedReceipt ?? _pivotNumber) - 1024 * 32)
            {
                return null;
            }

            Block predecessorBlock = _blockTree.FindBlock(_lowestRequestedHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (predecessorBlock == null)
            {
                return null;
            }

            Block block;
            if (_lowestRequestedHash != _pivotHash)
            {
                // if we have already requested receipts of the block number 1 then we have no moe requests
                if (predecessorBlock.ParentHash == _blockTree.Genesis.Hash)
                {
                    return null;
                }

                block = _blockTree.FindParent(predecessorBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (block == null)
                {
                    return null;
                }
            }
            else
            {
                block = predecessorBlock;
                predecessorBlock = null;
            }

            int requestSize = (int) Math.Min(block.Number, _requestSize);
            ReceiptsSyncBatch batch = new ReceiptsSyncBatch();
            batch.Description = "NEW BUILD";
            batch.Predecessors = new long?[requestSize];
            batch.Blocks = new Block[requestSize];
            batch.Request = new Keccak[requestSize];
            batch.MinNumber = block.Number; // not really a min number...

            int collectedRequests = 0;
            while (collectedRequests < requestSize)
            {
                if (block.Transactions.Length > 0)
                {
                    _lowestRequestedHash = block.Hash;
                    batch.Predecessors[collectedRequests] = predecessorBlock?.Number;
                    batch.Blocks[collectedRequests] = block;
                    batch.Request[collectedRequests] = block.Hash;
                    predecessorBlock = block;
                    collectedRequests++;
                }

                block = _blockTree.FindBlock(block.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (block == null || block.IsGenesis)
                {
                    break;
                }
            }

            if (collectedRequests == 0 && _blockTree.LowestInsertedBody.Number == 1 && (block?.IsGenesis ?? true))
            {
                // special finishing call
                // leaving this the bad way as it may be tricky to confirm that it is not called somewhere else
                // at least I will add a test for it now...
                if (_sent.Count + _pending.Count + _dependencies.Count == 0)
                {
                    _receiptStorage.LowestInsertedReceiptBlock = 1;
                }

                return null;
            }

            if (collectedRequests == 0)
            {
                // in parallel sync the body may not yet be there
                return null;
            }

            if (collectedRequests < requestSize)
            {
                batch.Resize(collectedRequests);
                batch.IsFinal = _blockTree.LowestInsertedBody.Number == 1;
            }

            return batch;
        }

        public override Task<ReceiptsSyncBatch> PrepareRequest()
        {
            HandleDependentBatches();

            if (_pending.TryDequeue(out ReceiptsSyncBatch batch))
            {
                batch.MarkRetry();
            }
            else if (ShouldBuildANewBatch())
            {
                bool moreBodiesWaiting = (_blockTree.LowestInsertedBody?.Number ?? long.MaxValue)
                                         < (_receiptStorage.LowestInsertedReceiptBlock ?? long.MaxValue);
                if (moreBodiesWaiting)
                {
                    batch = BuildNewBatch();
                }
            }

            if (batch != null)
            {
                SetBatchPriority(batch);
                _sent.TryAdd(batch, _dummyObject);
                if (batch.IsFinal)
                {
                    _logger.Info($"Marking the receipts batch as final: {batch}");
                    _hasRequestedFinalBatch = true;
                }

                LogStateOnPrepare();
            }

            return Task.FromResult(batch);
        }

        private void SetBatchPriority(ReceiptsSyncBatch batch)
        {
            if (batch.MinNumber == null)
            {
                throw new InvalidOperationException("Batch needs to have the min number set to determine priority");
            }

            if (_receiptStorage.LowestInsertedReceiptBlock - batch.MinNumber < FastBlocksPriorities.ForReceipts)
            {
                batch.Prioritized = true;
            }
        }

        public override SyncResponseHandlingResult HandleResponse(ReceiptsSyncBatch batch)
        {
            if (batch.IsResponseEmpty)
            {
                batch.MarkHandlingStart();
                if (_logger.IsTrace) _logger.Trace($"{batch} - came back EMPTY");
                _pending.Enqueue(batch);
                batch.MarkHandlingEnd();
                return batch.ResponseSourcePeer == null ? SyncResponseHandlingResult.NotAssigned : SyncResponseHandlingResult.NoProgress;
            }

            try
            {
                batch.MarkHandlingStart();
                try
                {
                    int added = InsertReceipts(batch);
                    if (added == 0)
                    {
                        return SyncResponseHandlingResult.NoProgress;
                    }
                    else
                    {
                        return SyncResponseHandlingResult.OK;
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsWarn) _logger.Warn($"Failed to handle {batch}. This is non-critical error and the node issued the request again. You may want to report {ex}.");

                    _pending.Enqueue(batch);
                    return SyncResponseHandlingResult.InternalError;
                }
            }
            finally
            {
                batch.MarkHandlingEnd();
                _sent.TryRemove(batch, out _);
            }
        }

        private void HandleDependentBatches()
        {
            long? lowest = _receiptStorage.LowestInsertedReceiptBlock;
            while (lowest.HasValue && _dependencies.TryRemove(lowest.Value, out List<(Block, TxReceipt[])> dependency))
            {
                InsertReceipts(dependency);
                lowest = _receiptStorage.LowestInsertedReceiptBlock;
            }
        }

        private int InsertReceipts(ReceiptsSyncBatch receiptSyncBatch)
        {
            int added = 0;
            long? lastPredecessor = null;

            List<(Block, TxReceipt[])> validReceipts = new List<(Block, TxReceipt[])>();
            if (receiptSyncBatch.Response.Length != 0 && receiptSyncBatch.Response[0] != null)
            {
                lastPredecessor = receiptSyncBatch.Predecessors.Length == 0 ? null : receiptSyncBatch.Predecessors[0];
            }

            for (int blockIndex = 0;
                blockIndex < receiptSyncBatch.Response.Length;
                blockIndex++)
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
                        if (_logger.IsWarn) _logger.Warn($"{receiptSyncBatch} - invalid receipt root");
                        _syncPeerPool.ReportBreachOfProtocol(receiptSyncBatch.ResponseSourcePeer, "invalid receipts root");
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
                ReceiptsSyncBatch fillerBatch = PrepareReceiptFiller(added, receiptSyncBatch);
                _pending.Enqueue(fillerBatch);
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
                        _dependencies.TryAdd(lastPredecessor.Value, validReceipts);
                    }
                    else
                    {
                        InsertReceipts(validReceipts);
                    }
                }

                if (_receiptStorage.LowestInsertedReceiptBlock != null)
                {
                    _syncReport.FastBlocksReceipts.Update(_pivotNumber - (_receiptStorage.LowestInsertedReceiptBlock ?? _pivotNumber) + 1);
                }

                if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_receiptStorage.LowestInsertedReceiptBlock} | HANDLED {receiptSyncBatch}");

                _syncReport.ReceiptsInQueue.Update(ReceiptsInQueue);

                return added;
            }
        }

        private object _reportLock = new object();

        private void LogStateOnPrepare()
        {
            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_receiptStorage.LowestInsertedReceiptBlock}, DEPENDENCIES {_dependencies.Count}, SENT: {_sent.Count}, PENDING: {_pending.Count}");
            if (_logger.IsTrace)
            {
                lock (_reportLock)
                {
                    ConcurrentDictionary<long, string> all = new ConcurrentDictionary<long, string>();
                    StringBuilder builder = new StringBuilder();
                    builder.AppendLine($"SENT {_sent.Count} PENDING {_pending.Count} DEPENDENCIES {_dependencies.Count}");
                    foreach (var headerDependency in _dependencies)
                    {
                        all.TryAdd(headerDependency.Value.Last().Item1.Number, $"  DEPENDENCY RECEIPTS [{headerDependency.Value.Last().Item1.Number}, {headerDependency.Value.First().Item1.Number}] on {headerDependency.Key}");
                    }

                    foreach (var pendingBatch in _pending)
                    {
                        all.TryAdd(pendingBatch.EndNumber, $"  PENDING    {pendingBatch}");
                    }

                    foreach (var sentBatch in _sent)
                    {
                        all.TryAdd(sentBatch.Key.EndNumber, $"  SENT       {sentBatch.Key}");
                    }

                    foreach (KeyValuePair<long, string> keyValuePair in all
                        .OrderByDescending(kvp => kvp.Key))
                    {
                        builder.AppendLine(keyValuePair.Value);
                    }

                    _logger.Trace($"{builder}");
                }
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

        private static ReceiptsSyncBatch PrepareReceiptFiller(int added, ReceiptsSyncBatch receiptsSyncBatch)
        {
            if (added == 0)
            {
                return receiptsSyncBatch;
            }

            int requestSize = receiptsSyncBatch.Blocks.Length;
            ReceiptsSyncBatch filler = new ReceiptsSyncBatch();
            filler.MinNumber = receiptsSyncBatch.MinNumber;
            filler.Description = "FILLER";
            filler.Predecessors = new long?[requestSize - added];
            filler.Blocks = new Block[requestSize - added];
            filler.Request = new Keccak[requestSize - added];

            int fillerIndex = 0;
            for (int missingIndex = added;
                missingIndex < requestSize;
                missingIndex++)
            {
                filler.Predecessors[fillerIndex] = receiptsSyncBatch.Predecessors[missingIndex];
                filler.Blocks[fillerIndex] = receiptsSyncBatch.Blocks[missingIndex];
                filler.Request[fillerIndex] = receiptsSyncBatch.Request[missingIndex];
                fillerIndex++;
            }

            return filler;
        }
    }
}