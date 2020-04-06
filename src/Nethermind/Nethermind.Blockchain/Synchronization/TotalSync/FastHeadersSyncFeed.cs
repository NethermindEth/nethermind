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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization.FastBlocks;
using Nethermind.Blockchain.Synchronization.SyncLimits;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Synchronization.TotalSync
{
    public class FastHeadersSyncFeed : SyncFeed<HeadersSyncBatch>
    {
        private ILogger _logger;
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly ISyncReport _syncReport;
        private IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;

        private object _dummyObject = new object();
        private object _handlerLock = new object();

        private long _startNumber;
        private Keccak _startHeaderHash;
        private UInt256 _startTotalDifficulty;

        private int _headersRequestSize = GethSyncLimits.MaxHeaderFetch;
        private long _lowestRequestedHeaderNumber;

        private Keccak _nextHeaderHash;
        private UInt256? _nextHeaderDiff;

        private long _pivotNumber;
        private Keccak _pivotHash;
        private UInt256 _pivotDifficulty;

        private ConcurrentDictionary<HeadersSyncBatch, object> _sent = new ConcurrentDictionary<HeadersSyncBatch, object>();
        private ConcurrentStack<HeadersSyncBatch> _pending = new ConcurrentStack<HeadersSyncBatch>();
        private ConcurrentDictionary<long, HeadersSyncBatch> _dependencies = new ConcurrentDictionary<long, HeadersSyncBatch>();

        public bool AllHeadersDownloaded => (_blockTree.LowestInsertedHeader?.Number ?? long.MaxValue) == 1;

        public FastHeadersSyncFeed(IBlockTree blockTree, IEthSyncPeerPool syncPeerPool, ISyncConfig syncConfig, ISyncReport syncReport, ILogManager logManager)
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
            _pivotHash = _syncConfig.PivotHashParsed;
            _pivotDifficulty = _syncConfig.PivotTotalDifficultyParsed;

            BlockHeader lowestInserted = _blockTree.LowestInsertedHeader;
            _startNumber = lowestInserted?.Number ?? _pivotNumber;
            _startHeaderHash = lowestInserted?.Hash ?? _pivotHash;
            _startTotalDifficulty = lowestInserted?.TotalDifficulty ?? _pivotDifficulty;

            _nextHeaderHash = _startHeaderHash;
            _nextHeaderDiff = _startTotalDifficulty;

            _lowestRequestedHeaderNumber = _startNumber + 1;
        }

        public override bool IsMultiFeed => true;

        private bool AnyBatchesLeftToBeBuilt()
        {
            bool anyHandlerDownloaded = _blockTree.LowestInsertedHeader != null;
            bool beamSyncFinished = _syncConfig.BeamSync && anyHandlerDownloaded;
            bool genesisHeaderRequested = _lowestRequestedHeaderNumber == 0;

            bool noBatchesLeft = AllHeadersDownloaded || beamSyncFinished || genesisHeaderRequested;
            if (noBatchesLeft)
            {
                if (AllHeadersDownloaded)
                {
                    Finish();
                    _syncReport.FastBlocksHeaders.Update(_pivotNumber);
                    _syncReport.FastBlocksHeaders.MarkEnd();
                    _dependencies.Clear(); // there may be some dependencies from wrong branches
                    _pending.Clear(); // there may be pending wrong branches
                    _sent.Clear(); // we my still be waiting for some bad branches
                }

                return false;
            }

            return true;
        }

        private void HandleDependentBatches()
        {
            long? lowestInsertedNumber = _blockTree.LowestInsertedHeader?.Number;
            while (lowestInsertedNumber.HasValue && _dependencies.ContainsKey(lowestInsertedNumber.Value - 1))
            {
                HeadersSyncBatch dependentBatch = _dependencies[lowestInsertedNumber.Value - 1];
                _dependencies.TryRemove(lowestInsertedNumber.Value - 1, out _);

                InsertHeaders(dependentBatch);
                lowestInsertedNumber = _blockTree.LowestInsertedHeader?.Number;
            }
        }

        public override Task<HeadersSyncBatch> PrepareRequest()
        {
            HandleDependentBatches();

            HeadersSyncBatch batch = null;
            if (_pending.Any())
            {
                _pending.TryPop(out batch);
                batch.MarkRetry();
            }
            else if (AnyBatchesLeftToBeBuilt())
            {
                batch = new HeadersSyncBatch();
                batch.MinNumber = _lowestRequestedHeaderNumber - 1;
                batch.StartNumber = Math.Max(0, _lowestRequestedHeaderNumber - _headersRequestSize);
                batch.RequestSize = (int) Math.Min(_lowestRequestedHeaderNumber, _headersRequestSize);
                _lowestRequestedHeaderNumber = batch.StartNumber;
            }

            if (batch != null)
            {
                _sent.TryAdd(batch, _dummyObject);
                if (batch.StartNumber >= (_blockTree.LowestInsertedHeader?.Number ?? 0) - 2048)
                {
                    batch.Prioritized = true;
                }

                LogStateOnPrepare();
            }

            return Task.FromResult(batch);
        }

        private void LogStateOnPrepare()
        {
            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_blockTree.LowestInsertedHeader?.Number}, LOWEST_REQUESTED {_lowestRequestedHeaderNumber}, DEPENDENCIES {_dependencies.Count}, SENT: {_sent.Count}, PENDING: {_pending.Count}");
            if (_logger.IsWarn)
            {
                lock (_handlerLock)
                {
                    Dictionary<long, string> all = new Dictionary<long, string>();
                    StringBuilder builder = new StringBuilder();
                    builder.AppendLine($"SENT {_sent.Count} PENDING {_pending.Count} DEPENDENCIES {_dependencies.Count}");
                    foreach (var headerDependency in _dependencies
                        .OrderByDescending(d => d.Value.EndNumber)
                        .ThenByDescending(d => d.Value.StartNumber))
                    {
                        all.Add(headerDependency.Value.EndNumber, $"  DEPENDENCY {headerDependency.Value}");
                    }

                    foreach (var pendingBatch in _pending
                        .OrderByDescending(d => d.EndNumber)
                        .ThenByDescending(d => d.StartNumber))
                    {
                        all.Add(pendingBatch.EndNumber, $"  PENDING    {pendingBatch}");
                    }

                    foreach (var sentBatch in _sent
                        .OrderByDescending(d => d.Key.EndNumber)
                        .ThenByDescending(d => d.Key.StartNumber))
                    {
                        all.Add(sentBatch.Key.EndNumber, $"  SENT       {sentBatch.Key}");
                    }

                    foreach (KeyValuePair<long, string> keyValuePair in all
                        .OrderByDescending(kvp => kvp.Key))
                    {
                        builder.AppendLine(keyValuePair.Value);
                    }

                    _logger.Warn($"{builder}");
                }
            }
        }

        public override SyncBatchResponseHandlingResult HandleResponse(HeadersSyncBatch batch)
        {
            if (batch.IsResponseEmpty)
            {
                batch.MarkHandlingStart();
                if (_logger.IsTrace) _logger.Trace($"{batch} - came back EMPTY");
                _pending.Push(batch);
                batch.MarkHandlingEnd();
                return SyncBatchResponseHandlingResult.NoData;
            }

            try
            {
                if (batch.RequestSize == 0)
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
                if (_logger.IsWarn) _logger.Warn($"Peer sent too long response ({batch.Response.Length}) to {batch}");
                _syncPeerPool.ReportInvalid(batch.ResponseSourcePeer, $"response too long ({batch.Response.Length})");
                _pending.Push(batch);
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
                    _syncPeerPool.ReportInvalid(batch.ResponseSourcePeer, "inconsistent headers batch");
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
                            if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID hash");
                            _syncPeerPool.ReportInvalid(batch.ResponseSourcePeer, "first hash inconsistent with request");
                        }

                        break;
                    }

                    // response needs to be cached until predecessors arrive
                    if (header.Hash != _nextHeaderHash)
                    {
                        if (header.Number == (_blockTree.LowestInsertedHeader?.Number ?? _pivotNumber + 1) - 1)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{batch} - ended up IGNORED - different branch - number {header.Number} was {header.Hash} while expected {_nextHeaderHash}");
                            _syncPeerPool.ReportInvalid(batch.ResponseSourcePeer, "headers - different branch");
                            break;
                        }

                        if (header.Number == _blockTree.LowestInsertedHeader?.Number)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{batch} - ended up IGNORED - different branch");
                            _syncPeerPool.ReportInvalid(batch.ResponseSourcePeer, "headers - different branch");
                            break;
                        }

                        if (_dependencies.ContainsKey(header.Number))
                        {
                            _pending.Push(batch);
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
                            if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID inconsistent");
                            _syncPeerPool.ReportInvalid(batch.ResponseSourcePeer, "headers - response not matching request");
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
                        if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID bad block");
                        _syncPeerPool.ReportInvalid(batch.ResponseSourcePeer, $"invalid header {header.ToString(BlockHeader.Format.Short)}");
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
                    _pending.Push(batch);
                }
                else
                {
                    if (leftFillerSize > 0)
                    {
                        HeadersSyncBatch leftFiller = BuildLeftFiller(batch, leftFillerSize);
                        _pending.Push(leftFiller);
                        if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {leftFiller}");
                    }

                    if (rightFillerSize > 0)
                    {
                        HeadersSyncBatch rightFiller = BuildRightFiller(batch, rightFillerSize);
                        _pending.Push(rightFiller);
                        if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {rightFiller}");
                    }
                }
            }

            if (added == 0)
            {
                if (_logger.IsDebug) _logger.Debug($"{batch} - reporting no progress");
                if (batch.ResponseSourcePeer != null)
                {
                    _syncPeerPool.ReportNoSyncProgress(batch.ResponseSourcePeer);
                }
            }

            if (_blockTree.LowestInsertedHeader != null)
            {
                _syncReport.FastBlocksPivotNumber = _pivotNumber;
                _syncReport.FastBlocksHeaders.Update(_pivotNumber - _blockTree.LowestInsertedHeader.Number + 1);
            }

            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_blockTree.LowestInsertedHeader?.Number} | HANDLED {batch}");

            _syncReport.HeadersInQueue.Update(_dependencies.Sum(hd => hd.Value.Response.Length));
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

                long parentNumber = header.Number - 1;
                if (_dependencies.ContainsKey(parentNumber))
                {
                    HeadersSyncBatch batch = _dependencies[parentNumber];
                    {
                        _dependencies.TryRemove(parentNumber, out _);
                        InsertHeaders(batch);
                    }
                }
            }

            return addBlockResult;
        }
    }
}