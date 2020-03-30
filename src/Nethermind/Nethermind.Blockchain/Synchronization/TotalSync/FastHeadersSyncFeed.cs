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
using Nethermind.Blockchain.Synchronization.FastBlocks;
using Nethermind.Blockchain.Synchronization.SyncLimits;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Synchronization.TotalSync
{
    public class FastHeadersSyncFeed : SyncFeed<FastBlocksBatch>
    {
        private ILogger _logger;
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly ISyncReport _syncReport;
        private IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;

        private object _empty = new object();
        private object _handlerLock = new object();

        private long _startNumber;
        private Keccak _startHeaderHash;
        private UInt256 _startTotalDifficulty;

        private long _lowestRequestedHeaderNumber;

        private Keccak _nextHeaderHash;
        private UInt256? _nextHeaderDiff;

        private long _pivotNumber;
        private long _requestsSent;
        private long _itemsSaved;
        private Keccak _pivotHash;
        private UInt256 _pivotDifficulty;

        private ConcurrentDictionary<FastBlocksBatch, object> _sentBatches = new ConcurrentDictionary<FastBlocksBatch, object>();
        private ConcurrentStack<FastBlocksBatch> _pendingBatches = new ConcurrentStack<FastBlocksBatch>();

        public bool IsFinished =>
            _pendingBatches.Count
            + _sentBatches.Count
            + _headerDependencies.Count == 0;

        public FastHeadersSyncFeed(IBlockTree blockTree, IEthSyncPeerPool syncPeerPool, ISyncConfig syncConfig, ISyncReport syncReport, ILogManager logManager)
        {
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _logger = logManager?.GetClassLogger<FastHeadersSyncFeed>() ?? throw new ArgumentNullException(nameof(FastHeadersSyncFeed));

            if (!_syncConfig.UseGethLimitsInFastBlocks)
            {
                HeadersRequestSize = NethermindSyncLimits.MaxHeaderFetch;
            }
        }

        private int HeadersRequestSize = GethSyncLimits.MaxHeaderFetch;

        private ConcurrentDictionary<long, FastBlocksBatch> _headerDependencies = new ConcurrentDictionary<long, FastBlocksBatch>();

        private bool _isMoreLikelyToBeHandlingDependenciesNow;

        private FastBlocksBatchType ResolveBatchType()
        {
            if (_syncConfig.BeamSync && _blockTree.LowestInsertedHeader != null)
            {
                // we just need a pivot header and can go up then
                _syncReport.FastBlocksHeaders.Update(_pivotNumber);
                _syncReport.FastBlocksHeaders.MarkEnd();
                _headerDependencies.Clear();
                return FastBlocksBatchType.None;
            }

            bool headersDownloaded = (_blockTree.LowestInsertedHeader?.Number ?? 0) == 1;

            if (!headersDownloaded)
            {
                return _lowestRequestedHeaderNumber == 0
                    ? FastBlocksBatchType.None
                    : FastBlocksBatchType.Headers;
            }

            _syncReport.FastBlocksHeaders.Update(_pivotNumber);
            _syncReport.FastBlocksHeaders.MarkEnd();

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
                        return gTask.FromResult((FastBlocksBatch)null);
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
            return Task.FromResult(batch);
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
        
        public override SyncBatchResponseHandlingResult HandleResponse(FastBlocksBatch batch)
        {
            if (batch.IsResponseEmpty)
            {
                batch.MarkHandlingStart();
                if (_logger.IsTrace) _logger.Trace($"{batch} - came back EMPTY");
                batch.Allocation = null;
                _pendingBatches.Push(batch);
                batch.MarkHandlingEnd();
                return SyncBatchResponseHandlingResult.NoData;
            }

            try
            {
                switch (batch.BatchType)
                {
                    case FastBlocksBatchType.Headers:
                    {
                        if (batch.Headers?.RequestSize == 0)
                        {
                            return SyncBatchResponseHandlingResult.OK; // 1
                        }

                        lock (_handlerLock)
                        {
                            batch.MarkHandlingStart();
                            int added = InsertHeaders(batch);
                            return SyncBatchResponseHandlingResult.OK;
                        }
                    }

                    default:
                    {
                        return SyncBatchResponseHandlingResult.InvalidFormat;
                    }
                }
            }
            finally
            {
                batch.MarkHandlingEnd();
                _sentBatches.TryRemove(batch, out _);
            }
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

        private int InsertHeaders(FastBlocksBatch batch)
        {
            var headersSyncBatch = batch.Headers;
            if (headersSyncBatch.Response.Length > batch.Headers.RequestSize)
            {
                if (_logger.IsWarn) _logger.Warn($"Peer sent too long response ({headersSyncBatch.Response.Length}) to {batch}");
                _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource, $"response too long ({headersSyncBatch.Response.Length})");
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
                    _syncPeerPool.ReportInvalid(batch.Allocation, "inconsistent headers batch");
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
                            _syncPeerPool.ReportInvalid(batch.Allocation, "first hash inconsistent with request");
                        }

                        break;
                    }

                    // response needs to be cached until predecessors arrive
                    if (header.Hash != _nextHeaderHash)
                    {
                        if (header.Number == (_blockTree.LowestInsertedHeader?.Number ?? _pivotNumber + 1) - 1)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{batch} - ended up IGNORED - different branch - number {header.Number} was {header.Hash} while expected {_nextHeaderHash}");
                            _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource, "headers - different branch");
                            break;
                        }

                        if (header.Number == _blockTree.LowestInsertedHeader?.Number)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{batch} - ended up IGNORED - different branch");
                            _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource, "headers - different branch");
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
                            _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource, "headers - response not matching request");
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
                        _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource, $"invalid header {header.ToString(BlockHeader.Format.Short)}");
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
                throw new Exception($"Added {added} + left {leftFillerSize} + right {rightFillerSize} != request size {batch.Headers.RequestSize} in {batch}");
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
                if (_logger.IsDebug) _logger.Debug($"{batch} - reporting no progress");
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

            _syncReport.HeadersInQueue.Update(_headerDependencies.Sum(hd => hd.Value.Headers.Response.Length));
            return added;
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
                    _headerDependencies.TryRemove(parentNumber, out _);
                    InsertHeaders(batch);
                }
            }

            return addBlockResult;
        }

        public override void Activate()
        {
            if (!_syncConfig.FastBlocks)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = _syncConfig.PivotNumberParsed;
            _pivotHash = _syncConfig.PivotHashParsed;
            _pivotDifficulty = _syncConfig.PivotTotalDifficultyParsed;

            BlockHeader lowestInserted = _blockTree.LowestInsertedHeader;
            _startNumber = lowestInserted?.Number ?? _pivotNumber;
            _startHeaderHash = lowestInserted?.Hash ?? _pivotHash;
            _startTotalDifficulty = lowestInserted?.TotalDifficulty ?? _pivotDifficulty;

            _nextHeaderHash = _startHeaderHash;
            _nextHeaderDiff = _startTotalDifficulty;

            _lowestRequestedHeaderNumber = _startNumber + 1;

            _sentBatches.Clear();
            _pendingBatches.Clear();
            _headerDependencies.Clear();
            
            ChangeState(SyncFeedState.Active);
        }
    }
}