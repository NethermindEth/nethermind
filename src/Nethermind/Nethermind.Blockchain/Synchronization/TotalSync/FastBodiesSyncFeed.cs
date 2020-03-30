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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization.FastBlocks;
using Nethermind.Blockchain.Synchronization.SyncLimits;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State.Proofs;

namespace Nethermind.Blockchain.Synchronization.TotalSync
{
    public class FastBodiesSyncFeed : SyncFeed<FastBlocksBatch>
    {
        private int BodiesRequestSize = GethSyncLimits.MaxBodyFetch;

        private ILogger _logger;
        private IBlockTree _blockTree;
        private ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private IEthSyncPeerPool _syncPeerPool;

        private ConcurrentDictionary<long, List<Block>> _bodiesDependencies = new ConcurrentDictionary<long, List<Block>>();
        private ConcurrentDictionary<FastBlocksBatch, object> _sentBatches = new ConcurrentDictionary<FastBlocksBatch, object>();
        private ConcurrentStack<FastBlocksBatch> _pendingBatches = new ConcurrentStack<FastBlocksBatch>();

        private object _empty = new object();
        private object _handlerLock = new object();

        private Keccak _startBodyHash;

        private Keccak _lowestRequestedBodyHash;

        private long _pivotNumber;
        private Keccak _pivotHash;

        public bool IsFinished =>
            _pendingBatches.Count
            + _sentBatches.Count
            + _bodiesDependencies.Count == 0;

        public FastBodiesSyncFeed(IBlockTree blockTree, IEthSyncPeerPool syncPeerPool, ISyncConfig syncConfig, ISyncReport syncReport, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));

            if (!_syncConfig.UseGethLimitsInFastBlocks)
            {
                BodiesRequestSize = NethermindSyncLimits.MaxBodyFetch;
            }
        }

        private bool _isMoreLikelyToBeHandlingDependenciesNow;

        private FastBlocksBatchType ResolveBatchType()
        {
            if (_syncConfig.BeamSync && _blockTree.LowestInsertedHeader != null)
            {
                return FastBlocksBatchType.None;
            }

            bool bodiesDownloaded = (_blockTree.LowestInsertedBody?.Number ?? 0) == 1;
            if (!bodiesDownloaded && _syncConfig.DownloadBodiesInFastSync)
            {
                return _lowestRequestedBodyHash == _blockTree.Genesis.Hash
                    ? FastBlocksBatchType.None
                    : FastBlocksBatchType.Bodies;
            }

            _syncReport.FastBlocksBodies.Update(_pivotNumber);
            _syncReport.FastBlocksBodies.MarkEnd();
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
                        return Task.FromResult((FastBlocksBatch) null);
                    }

                    case FastBlocksBatchType.Bodies:
                    {
                        Keccak hash = _lowestRequestedBodyHash;
                        BlockHeader header = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                        if (header == null)
                        {
                            return Task.FromResult((FastBlocksBatch) null);
                        }

                        if (_lowestRequestedBodyHash != _pivotHash)
                        {
                            if (header.ParentHash == _blockTree.Genesis.Hash)
                            {
                                return Task.FromResult((FastBlocksBatch) null);
                            }

                            header = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                            if (header == null)
                            {
                                return Task.FromResult((FastBlocksBatch) null);
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
                            return Task.FromResult((FastBlocksBatch) null);
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
            if (_logger.IsTrace)
            {
                lock (_handlerLock)
                {
                    Dictionary<long, string> all = new Dictionary<long, string>();
                    StringBuilder builder = new StringBuilder();
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

        private void HandleDependentBatches()
        {
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
        }

        private void InsertBlocks(List<Block> validResponses)
        {
            _blockTree.Insert(validResponses);
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
                    case FastBlocksBatchType.Bodies:
                    {
                        if (batch.Bodies.Request.Length == 0)
                        {
                            return SyncBatchResponseHandlingResult.OK; // (BlocksDataHandlerResult.OK, 1);
                        }

                        batch.MarkHandlingStart();
                        Stopwatch stopwatch = Stopwatch.StartNew();
                        int added = InsertBodies(batch);
                        stopwatch.Stop();
//                        var nonNull = batch.Bodies.Headers.Where(h => h != null).OrderBy(h => h.Number).ToArray();
//                        _logger.Warn($"Handled blocks response blocks [{nonNull.First().Number},{nonNull.Last().Number}]{batch.Bodies.Request.Length} in {stopwatch.ElapsedMilliseconds}ms");
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

        public override bool IsMultiFeed => true;

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
                if (new TxTrie(block.Transactions).RootHash != block.TxRoot ||
                    OmmersHash.Calculate(block) != block.OmmersHash)
                {
                    if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID - tx or ommers");
                    _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.OriginalDataSource, $"invalid tx or ommers root");
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

            _syncReport.BodiesInQueue.Update(_bodiesDependencies.Sum(d => d.Value.Count));
            return validResponsesCount;
        }

        public override void Activate()
        {
            if (!_syncConfig.FastBlocks)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = LongConverter.FromString(_syncConfig.PivotNumber ?? "0x0");
            _pivotHash = _syncConfig.PivotHash == null ? null : new Keccak(_syncConfig.PivotHash);

            Block lowestInsertedBody = _blockTree.LowestInsertedBody;
            _startBodyHash = lowestInsertedBody?.Hash ?? _pivotHash;

            _lowestRequestedBodyHash = _startBodyHash;

            _sentBatches.Clear();
            _pendingBatches.Clear();
            _bodiesDependencies.Clear();
            
            ChangeState(SyncFeedState.Active);
        }
    }
}