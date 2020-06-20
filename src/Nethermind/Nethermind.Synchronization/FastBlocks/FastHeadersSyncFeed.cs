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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SyncLimits;

namespace Nethermind.Synchronization.FastBlocks
{
    public class FastHeadersSyncFeed : SyncFeed<HeadersSyncBatch>
    {
        private readonly ILogger _logger;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly ISyncReport _syncReport;
        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;

        private object _dummyObject = new object();
        private object _handlerLock = new object();

        private int _headersRequestSize = GethSyncLimits.MaxHeaderFetch;
        private long _lowestRequestedHeaderNumber;

        private Keccak _nextHeaderHash;
        private UInt256? _nextHeaderDiff;

        private long _pivotNumber;

        /// <summary>
        /// Requests awaiting to be sent - these are results of partial or invalid responses being queued again 
        /// </summary>
        private ConcurrentQueue<HeadersSyncBatch> _pending = new ConcurrentQueue<HeadersSyncBatch>();

        /// <summary>
        /// Requests sent to peers for which responses have not been received yet  
        /// </summary>
        private ConcurrentDictionary<HeadersSyncBatch, object> _sent = new ConcurrentDictionary<HeadersSyncBatch, object>();

        /// <summary>
        /// Responses received from peers but waiting in a queue for some other requests to be handled first
        /// </summary>
        private ConcurrentDictionary<long, HeadersSyncBatch> _dependencies = new ConcurrentDictionary<long, HeadersSyncBatch>();

        private bool AllHeadersDownloaded => (_blockTree.LowestInsertedHeader?.Number ?? long.MaxValue) == 1;
        private bool AnyHeaderDownloaded => _blockTree.LowestInsertedHeader != null;

        private long HeadersInQueue => _dependencies.Sum(hd => hd.Value.Response.Length);

        public FastHeadersSyncFeed(IBlockTree blockTree, ISyncPeerPool syncPeerPool, ISyncConfig syncConfig, ISyncReport syncReport, ILogManager logManager)
            : base(logManager)
        {
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _logger = logManager?.GetClassLogger<FastHeadersSyncFeed>() ?? throw new ArgumentNullException(nameof(FastHeadersSyncFeed));

            if (!_syncConfig.UseGethLimitsInFastBlocks)
            {
                _headersRequestSize = NethermindSyncLimits.MaxHeaderFetch;
            }

            if (!_syncConfig.FastBlocks)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = _syncConfig.PivotNumberParsed;

            BlockHeader lowestInserted = _blockTree.LowestInsertedHeader;
            long startNumber = lowestInserted?.Number ?? _pivotNumber;
            Keccak startHeaderHash = lowestInserted?.Hash ?? _syncConfig.PivotHashParsed;
            UInt256 startTotalDifficulty = lowestInserted?.TotalDifficulty ?? _syncConfig.PivotTotalDifficultyParsed;

            _nextHeaderHash = startHeaderHash;
            _nextHeaderDiff = startTotalDifficulty;

            _lowestRequestedHeaderNumber = startNumber + 1;

            Activate();
        }

        public override bool IsMultiFeed => true;
        public override AllocationContexts Contexts => AllocationContexts.Headers;

        private bool ShouldBuildANewBatch()
        {
            bool genesisHeaderRequested = _lowestRequestedHeaderNumber == 0;
            
            bool isImmediateSync = !_syncConfig.DownloadHeadersInFastSync;

            bool noBatchesLeft = AllHeadersDownloaded
                                 || genesisHeaderRequested
                                 || HeadersInQueue >= FastBlocksQueueLimits.ForHeaders
                                 || isImmediateSync && AnyHeaderDownloaded;

            if (noBatchesLeft)
            {
                if (AllHeadersDownloaded || isImmediateSync && AnyHeaderDownloaded)
                {
                    Finish();
                    PostFinishCleanUp();
                }

                return false;
            }

            return true;
        }

        private void PostFinishCleanUp()
        {
            _syncReport.FastBlocksHeaders.Update(_pivotNumber);
            _syncReport.FastBlocksHeaders.MarkEnd();
            _dependencies.Clear(); // there may be some dependencies from wrong branches
            _pending.Clear(); // there may be pending wrong branches
            _sent.Clear(); // we my still be waiting for some bad branches
            _syncReport.HeadersInQueue.Update(0L);
            _syncReport.HeadersInQueue.MarkEnd();
        }

        private void HandleDependentBatches()
        {
            long? lowest = _blockTree.LowestInsertedHeader?.Number;
            while (lowest.HasValue && _dependencies.TryRemove(lowest.Value - 1, out HeadersSyncBatch dependentBatch))
            {
                InsertHeaders(dependentBatch);
                lowest = _blockTree.LowestInsertedHeader?.Number;
            }
        }

        public override Task<HeadersSyncBatch> PrepareRequest()
        {
            HandleDependentBatches();

            if (_pending.TryDequeue(out HeadersSyncBatch batch))
            {
                batch.MarkRetry();
            }
            else if (ShouldBuildANewBatch())
            {
                batch = BuildNewBatch();
            }

            if (batch != null)
            {
                _sent.TryAdd(batch, _dummyObject);
                if (batch.StartNumber >= (_blockTree.LowestInsertedHeader?.Number ?? 0) - FastBlocksPriorities.ForHeaders)
                {
                    batch.Prioritized = true;
                }

                LogStateOnPrepare();
            }

            return Task.FromResult(batch);
        }

        private HeadersSyncBatch BuildNewBatch()
        {
            HeadersSyncBatch batch = new HeadersSyncBatch();
            batch.MinNumber = _lowestRequestedHeaderNumber - 1;
            batch.StartNumber = Math.Max(0, _lowestRequestedHeaderNumber - _headersRequestSize);
            batch.RequestSize = (int) Math.Min(_lowestRequestedHeaderNumber, _headersRequestSize);
            _lowestRequestedHeaderNumber = batch.StartNumber;
            return batch;
        }

        private void LogStateOnPrepare()
        {
            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_blockTree.LowestInsertedHeader?.Number}, LOWEST_REQUESTED {_lowestRequestedHeaderNumber}, DEPENDENCIES {_dependencies.Count}, SENT: {_sent.Count}, PENDING: {_pending.Count}");
            if (_logger.IsTrace)
            {
                lock (_handlerLock)
                {
                    ConcurrentDictionary<long, string> all = new ConcurrentDictionary<long, string>();
                    StringBuilder builder = new StringBuilder();
                    builder.AppendLine($"SENT {_sent.Count} PENDING {_pending.Count} DEPENDENCIES {_dependencies.Count}");
                    foreach (var headerDependency in _dependencies)
                    {
                        all.TryAdd(headerDependency.Value.EndNumber, $"  DEPENDENCY {headerDependency.Value}");
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

        public override SyncResponseHandlingResult HandleResponse(HeadersSyncBatch batch)
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
                if (batch.RequestSize == 0)
                {
                    return SyncResponseHandlingResult.OK; // 1
                }

                lock (_handlerLock)
                {
                    batch.MarkHandlingStart();
                    int added = InsertHeaders(batch);
                    return SyncResponseHandlingResult.OK;
                }
            }
            finally
            {
                batch.MarkHandlingEnd();
                _sent.TryRemove(batch, out _);
            }
        }

        private static HeadersSyncBatch BuildRightFiller(HeadersSyncBatch batch, int rightFillerSize)
        {
            HeadersSyncBatch rightFiller = new HeadersSyncBatch();
            rightFiller.StartNumber = batch.EndNumber - rightFillerSize + 1;
            rightFiller.RequestSize = rightFillerSize;
            rightFiller.MinNumber = batch.MinNumber;
            return rightFiller;
        }

        private static HeadersSyncBatch BuildLeftFiller(HeadersSyncBatch batch, int leftFillerSize)
        {
            HeadersSyncBatch leftFiller = new HeadersSyncBatch();
            leftFiller.StartNumber = batch.StartNumber;
            leftFiller.RequestSize = leftFillerSize;
            leftFiller.MinNumber = batch.MinNumber;
            return leftFiller;
        }

        private static HeadersSyncBatch BuildDependentBatch(HeadersSyncBatch batch, long addedLast, long addedEarliest)
        {
            HeadersSyncBatch dependentBatch = new HeadersSyncBatch();
            dependentBatch.StartNumber = batch.StartNumber;
            dependentBatch.RequestSize = (int) (addedLast - addedEarliest + 1);
            dependentBatch.MinNumber = batch.MinNumber;
            dependentBatch.Response = batch.Response
                .Skip((int) (addedEarliest - batch.StartNumber))
                .Take((int) (addedLast - addedEarliest + 1)).ToArray();
            dependentBatch.ResponseSourcePeer = batch.ResponseSourcePeer;
            return dependentBatch;
        }

        private int InsertHeaders(HeadersSyncBatch batch)
        {
            if (batch.Response.Length > batch.RequestSize)
            {
                if (_logger.IsDebug) _logger.Debug($"Peer sent too long response ({batch.Response.Length}) to {batch}");
                _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, $"response too long ({batch.Response.Length})");
                _pending.Enqueue(batch);
                return 0;
            }

            long addedLast = batch.StartNumber - 1;
            long addedEarliest = batch.EndNumber + 1;
            int skippedAtTheEnd = 0;
            for (int i = batch.Response.Length - 1; i >= 0; i--)
            {
                BlockHeader header = batch.Response[i];
                if (header == null)
                {
                    skippedAtTheEnd++;
                    continue;
                }

                if (header.Number != batch.StartNumber + i)
                {
                    _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, "inconsistent headers batch");
                    break;
                }

                bool isFirst = i == batch.Response.Length - 1 - skippedAtTheEnd;
                if (isFirst)
                {
                    BlockHeader lowestInserted = _blockTree.LowestInsertedHeader;
                    // response does not carry expected data
                    if (header.Number == lowestInserted?.Number && header.Hash != lowestInserted?.Hash)
                    {
                        if (batch.ResponseSourcePeer != null)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{batch} - reporting INVALID hash");
                            _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, "first hash inconsistent with request");
                        }

                        break;
                    }

                    // response needs to be cached until predecessors arrive
                    if (header.Hash != _nextHeaderHash)
                    {
                        if (header.Number == (_blockTree.LowestInsertedHeader?.Number ?? _pivotNumber + 1) - 1)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{batch} - ended up IGNORED - different branch - number {header.Number} was {header.Hash} while expected {_nextHeaderHash}");
                            _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, "headers - different branch");
                            break;
                        }

                        if (header.Number == _blockTree.LowestInsertedHeader?.Number)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{batch} - ended up IGNORED - different branch");
                            _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, "headers - different branch");
                            break;
                        }

                        if (_dependencies.ContainsKey(header.Number))
                        {
                            _pending.Enqueue(batch);
                            throw new InvalidOperationException($"Only one header dependency expected ({batch})");
                        }

                        for (int j = 0; j < batch.Response.Length; j++)
                        {
                            BlockHeader current = batch.Response[j];
                            if (batch.Response[j] != null)
                            {
                                addedEarliest = Math.Min(addedEarliest, current.Number);
                                addedLast = Math.Max(addedLast, current.Number);
                            }
                            else
                            {
                                break;
                            }
                        }

                        HeadersSyncBatch dependentBatch = BuildDependentBatch(batch, addedLast, addedEarliest);
                        _dependencies[header.Number] = dependentBatch;
                        if (_logger.IsDebug) _logger.Debug($"{batch} -> DEPENDENCY {dependentBatch}");

                        // but we cannot do anything with it yet
                        break;
                    }
                }
                else
                {
                    if (header.Hash != batch.Response[i + 1]?.ParentHash)
                    {
                        if (batch.ResponseSourcePeer != null)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{batch} - reporting INVALID inconsistent");
                            _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, "headers - response not matching request");
                        }

                        break;
                    }
                }

                header.TotalDifficulty = _nextHeaderDiff;
                AddBlockResult addBlockResult = InsertHeader(header);
                if (addBlockResult == AddBlockResult.InvalidBlock)
                {
                    if (batch.ResponseSourcePeer != null)
                    {
                        if (_logger.IsDebug) _logger.Debug($"{batch} - reporting INVALID bad block");
                        _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, $"invalid header {header.ToString(BlockHeader.Format.Short)}");
                    }

                    break;
                }

                addedEarliest = Math.Min(addedEarliest, header.Number);
                addedLast = Math.Max(addedLast, header.Number);
            }

            int added = (int) (addedLast - addedEarliest + 1);
            int leftFillerSize = (int) (addedEarliest - batch.StartNumber);
            int rightFillerSize = (int) (batch.EndNumber - addedLast);
            if (added + leftFillerSize + rightFillerSize != batch.RequestSize)
            {
                throw new Exception($"Added {added} + left {leftFillerSize} + right {rightFillerSize} != request size {batch.RequestSize} in {batch}");
            }

            added = Math.Max(0, added);

            if (added < batch.RequestSize)
            {
                if (added <= 0)
                {
                    batch.Response = null;
                    _pending.Enqueue(batch);
                }
                else
                {
                    if (leftFillerSize > 0)
                    {
                        HeadersSyncBatch leftFiller = BuildLeftFiller(batch, leftFillerSize);
                        _pending.Enqueue(leftFiller);
                        if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {leftFiller}");
                    }

                    if (rightFillerSize > 0)
                    {
                        HeadersSyncBatch rightFiller = BuildRightFiller(batch, rightFillerSize);
                        _pending.Enqueue(rightFiller);
                        if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {rightFiller}");
                    }
                }
            }

            if (added == 0)
            {
                if (batch.ResponseSourcePeer != null)
                {
                    if (_logger.IsDebug) _logger.Debug($"{batch} - reporting no progress");
                    _syncPeerPool.ReportNoSyncProgress(batch.ResponseSourcePeer, AllocationContexts.Headers);
                }
            }

            if (_blockTree.LowestInsertedHeader != null)
            {
                _syncReport.FastBlocksHeaders.Update(_pivotNumber - _blockTree.LowestInsertedHeader.Number + 1);
            }

            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_blockTree.LowestInsertedHeader?.Number} | HANDLED {batch}");

            _syncReport.HeadersInQueue.Update(HeadersInQueue);
            return added;
        }

        private AddBlockResult InsertHeader(BlockHeader header)
        {
            if (header.IsGenesis)
            {
                return AddBlockResult.AlreadyKnown;
            }

            AddBlockResult insertOutcome = _blockTree.Insert(header);
            if (insertOutcome == AddBlockResult.Added || insertOutcome == AddBlockResult.AlreadyKnown)
            {
                _nextHeaderHash = header.ParentHash;
                _nextHeaderDiff = (header.TotalDifficulty ?? 0) - header.Difficulty;
            }

            return insertOutcome;
        }
    }
}