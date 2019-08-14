/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

// ReSharper disable InconsistentlySynchronizedField
namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class FastBlocksFeed : IFastBlocksFeed
    {
        private const int BodiesRequestSize = 512;
        private const int HeadersRequestSize = 512;
        private const int ReceiptsRequestStats = 256;

        private ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private IEthSyncPeerPool _syncPeerPool;

        private ConcurrentDictionary<long, FastBlocksBatch> _headerDependencies = new ConcurrentDictionary<long, FastBlocksBatch>();
        private ConcurrentDictionary<long, List<Block>> _bodiesDependencies = new ConcurrentDictionary<long, List<Block>>();
        private ConcurrentDictionary<long, List<(long, TxReceipt)>> _receiptDependencies = new ConcurrentDictionary<long, List<(long, TxReceipt)>>();
        private ConcurrentDictionary<FastBlocksBatch, object> _sentBatches = new ConcurrentDictionary<FastBlocksBatch, object>();
        private ConcurrentStack<FastBlocksBatch> _pendingBatches = new ConcurrentStack<FastBlocksBatch>();

        private object _empty = new object();
        private object _handlerLock = new object();

        private long _startNumber;
        private Keccak _startBodyHash;
        private Keccak _startHeaderHash;
        private Keccak _startReceiptsHash;
        private UInt256 _startTotalDifficulty;

        private long _lowestRequestedHeaderNumber;
        private Keccak _lowestRequestedBodyHash;
        private Keccak _lowestRequestedReceiptsHash;

        private Keccak _nextHeaderHash;
        private UInt256? _nextHeaderDiff;

        private long _pivotNumber;
        private long _requestsSent;
        private long _itemsSaved;
        private Keccak _pivotHash;
        private UInt256 _pivotDifficulty;

        public bool IsFinished =>
            _pendingBatches.Count
            + _sentBatches.Count
            + _receiptDependencies.Count
            + _headerDependencies.Count
            + _bodiesDependencies.Count == 0;

        public FastBlocksFeed(ISpecProvider specProvider, IBlockTree blockTree, IReceiptStorage receiptStorage, IEthSyncPeerPool syncPeerPool, ISyncConfig syncConfig, ISyncReport syncReport, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
        }

        private bool _isMoreLikelyToBeHandlingDependenciesNow;

        public FastBlocksBatch PrepareRequest()
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
                        return null;
                    }

                    case FastBlocksBatchType.Bodies:
                    {
                        Keccak hash = _lowestRequestedBodyHash;
                        BlockHeader header = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                        if (header == null)
                        {
                            throw new InvalidDataException($"Last header is null for {hash} at lowest inserted body: {_blockTree.LowestInsertedBody?.Number}");
                        }

                        if (_lowestRequestedBodyHash != _pivotHash)
                        {
                            if (header.ParentHash == _blockTree.Genesis.Hash)
                            {
                                return null;
                            }

                            header = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                            if (header == null)
                            {
                                throw new InvalidDataException($"Parent header is null for {hash} at lowest inserted body: {_blockTree.LowestInsertedBody?.Number}");
                            }
                        }

                        int requestSize = (int) Math.Min(header.Number, BodiesRequestSize);
                        batch = new FastBlocksBatch();
                        batch.Bodies = new BodiesSyncBatch();
                        batch.Bodies.Request = new Keccak[requestSize];
                        batch.Bodies.Headers = new BlockHeader[requestSize];
                        batch.MinNumber = header.Number;
                        if ((_blockTree.LowestInsertedBody?.Number ?? 0) - header.Number < 1024)
                        {
                            batch.Prioritized = true;
                        }

                        int collectedRequests = 0;
                        while (collectedRequests < requestSize)
                        {
                            int i = requestSize - collectedRequests - 1;
//                            while (header != null && !header.HasBody)
//                            {
//                                header = _blockTree.FindHeader(header.ParentHash);
//                            }

                            if (header == null)
                            {
                                break;
                            }

                            batch.Bodies.Headers[i] = header;
                            collectedRequests++;
                            _lowestRequestedBodyHash = batch.Bodies.Request[i] = header.Hash;

                            header = _blockTree.FindHeader(header.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                        }

                        if (collectedRequests == 0)
                        {
                            return null;
                        }

                        //only for the final one
                        if (collectedRequests < requestSize)
                        {
                            BlockHeader[] currentHeaders = batch.Bodies.Headers;
                            Keccak[] currentRequests = batch.Bodies.Request;
                            batch.Bodies.Request = new Keccak[collectedRequests];
                            batch.Bodies.Headers = new BlockHeader[collectedRequests];
                            Array.Copy(currentHeaders, requestSize - collectedRequests, batch.Bodies.Headers, 0, collectedRequests);
                            Array.Copy(currentRequests, requestSize - collectedRequests, batch.Bodies.Request, 0, collectedRequests);
                        }

                        break;
                    }

                    case FastBlocksBatchType.Headers:
                    {
                        batch = new FastBlocksBatch();
                        batch.MinNumber = _lowestRequestedHeaderNumber - 1;
                        batch.Headers = new HeadersSyncBatch();
                        batch.Headers.StartNumber = Math.Max(0, _lowestRequestedHeaderNumber - HeadersRequestSize);
                        batch.Headers.RequestSize = (int) Math.Min(_lowestRequestedHeaderNumber, HeadersRequestSize);
                        _lowestRequestedHeaderNumber = batch.Headers.StartNumber;

                        break;
                    }

                    case FastBlocksBatchType.Receipts:
                    {
                        Keccak hash = _lowestRequestedReceiptsHash;
                        Block predecessorBlock = _blockTree.FindBlock(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                        Block block = predecessorBlock;
                        if (block == null)
                        {
                            throw new InvalidDataException($"Last block is null for {hash} at lowest inserted body: {_blockTree.LowestInsertedBody?.Number}");
                        }

                        if (_lowestRequestedReceiptsHash != _pivotHash)
                        {
                            if (block.ParentHash == _blockTree.Genesis.Hash)
                            {
                                return null;
                            }

                            block = _blockTree.FindParent(predecessorBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                            if (block == null)
                            {
                                throw new InvalidDataException($"Parent block is null for {hash} at lowest inserted body: {_blockTree.LowestInsertedBody?.Number}");
                            }
                        }
                        else
                        {
                            predecessorBlock = null;
                        }

                        int requestSize = (int) Math.Min(block.Number, ReceiptsRequestStats);
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

            LogStateOnPrepare();
            return batch;
        }

        private FastBlocksBatchType ResolveBatchType()
        {
            bool headersDownloaded = (_blockTree.LowestInsertedHeader?.Number ?? 0) == 1;
            bool bodiesDownloaded = (_blockTree.LowestInsertedBody?.Number ?? 0) == 1;
            bool receiptsDownloaded = _receiptStorage.LowestInsertedReceiptBlock == 1;

            if (!headersDownloaded)
            {
                return _lowestRequestedHeaderNumber == 0
                    ? FastBlocksBatchType.None
                    : FastBlocksBatchType.Headers;
            }

            _syncReport.FastBlocksHeaders.Update(_pivotNumber);
            _syncReport.FastBlocksHeaders.MarkEnd();

            if (!bodiesDownloaded  && _syncConfig.DownloadBodiesInFastSync)
            {
                return _lowestRequestedBodyHash == _blockTree.Genesis.Hash
                    ? FastBlocksBatchType.None
                    : FastBlocksBatchType.Bodies;
            }
            
            _syncReport.FastBlocksBodies.Update(_pivotNumber);
            _syncReport.FastBlocksBodies.MarkEnd();

            if (!receiptsDownloaded
                && _syncConfig.DownloadReceiptsInFastSync)
            {
                return _lowestRequestedReceiptsHash == _blockTree.Genesis.Hash
                    ? FastBlocksBatchType.None
                    : FastBlocksBatchType.Receipts;
            }
            
            _syncReport.FastBlocksReceipts.Update(_pivotNumber);
            _syncReport.FastBlocksReceipts.MarkEnd();

            return FastBlocksBatchType.None;
        }

        private void HandleDependentBatches()
        {
            long? lowestHeaderNumber = _blockTree.LowestInsertedHeader?.Number;
            lock (_handlerLock)
            {
                while (lowestHeaderNumber.HasValue && _headerDependencies.ContainsKey(lowestHeaderNumber.Value - 1))
                {
                    FastBlocksBatch dependentBatch = _headerDependencies[lowestHeaderNumber.Value - 1];
                    _headerDependencies.TryRemove(lowestHeaderNumber.Value - 1, out _);

                    InsertHeaders(dependentBatch);
                    lowestHeaderNumber = _blockTree.LowestInsertedHeader?.Number;
                }
            }

            long? lowestBodyNumber = _blockTree.LowestInsertedBody?.Number;
            while (lowestBodyNumber.HasValue && _bodiesDependencies.ContainsKey(lowestBodyNumber.Value - 1))
            {
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();
                List<Block> dependentBatch = _bodiesDependencies[lowestBodyNumber.Value - 1];
                dependentBatch.Reverse();
                InsertBlocks(dependentBatch);
                _bodiesDependencies.Remove(lowestBodyNumber.Value - 1, out _);
                lowestBodyNumber = _blockTree.LowestInsertedBody?.Number;
                stopwatch.Stop();
//                _logger.Warn($"Handled dependent blocks [{dependentBatch.First().Number},{dependentBatch.Last().Number}]({dependentBatch.Count}) in {stopwatch.ElapsedMilliseconds}ms");
            }

            long? lowestReceiptNumber = _receiptStorage.LowestInsertedReceiptBlock;
            while (lowestReceiptNumber.HasValue && _receiptDependencies.ContainsKey(lowestReceiptNumber.Value))
            {
                InsertReceipts(_receiptDependencies[lowestReceiptNumber.Value]);
                _receiptDependencies.Remove(lowestReceiptNumber.Value, out _);
                lowestReceiptNumber = _receiptStorage.LowestInsertedReceiptBlock;
            }
        }

        private void LogStateOnPrepare()
        {
            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_blockTree.LowestInsertedHeader?.Number}, LOWEST_REQUESTED {_lowestRequestedHeaderNumber}, DEPENDENCIES {_headerDependencies.Count}, SENT: {_sentBatches.Count}, PENDING: {_pendingBatches.Count}");
            if (_logger.IsTrace)
            {
                lock (_handlerLock)
                {
                    Dictionary<long, string> all = new Dictionary<long, string>();
                    StringBuilder builder = new StringBuilder();
                    builder.AppendLine($"SENT {_sentBatches.Count} PENDING {_pendingBatches.Count} DEPENDENCIES {_headerDependencies.Count}");
                    foreach (var headerDependency in _headerDependencies
                        .OrderByDescending(d => d.Value.Headers.EndNumber)
                        .ThenByDescending(d => d.Value.Headers.StartNumber))
                    {
                        all.Add(headerDependency.Value.Headers.EndNumber, $"  DEPENDENCY {headerDependency.Value}");
                    }

                    foreach (var pendingBatch in _pendingBatches
                        .Where(b => b.BatchType == FastBlocksBatchType.Headers)
                        .OrderByDescending(d => d.Headers.EndNumber)
                        .ThenByDescending(d => d.Headers.StartNumber))
                    {
                        all.Add(pendingBatch.Headers.EndNumber, $"  PENDING    {pendingBatch}");
                    }

                    foreach (var sentBatch in _sentBatches
                        .Where(sb => sb.Key.BatchType == FastBlocksBatchType.Headers)
                        .OrderByDescending(d => d.Key.Headers.EndNumber)
                        .ThenByDescending(d => d.Key.Headers.StartNumber))
                    {
                        all.Add(sentBatch.Key.Headers.EndNumber, $"  SENT       {sentBatch.Key}");
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

        public (BlocksDataHandlerResult Result, int BlocksConsumed) HandleResponse(FastBlocksBatch batch)
        {
            if (batch.IsResponseEmpty)
            {
                batch.MarkHandlingStart();
                if (_logger.IsTrace) _logger.Trace($"{batch} - came back EMPTY");
                batch.Allocation = null;
                _pendingBatches.Push(batch);
                batch.MarkHandlingEnd();
                return (BlocksDataHandlerResult.OK, 0);
            }

            try
            {
                switch (batch.BatchType)
                {
                    case FastBlocksBatchType.Headers:
                    {
                        if (batch.Headers?.RequestSize == 0)
                        {
                            return (BlocksDataHandlerResult.OK, 1);
                        }

                        lock (_handlerLock)
                        {
                            batch.MarkHandlingStart();
                            int added = InsertHeaders(batch);
                            return (BlocksDataHandlerResult.OK, added);
                        }
                    }

                    case FastBlocksBatchType.Bodies:
                    {
                        if (batch.Bodies.Request.Length == 0)
                        {
                            return (BlocksDataHandlerResult.OK, 1);
                        }

                        batch.MarkHandlingStart();
                        Stopwatch stopwatch = Stopwatch.StartNew();
                        int added = InsertBodies(batch);
                        stopwatch.Stop();
//                        var nonNull = batch.Bodies.Headers.Where(h => h != null).OrderBy(h => h.Number).ToArray();
//                        _logger.Warn($"Handled blocks response blocks [{nonNull.First().Number},{nonNull.Last().Number}]{batch.Bodies.Request.Length} in {stopwatch.ElapsedMilliseconds}ms");
                        return (BlocksDataHandlerResult.OK, added);
                    }

                    case FastBlocksBatchType.Receipts:
                    {
                        batch.MarkHandlingStart();
                        int added = InsertReceipts(batch);
                        return (BlocksDataHandlerResult.OK, added);
                    }

                    default:
                    {
                        return (BlocksDataHandlerResult.InvalidFormat, 0);
                    }
                }
            }
            finally
            {
                batch.MarkHandlingEnd();
                _sentBatches.TryRemove(batch, out _);
            }
        }

        private int InsertReceipts(FastBlocksBatch batch)
        {
            var receiptSyncBatch = batch.Receipts;
            int added = 0;
            long? lastPredecessor = null;
            List<(long, TxReceipt)> validReceipts = new List<(long, TxReceipt)>();

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
                    Keccak receiptsRoot = block.CalculateReceiptRoot(_specProvider, blockReceipts);
                    if (receiptsRoot != block.ReceiptsRoot)
                    {
                        if (_logger.IsWarn) _logger.Warn($"{batch} - invalid receipt root");
                        _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource);
                        wasInvalid = true;
                    }
                }

                if (!wasInvalid)
                {
                    for (int receiptIndex = 0; receiptIndex < blockReceipts.Length; receiptIndex++)
                    {
                        validReceipts.Add((block.Number, blockReceipts[receiptIndex]));
                    }

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
                        validReceipts.Add((1, null)); // special finisher
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

                return added;
            }
        }

        private void InsertReceipts(List<(long, TxReceipt)> receipts)
        {
            foreach ((long blockNumber, TxReceipt receipt) in receipts)
            {
                _receiptStorage.Insert(blockNumber, receipt);
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

        private int InsertBodies(FastBlocksBatch batch)
        {
            var bodiesSyncBatch = batch.Bodies;
            List<Block> validResponses = new List<Block>();
            for (int i = 0; i < bodiesSyncBatch.Response.Length; i++)
            {
                BlockBody blockBody = bodiesSyncBatch.Response[i];
                if (blockBody == null)
                {
                    break;
                }

                Block block = new Block(bodiesSyncBatch.Headers[i], blockBody.Transactions, blockBody.Ommers);
                if (block.CalculateTxRoot() != block.TransactionsRoot ||
                    block.CalculateOmmersHash() != block.OmmersHash)
                {
                    if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID - tx or ommers");
                    _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource);
                    break;
                }

                validResponses.Add(block);
            }

            int validResponsesCount = validResponses.Count;
            if (validResponses.Count < bodiesSyncBatch.Request.Length)
            {
                FastBlocksBatch fillerBatch = new FastBlocksBatch();
                fillerBatch.MinNumber = batch.MinNumber;
                fillerBatch.Bodies = new BodiesSyncBatch();

                int originalLength = bodiesSyncBatch.Request.Length;
                fillerBatch.Bodies.Request = new Keccak[originalLength - validResponsesCount];
                fillerBatch.Bodies.Headers = new BlockHeader[originalLength - validResponsesCount];

                for (int i = validResponsesCount; i < originalLength; i++)
                {
                    fillerBatch.Bodies.Request[i - validResponsesCount] = bodiesSyncBatch.Request[i];
                    fillerBatch.Bodies.Headers[i - validResponsesCount] = bodiesSyncBatch.Headers[i];
                }

                if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {fillerBatch}");
                _pendingBatches.Push(fillerBatch);
            }

            if (validResponses.Any())
            {
                long expectedNumber = _blockTree.LowestInsertedBody?.Number - 1 ?? LongConverter.FromString(_syncConfig.PivotNumber ?? "0");
                if (validResponses.Last().Number != expectedNumber)
                {
                    _bodiesDependencies.TryAdd(validResponses.Last().Number, validResponses);
                }
                else
                {
                    validResponses.Reverse();
                    InsertBlocks(validResponses);
                }

                if (_blockTree.LowestInsertedBody != null)
                {
                    _syncReport.FastBlocksPivotNumber = _pivotNumber;
                    _syncReport.FastBlocksBodies.Update(_pivotNumber - _blockTree.LowestInsertedBody.Number + 1);
                }
            }

            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_blockTree.LowestInsertedBody?.Number} | HANDLED {batch}");

            return validResponsesCount;
        }

        private void InsertBlocks(List<Block> validResponses)
        {
            _blockTree.Insert(validResponses);
        }

        public void StartNewRound()
        {
            _pivotNumber = LongConverter.FromString(_syncConfig.PivotNumber ?? "0x0");
            _pivotHash = _syncConfig.PivotHash == null ? null : new Keccak(_syncConfig.PivotHash);
            _pivotDifficulty = UInt256.Parse(_syncConfig.PivotTotalDifficulty ?? "0x0");

            BlockHeader lowestInserted = _blockTree.LowestInsertedHeader;
            Block lowestInsertedBody = _blockTree.LowestInsertedBody;
            _startNumber = lowestInserted?.Number ?? _pivotNumber;
            _startBodyHash = lowestInsertedBody?.Hash ?? _pivotHash;
            _startHeaderHash = lowestInserted?.Hash ?? _pivotHash;
            _startReceiptsHash = _blockTree.FindHash(_receiptStorage.LowestInsertedReceiptBlock ?? long.MaxValue) ?? _pivotHash;
            _startTotalDifficulty = lowestInserted?.TotalDifficulty ?? _pivotDifficulty;

            _nextHeaderHash = _startHeaderHash;
            _nextHeaderDiff = _startTotalDifficulty;

            _lowestRequestedHeaderNumber = _startNumber + 1;
            _lowestRequestedBodyHash = _startBodyHash;
            _lowestRequestedReceiptsHash = _startReceiptsHash;

            _sentBatches.Clear();
            _pendingBatches.Clear();
            _headerDependencies.Clear();
            _bodiesDependencies.Clear();
            _receiptDependencies.Clear();
        }

        private int InsertHeaders(FastBlocksBatch batch)
        {
            var headersSyncBatch = batch.Headers;
            if (headersSyncBatch.Response.Length > batch.Headers.RequestSize)
            {
                if (_logger.IsWarn) _logger.Warn($"Peer sent too long response ({headersSyncBatch.Response.Length}) to {batch}");
                _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource);
                _pendingBatches.Push(batch);
                return 0;
            }

            decimal ratio = (decimal) _itemsSaved / (_requestsSent == 0 ? 1 : _requestsSent);
            _requestsSent += batch.Headers.RequestSize;

            long addedLast = batch.Headers.StartNumber - 1;
            long addedEarliest = batch.Headers.EndNumber + 1;
            int skippedAtTheEnd = 0;
            for (int i = headersSyncBatch.Response.Length - 1; i >= 0; i--)
            {
                BlockHeader header = headersSyncBatch.Response[i];
                if (header == null)
                {
                    skippedAtTheEnd++;
                    continue;
                }

                if (header.Number != headersSyncBatch.StartNumber + i)
                {
                    _syncPeerPool.ReportInvalid(batch.Allocation);
                    break;
                }

                bool isFirst = i == headersSyncBatch.Response.Length - 1 - skippedAtTheEnd;
                if (isFirst)
                {
                    BlockHeader lowestInserted = _blockTree.LowestInsertedHeader;
                    // response does not carry expected data
                    if (header.Number == lowestInserted?.Number && header.Hash != lowestInserted?.Hash)
                    {
                        if (batch.Allocation != null)
                        {
                            if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID hash");
                            _syncPeerPool.ReportInvalid(batch.Allocation);
                        }

                        break;
                    }

                    // response needs to be cached until predecessors arrive
                    if (header.Hash != _nextHeaderHash)
                    {
                        if (header.Number == (_blockTree.LowestInsertedHeader?.Number ?? _pivotNumber + 1) - 1)
                        {
                            if (_logger.IsWarn) _logger.Warn($"{batch} - ended up IGNORED - different branch");
                            _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource);
                            break;
                        }

                        if (header.Number == _blockTree.LowestInsertedHeader?.Number)
                        {
                            if (_logger.IsWarn) _logger.Warn($"{batch} - ended up IGNORED - different branch");
                            _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource);
                            break;
                        }

                        if (_headerDependencies.ContainsKey(header.Number))
                        {
                            _pendingBatches.Push(batch);
                            throw new InvalidOperationException($"Only one header dependency expected ({batch})");
                        }

                        for (int j = 0; j < batch.Headers.Response.Length; j++)
                        {
                            BlockHeader current = batch.Headers.Response[j];
                            if (batch.Headers.Response[j] != null)
                            {
                                addedEarliest = Math.Min(addedEarliest, current.Number);
                                addedLast = Math.Max(addedLast, current.Number);
                            }
                            else
                            {
                                break;
                            }
                        }

                        FastBlocksBatch dependentBatch = BuildDependentBatch(batch, addedLast, addedEarliest);
                        _headerDependencies[header.Number] = dependentBatch;
                        if (_logger.IsDebug) _logger.Debug($"{batch} -> DEPENDENCY {dependentBatch}");

                        // but we cannot do anything with it yet
                        break;
                    }
                }
                else
                {
                    if (header.Hash != headersSyncBatch.Response[i + 1]?.ParentHash)
                    {
                        if (batch.Allocation != null)
                        {
                            if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID inconsistent");
                            _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource);
                        }

                        break;
                    }
                }

                header.TotalDifficulty = _nextHeaderDiff;
                AddBlockResult addBlockResult = InsertHeader(header);
                if (addBlockResult == AddBlockResult.InvalidBlock)
                {
                    if (batch.Allocation != null)
                    {
                        if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID bad block");
                        _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource);
                    }

                    break;
                }

                addedEarliest = Math.Min(addedEarliest, header.Number);
                addedLast = Math.Max(addedLast, header.Number);
            }

            int added = (int) (addedLast - addedEarliest + 1);
            int leftFillerSize = (int) (addedEarliest - batch.Headers.StartNumber);
            int rightFillerSize = (int) (batch.Headers.EndNumber - addedLast);
            if (added + leftFillerSize + rightFillerSize != batch.Headers.RequestSize)
            {
                throw new Exception($"Added {added} + left {leftFillerSize} + right {rightFillerSize} != request size {batch.Headers.RequestSize} in  {batch}");
            }

            added = Math.Max(0, added);

            if (added < batch.Headers.RequestSize)
            {
                if (added <= 0)
                {
                    batch.Headers.Response = null;
                    _pendingBatches.Push(batch);
                }
                else
                {
                    if (leftFillerSize > 0)
                    {
                        FastBlocksBatch leftFiller = BuildLeftFiller(batch, leftFillerSize);
                        _pendingBatches.Push(leftFiller);
                        if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {leftFiller}");
                    }

                    if (rightFillerSize > 0)
                    {
                        FastBlocksBatch rightFiller = BuildRightFiller(batch, rightFillerSize);
                        _pendingBatches.Push(rightFiller);
                        if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {rightFiller}");
                    }
                }
            }

            if (added == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"{batch} - reporting no progress");
                if (batch.Allocation != null)
                {
                    _syncPeerPool.ReportNoSyncProgress(batch.Allocation);
                }
                else if (batch.OriginalDataSource != null)
                {
                    _syncPeerPool.ReportNoSyncProgress(batch.OriginalDataSource);
                }
            }

            if (_blockTree.LowestInsertedHeader != null)
            {
                _syncReport.FastBlocksPivotNumber = _pivotNumber;
                _syncReport.FastBlocksHeaders.Update(_pivotNumber - _blockTree.LowestInsertedHeader.Number + 1);
            }

            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_blockTree.LowestInsertedHeader?.Number} | HANDLED {batch}");

            return added;
        }

        private static FastBlocksBatch BuildRightFiller(FastBlocksBatch batch, int rightFillerSize)
        {
            FastBlocksBatch rightFiller = new FastBlocksBatch();
            rightFiller.Headers = new HeadersSyncBatch();
            rightFiller.Headers.StartNumber = batch.Headers.EndNumber - rightFillerSize + 1;
            rightFiller.Headers.RequestSize = rightFillerSize;
            rightFiller.Allocation = null;
            rightFiller.MinNumber = batch.MinNumber;
            return rightFiller;
        }

        private static FastBlocksBatch BuildLeftFiller(FastBlocksBatch batch, int leftFillerSize)
        {
            FastBlocksBatch leftFiller = new FastBlocksBatch();
            leftFiller.Headers = new HeadersSyncBatch();
            leftFiller.Headers.StartNumber = batch.Headers.StartNumber;
            leftFiller.Headers.RequestSize = leftFillerSize;
            leftFiller.Allocation = null;
            leftFiller.MinNumber = batch.MinNumber;
            return leftFiller;
        }

        private static FastBlocksBatch BuildDependentBatch(FastBlocksBatch batch, long addedLast, long addedEarliest)
        {
            FastBlocksBatch dependentBatch = new FastBlocksBatch();
            dependentBatch.Headers = new HeadersSyncBatch();
            dependentBatch.Headers.StartNumber = batch.Headers.StartNumber;
            dependentBatch.Headers.RequestSize = (int) (addedLast - addedEarliest + 1);
            dependentBatch.Allocation = null;
            dependentBatch.MinNumber = batch.MinNumber;
            dependentBatch.Headers.Response = batch.Headers.Response
                .Skip((int) (addedEarliest - batch.Headers.StartNumber))
                .Take((int) (addedLast - addedEarliest + 1)).ToArray();
            dependentBatch.OriginalDataSource = batch.Allocation?.Current ?? batch.OriginalDataSource;
            return dependentBatch;
        }

        private AddBlockResult InsertHeader(BlockHeader header)
        {
            if (header.IsGenesis)
            {
                return AddBlockResult.AlreadyKnown;
            }

            AddBlockResult addBlockResult = _blockTree.Insert(header);

            if (addBlockResult == AddBlockResult.Added || addBlockResult == AddBlockResult.AlreadyKnown)
            {
                _nextHeaderHash = header.ParentHash;
                _nextHeaderDiff = (header.TotalDifficulty ?? 0) - header.Difficulty;
                if (addBlockResult == AddBlockResult.Added)
                {
                    _itemsSaved++;
                }
            }

            if (addBlockResult == AddBlockResult.InvalidBlock)
            {
                return addBlockResult;
            }

            if (addBlockResult == AddBlockResult.UnknownParent)
            {
                return addBlockResult;
            }

            long parentNumber = header.Number - 1;
            if (_headerDependencies.ContainsKey(parentNumber))
            {
                FastBlocksBatch batch = _headerDependencies[parentNumber];
                {
                    batch.Allocation = null;
                    _headerDependencies.Remove(parentNumber, out _);
                    InsertHeaders(batch);
                }
            }

            return addBlockResult;
        }
    }
}