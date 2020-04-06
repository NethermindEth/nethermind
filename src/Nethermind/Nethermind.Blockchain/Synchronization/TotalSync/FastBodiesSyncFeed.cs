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

        private ILogger _logger;
        private IBlockTree _blockTree;
        private ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private IEthSyncPeerPool _syncPeerPool;

        private ConcurrentDictionary<long, List<Block>> _dependencies = new ConcurrentDictionary<long, List<Block>>();
        private ConcurrentDictionary<BodiesSyncBatch, object> _sent = new ConcurrentDictionary<BodiesSyncBatch, object>();
        private ConcurrentStack<BodiesSyncBatch> _pending = new ConcurrentStack<BodiesSyncBatch>();

        private Keccak _startBodyHash;
        private Keccak _lowestRequestedBodyHash;

        private long _pivotNumber;
        private Keccak _pivotHash;

        public bool ShouldFinish => (_blockTree.LowestInsertedBody?.Number ?? 0) == 1;

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
            _startBodyHash = lowestInsertedBody?.Hash ?? _pivotHash;

            _lowestRequestedBodyHash = _startBodyHash;
        }

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

        // private void LogStateOnPrepare()
        // {
        //     if (_logger.IsWarn) _logger.Warn($"LOWEST_INSERTED {_blockTree.LowestInsertedHeader?.Number}, LOWEST_REQUESTED {_lowestRequestedHeaderNumber}, DEPENDENCIES {_dependencies.Count}, SENT: {_sent.Count}, PENDING: {_pending.Count}");
        //     if (_logger.IsWarn)
        //     {
        //         lock (_reportLock)
        //         {
        //             ConcurrentDictionary<long, string> all = new ConcurrentDictionary<long, string>();
        //             StringBuilder builder = new StringBuilder();
        //             builder.AppendLine($"SENT {_sent.Count} PENDING {_pending.Count} DEPENDENCIES {_dependencies.Count}");
        //             foreach (var headerDependency in _dependencies
        //                 .OrderByDescending(d => d.Value.EndNumber)
        //                 .ThenByDescending(d => d.Value.StartNumber))
        //             {
        //                 all.TryAdd(headerDependency.Value.EndNumber, $"  DEPENDENCY {headerDependency.Value}");
        //             }
        //
        //             foreach (var pendingBatch in _pending
        //                 .OrderByDescending(d => d.EndNumber)
        //                 .ThenByDescending(d => d.StartNumber))
        //             {
        //                 all.TryAdd(pendingBatch.EndNumber, $"  PENDING    {pendingBatch}");
        //             }
        //
        //             foreach (var sentBatch in _sent
        //                 .OrderByDescending(d => d.Key.EndNumber)
        //                 .ThenByDescending(d => d.Key.StartNumber))
        //             {
        //                 all.TryAdd(sentBatch.Key.EndNumber, $"  SENT       {sentBatch.Key}");
        //             }
        //
        //             foreach (KeyValuePair<long, string> keyValuePair in all
        //                 .OrderByDescending(kvp => kvp.Key))
        //             {
        //                 builder.AppendLine(keyValuePair.Value);
        //             }
        //
        //             _logger.Warn($"{builder}");
        //         }
        //     }
        // }

        public override Task<BodiesSyncBatch> PrepareRequest()
        {
            HandleDependentBatches();
            
            if (_pending.TryPop(out BodiesSyncBatch batch))
            {
                batch.MarkRetry();
            }
            else
            {
                bool anyBatchesLeftToPrepare = AnyBatchesLeftToPrepare();
                if (anyBatchesLeftToPrepare)
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
            }

            if (batch != null)
            {
                _sent.TryAdd(batch, _dummyObject);
                if ((_blockTree.LowestInsertedBody?.Number ?? 0) - batch.Headers[0].Number < 1024)
                {
                    batch.Prioritized = true;
                }
            }

            return Task.FromResult(batch);
        }

        private void HandleDependentBatches()
        {
            long? lowestBodyNumber = _blockTree.LowestInsertedBody?.Number;
            while (lowestBodyNumber.HasValue && _dependencies.ContainsKey(lowestBodyNumber.Value - 1))
            {
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();
                List<Block> dependentBatch = _dependencies[lowestBodyNumber.Value - 1];
                dependentBatch.Reverse();
                InsertBlocks(dependentBatch);
                _dependencies.Remove(lowestBodyNumber.Value - 1, out _);
                lowestBodyNumber = _blockTree.LowestInsertedBody?.Number;
                stopwatch.Stop();
//                _logger.Warn($"Handled dependent blocks [{dependentBatch.First().Number},{dependentBatch.Last().Number}]({dependentBatch.Count}) in {stopwatch.ElapsedMilliseconds}ms");
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
                _pending.Push(batch);
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
                _pending.Push(fillerBatch);
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