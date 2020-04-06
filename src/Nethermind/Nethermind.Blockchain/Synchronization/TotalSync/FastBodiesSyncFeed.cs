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
using System.Threading;
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
    public class FastBodiesSyncFeed : SyncFeed<BodiesSyncBatch>
    {
        private int _bodiesRequestSize = GethSyncLimits.MaxBodyFetch;
        private object _dummyObject = new object();
        private object _reportLock = new object();

        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private readonly IEthSyncPeerPool _syncPeerPool;

        private ConcurrentDictionary<long, List<Block>> _dependencies = new ConcurrentDictionary<long, List<Block>>();
        private ConcurrentDictionary<BodiesSyncBatch, object> _sent = new ConcurrentDictionary<BodiesSyncBatch, object>();
        private ConcurrentQueue<BodiesSyncBatch> _pending = new ConcurrentQueue<BodiesSyncBatch>();

        private Keccak _lowestRequestedBodyHash;

        private long _pivotNumber;
        private Keccak _pivotHash;

        public FastBodiesSyncFeed(IBlockTree blockTree, IEthSyncPeerPool syncPeerPool, ISyncConfig syncConfig, ISyncReport syncReport, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));

            if (!_syncConfig.UseGethLimitsInFastBlocks)
            {
                _bodiesRequestSize = NethermindSyncLimits.MaxBodyFetch;
            }

            if (!_syncConfig.FastBlocks)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = _syncConfig.PivotNumberParsed;
            _pivotHash = _syncConfig.PivotHashParsed;

            Block lowestInsertedBody = _blockTree.LowestInsertedBody;
            Keccak startBodyHash = lowestInsertedBody?.Hash ?? _pivotHash;

            _lowestRequestedBodyHash = startBodyHash;
        }
        
        private bool ShouldFinish => (_blockTree.LowestInsertedBody?.Number ?? 0) == 1;
        
        public override bool IsMultiFeed => true;

        private bool AnyBatchesLeftToPrepare()
        {
            bool shouldDownloadBodies = _syncConfig.DownloadBodiesInFastSync;
            bool isBeamSync = _syncConfig.BeamSync;
            bool anyHeaderSynced = _blockTree.LowestInsertedHeader != null;
            bool allBodiesDownloaded = (_blockTree.LowestInsertedBody?.Number ?? 0) == 1;
            bool requestedGenesis = _lowestRequestedBodyHash == _blockTree.Genesis.Hash;

            bool noBatchesLeft = !shouldDownloadBodies
                                 || allBodiesDownloaded
                                 || isBeamSync && anyHeaderSynced
                                 || requestedGenesis;

            if (noBatchesLeft)
            {
                if (ShouldFinish)
                {
                    Finish();
                    _syncReport.FastBlocksBodies.Update(_pivotNumber);
                    _syncReport.FastBlocksBodies.MarkEnd();
                    _dependencies.Clear();
                    _pending.Clear();
                    _sent.Clear();
                }

                return false;
            }

            return true;
        }

        private void LogStateOnPrepare()
        {
            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_blockTree.LowestInsertedBody?.Number}, DEPENDENCIES {_dependencies.Count}, SENT: {_sent.Count}, PENDING: {_pending.Count}");
            if (_logger.IsTrace)
            {
                lock (_reportLock)
                {
                    ConcurrentDictionary<long, string> all = new ConcurrentDictionary<long, string>();
                    StringBuilder builder = new StringBuilder();
                    builder.AppendLine($"SENT {_sent.Count} PENDING {_pending.Count} DEPENDENCIES {_dependencies.Count}");
                    foreach (var headerDependency in _dependencies)
                    {
                        all.TryAdd(headerDependency.Value.Last().Number, $"  DEPENDENCY [{headerDependency.Value.First().Number}, {headerDependency.Value.Last().Number}]");
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

        public override Task<BodiesSyncBatch> PrepareRequest()
        {
            HandleDependentBatches();

            if (_pending.TryDequeue(out BodiesSyncBatch batch))
            {
                batch.MarkRetry();
            }
            else if (AnyBatchesLeftToPrepare())
            {
                Keccak hash = _lowestRequestedBodyHash;
                BlockHeader header = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (header == null)
                {
                    return Task.FromResult((BodiesSyncBatch) null);
                }

                if (_lowestRequestedBodyHash != _pivotHash)
                {
                    if (header.ParentHash == _blockTree.Genesis.Hash)
                    {
                        return Task.FromResult((BodiesSyncBatch) null);
                    }

                    header = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                    if (header == null)
                    {
                        return Task.FromResult((BodiesSyncBatch) null);
                    }
                }

                int requestSize = (int) Math.Min(header.Number, _bodiesRequestSize);
                batch = new BodiesSyncBatch();
                batch.Request = new Keccak[requestSize];
                batch.Headers = new BlockHeader[requestSize];
                batch.MinNumber = header.Number;

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

                    batch.Headers[i] = header;
                    collectedRequests++;
                    _lowestRequestedBodyHash = batch.Request[i] = header.Hash;

                    header = _blockTree.FindHeader(header.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                }

                if (collectedRequests == 0)
                {
                    return Task.FromResult((BodiesSyncBatch) null);
                }

                //only for the final one
                if (collectedRequests < requestSize)
                {
                    BlockHeader[] currentHeaders = batch.Headers;
                    Keccak[] currentRequests = batch.Request;
                    batch.Request = new Keccak[collectedRequests];
                    batch.Headers = new BlockHeader[collectedRequests];
                    Array.Copy(currentHeaders, requestSize - collectedRequests, batch.Headers, 0, collectedRequests);
                    Array.Copy(currentRequests, requestSize - collectedRequests, batch.Request, 0, collectedRequests);
                }
            }

            if (batch != null)
            {
                _sent.TryAdd(batch, _dummyObject);
                if ((_blockTree.LowestInsertedBody?.Number ?? 0) - batch.Headers[0].Number < 1024)
                {
                    batch.Prioritized = true;
                }

                LogStateOnPrepare();
            }

            return Task.FromResult(batch);
        }

        private void HandleDependentBatches()
        {
            long? lowest = _blockTree.LowestInsertedBody?.Number;
            while (lowest.HasValue && _dependencies.TryRemove(lowest.Value - 1, out List<Block> dependentBatch))
            {
                dependentBatch.Reverse();
                InsertBlocks(dependentBatch);
                lowest = _blockTree.LowestInsertedBody?.Number;
            }
        }

        private void InsertBlocks(List<Block> validResponses)
        {
            _blockTree.Insert(validResponses);
        }

        public override SyncBatchResponseHandlingResult HandleResponse(BodiesSyncBatch batch)
        {
            if (batch.IsResponseEmpty)
            {
                batch.MarkHandlingStart();
                if (_logger.IsTrace) _logger.Trace($"{batch} - came back EMPTY");
                _pending.Enqueue(batch);
                batch.MarkHandlingEnd();
                return SyncBatchResponseHandlingResult.NoData; //(BlocksDataHandlerResult.OK, 0);
            }

            try
            {
                batch.MarkHandlingStart();
                Stopwatch stopwatch = Stopwatch.StartNew();
                int added = InsertBodies(batch);
                stopwatch.Stop();
                //                        var nonNull = batch.Bodies.Headers.Where(h => h != null).OrderBy(h => h.Number).ToArray();
                //                        _logger.Warn($"Handled blocks response blocks [{nonNull.First().Number},{nonNull.Last().Number}]{batch.Bodies.Request.Length} in {stopwatch.ElapsedMilliseconds}ms");
                return SyncBatchResponseHandlingResult.OK; //(BlocksDataHandlerResult.OK, added);
            }
            finally
            {
                batch.MarkHandlingEnd();
                _sent.TryRemove(batch, out _);
            }
        }

        private int InsertBodies(BodiesSyncBatch batch)
        {
            List<Block> validResponses = new List<Block>();
            for (int i = 0; i < batch.Response.Length; i++)
            {
                BlockBody blockBody = batch.Response[i];
                if (blockBody == null)
                {
                    break;
                }

                Block block = new Block(batch.Headers[i], blockBody.Transactions, blockBody.Ommers);
                if (new TxTrie(block.Transactions).RootHash != block.TxRoot ||
                    OmmersHash.Calculate(block) != block.OmmersHash)
                {
                    if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID - tx or ommers");
                    _syncPeerPool.ReportInvalid(batch.ResponseSourcePeer, $"invalid tx or ommers root");
                    break;
                }

                validResponses.Add(block);
            }

            int validResponsesCount = validResponses.Count;
            if (validResponses.Count < batch.Request.Length)
            {
                BodiesSyncBatch fillerBatch = new BodiesSyncBatch();
                fillerBatch.MinNumber = batch.MinNumber;

                int originalLength = batch.Request.Length;
                fillerBatch.Request = new Keccak[originalLength - validResponsesCount];
                fillerBatch.Headers = new BlockHeader[originalLength - validResponsesCount];

                for (int i = validResponsesCount; i < originalLength; i++)
                {
                    fillerBatch.Request[i - validResponsesCount] = batch.Request[i];
                    fillerBatch.Headers[i - validResponsesCount] = batch.Headers[i];
                }

                if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {fillerBatch}");
                _pending.Enqueue(fillerBatch);
            }

            if (validResponses.Any())
            {
                long expectedNumber = _blockTree.LowestInsertedBody?.Number - 1 ?? LongConverter.FromString(_syncConfig.PivotNumber ?? "0");
                if (validResponses.Last().Number != expectedNumber)
                {
                    _dependencies.TryAdd(validResponses.Last().Number, validResponses);
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

            _syncReport.BodiesInQueue.Update(_dependencies.Sum(d => d.Value.Count));
            return validResponsesCount;
        }
    }
}