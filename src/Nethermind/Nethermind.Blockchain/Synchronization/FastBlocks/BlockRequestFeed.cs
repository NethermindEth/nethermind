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
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Json;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class BlockRequestFeed : IBlockRequestFeed
    {
        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly ISyncConfig _syncConfig;

        private ConcurrentDictionary<long, BlockSyncBatch> _headerDependencies = new ConcurrentDictionary<long, BlockSyncBatch>();
        private ConcurrentStack<BlockSyncBatch> _pendingBatches = new ConcurrentStack<BlockSyncBatch>();
        private ConcurrentDictionary<BlockSyncBatch, object> _sentBatches = new ConcurrentDictionary<BlockSyncBatch, object>();

        private object _handlerLock = new object();
        private SyncStats _syncStats;

        public int RequestSize { get; set; } = 256;

        public long StartNumber { get; set; }

        public Keccak StartHash { get; set; }

        public UInt256 StartTotalDifficulty { get; set; }

        private long? LowestRequestedHeaderNumber { get; set; }
        
        private Keccak LowestRequestedBodyHash { get; set; }

        private Keccak NextHash;

        private UInt256? NextTotalDifficulty;

        public BlockRequestFeed(IBlockTree blockTree, IEthSyncPeerPool syncPeerPool, ISyncConfig syncConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncStats = new SyncStats(logManager);

            _pivotNumber = LongConverter.FromString(_syncConfig.PivotNumber ?? "0");
            StartNewRound();
        }

        private long _requestsSent;
        private long _itemsSaved;
        private object _empty = new object();
        private long _pivotNumber;

        public BlockSyncBatch PrepareRequest()
        {
            if (NextHash == null)
            {
                NextHash = StartHash;
            }

            if (NextTotalDifficulty == null)
            {
                NextTotalDifficulty = StartTotalDifficulty;
            }

            lock (_handlerLock)
            {
                while (_headerDependencies.ContainsKey((_blockTree.LowestInsertedHeader?.Number ?? 0) - 1))
                {
                    InsertHeaders(_headerDependencies[(_blockTree.LowestInsertedHeader?.Number ?? 0) - 1]);
                }
            }

            BlockSyncBatch blockSyncBatch;
            if (_pendingBatches.Any())
            {
                _pendingBatches.TryPop(out blockSyncBatch);
                blockSyncBatch.MarkRetry();
            }
            else
            {
                if ((_blockTree.LowestInsertedHeader?.Number ?? 0L) == 1L)
                {
                    if (!_syncConfig.DownloadBodiesInFastSync)
                    {
                        return null;
                    }

                    if ((_blockTree.LowestInsertedBody?.Number ?? 0L) == 1L || LowestRequestedBodyHash == _blockTree.Genesis.Hash)
                    {
                        return null;
                    }
                    
                    Keccak lastHash = LowestRequestedBodyHash ?? StartHash;
                    BlockHeader lastHeader = _blockTree.FindHeader(lastHash);
                    blockSyncBatch = new BlockSyncBatch();
                    int thisRequestSize = (int)Math.Min(lastHeader.Number + 1, RequestSize);
                    blockSyncBatch.BodiesSyncBatch = new BodiesSyncBatch();
                    blockSyncBatch.BodiesSyncBatch.Request = new Keccak[thisRequestSize];
                    blockSyncBatch.MinNumber = lastHeader.Number;
                    
                    for (int i = thisRequestSize - 1; i >= 0; i--)
                    {
                        LowestRequestedBodyHash = blockSyncBatch.BodiesSyncBatch.Request[i] = lastHeader.Hash;
                        try
                        {
                            lastHeader = _blockTree.FindHeader(lastHeader.ParentHash);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                            throw;
                        }
                        
                        if (lastHeader == null)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    if (LowestRequestedHeaderNumber == 0L)
                    {
                        return null;
                    }
                    
                    blockSyncBatch = new BlockSyncBatch();
                    blockSyncBatch.MinNumber = LowestRequestedHeaderNumber ?? StartNumber;
                    blockSyncBatch.HeadersSyncBatch = new HeadersSyncBatch();
                    blockSyncBatch.HeadersSyncBatch.StartNumber = Math.Max(0, (LowestRequestedHeaderNumber - 1 ?? StartNumber) - (RequestSize - 1));
                    blockSyncBatch.HeadersSyncBatch.RequestSize = (int) Math.Min(LowestRequestedHeaderNumber ?? StartNumber + 1, RequestSize);
                    LowestRequestedHeaderNumber = blockSyncBatch.HeadersSyncBatch.StartNumber;    
                }
            }
            
            _sentBatches.TryAdd(blockSyncBatch, _empty);
            if (blockSyncBatch.HeadersSyncBatch != null && blockSyncBatch.HeadersSyncBatch.StartNumber >= ((_blockTree.LowestInsertedHeader?.Number ?? 0) - 2048))
            {
                blockSyncBatch.Prioritized = true;
            }

            LogStateOnPrepare();
            return blockSyncBatch;
        }

        private void LogStateOnPrepare()
        {
            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_blockTree.LowestInsertedHeader?.Number}, LOWEST_REQUESTED {LowestRequestedHeaderNumber}, DEPENDENCIES {_headerDependencies.Count}, SENT: {_sentBatches.Count}, PENDING: {_pendingBatches.Count}");
            if (_logger.IsTrace)
            {
                lock (_handlerLock)
                {
                    Dictionary<long, string> all = new Dictionary<long, string>();
                    StringBuilder builder = new StringBuilder();
                    builder.AppendLine($"SENT {_sentBatches.Count} PENDING {_pendingBatches.Count} DEPENDENCIES {_headerDependencies.Count}");
                    foreach (var headerDependency in _headerDependencies
                        .OrderByDescending(d => d.Value.HeadersSyncBatch.EndNumber)
                        .ThenByDescending(d => d.Value.HeadersSyncBatch.StartNumber))
                    {
                        all.Add(headerDependency.Value.HeadersSyncBatch.EndNumber, $"  DEPENDENCY {headerDependency.Value}");
                    }

                    foreach (var pendingBatch in _pendingBatches
                        .Where(b => b.BatchType == BatchType.Headers)
                        .OrderByDescending(d => d.HeadersSyncBatch.EndNumber)
                        .ThenByDescending(d => d.HeadersSyncBatch.StartNumber))
                    {
                        all.Add(pendingBatch.HeadersSyncBatch.EndNumber, $"  PENDING    {pendingBatch}");
                    }

                    foreach (var sentBatch in _sentBatches
                        .Where(sb => sb.Key.BatchType == BatchType.Headers)
                        .OrderByDescending(d => d.Key.HeadersSyncBatch.EndNumber)
                        .ThenByDescending(d => d.Key.HeadersSyncBatch.StartNumber))
                    {
                        all.Add(sentBatch.Key.HeadersSyncBatch.EndNumber, $"  SENT       {sentBatch.Key}");
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

        public (BlocksDataHandlerResult Result, int BlocksConsumed) HandleResponse(BlockSyncBatch batch)
        {
            lock (_handlerLock)
            {
                if (batch.HeadersSyncBatch != null)
                {
                    int added = InsertHeaders(batch);
                    return (BlocksDataHandlerResult.OK, added);
                }
                
                if (batch.BodiesSyncBatch != null)
                {
                    int added = InsertBodies(batch);
                    return (BlocksDataHandlerResult.OK, added);
                }

                return (BlocksDataHandlerResult.InvalidFormat, 0);
            }
        }

        private int InsertBodies(BlockSyncBatch batch)
        {
            var bodiesSyncBatch = batch.BodiesSyncBatch;
            if (bodiesSyncBatch.Response == null)
            {
                if (_logger.IsTrace) _logger.Trace($"{batch} - came back EMPTY");
                batch.Allocation = null;
                _pendingBatches.Push(batch);
                return 0;
            }
            
            int added = 0;
            for (int i = 0; i < batch.BodiesSyncBatch.Response.Length; i++)
            {
                BlockBody blockBody = batch.BodiesSyncBatch.Response[i];
                if (blockBody == null)
                {
                    break;
                }
                
                Block block = new Block(_blockTree.FindHeader(batch.BodiesSyncBatch.Request[i]), blockBody.Transactions, blockBody.Ommers);
                if (block.CalculateTxRoot() != block.TransactionsRoot ||
                    block.CalculateOmmersHash() != block.OmmersHash)
                {
                    _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.PreviousPeerInfo);
                    break;
                }

                if (!block.IsGenesis)
                {
                    _blockTree.Insert(block);
                    added++;
                }
            }

            if (added < batch.BodiesSyncBatch.Request.Length)
            {
                BlockSyncBatch fillerBatch = new BlockSyncBatch();
                fillerBatch.MinNumber = batch.MinNumber;
                fillerBatch.BodiesSyncBatch = new BodiesSyncBatch();
                
                int originalLength = batch.BodiesSyncBatch.Request.Length;
                fillerBatch.BodiesSyncBatch.Request = new Keccak[originalLength - added];
                for (int i = added; i < originalLength; i++)
                {
                    fillerBatch.BodiesSyncBatch.Request[i - added] = batch.BodiesSyncBatch.Request[i];
                }
                
                _pendingBatches.Push(fillerBatch);
            }

            return added;
        }

        public void StartNewRound()
        {
            LowestRequestedHeaderNumber = null;

            _pendingBatches.Clear();
            _headerDependencies.Clear();
            _sentBatches.Clear();
        }

        private int InsertHeaders(BlockSyncBatch batch)
        {
            batch.MarkHandlingStart();
            try
            {
                var headersSyncBatch = batch.HeadersSyncBatch;
                if (headersSyncBatch.Response == null)
                {
                    if (_logger.IsTrace) _logger.Trace($"{batch} - came back EMPTY");
                    batch.Allocation = null;
                    _pendingBatches.Push(batch);
                    return 0;
                }

                if (headersSyncBatch.Response.Length > batch.HeadersSyncBatch.RequestSize)
                {
                    if (_logger.IsWarn) _logger.Warn($"Peer sent too long response ({headersSyncBatch.Response.Length}) to {batch}");
                    _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.PreviousPeerInfo);
                    _pendingBatches.Push(batch);
                    return 0;
                }

                decimal ratio = (decimal) _itemsSaved / (_requestsSent == 0 ? 1 : _requestsSent);
                _requestsSent += batch.HeadersSyncBatch.RequestSize;

                long addedLast = batch.HeadersSyncBatch.StartNumber - 1;
                long addedEarliest = batch.HeadersSyncBatch.EndNumber + 1;
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
                        if (header.Hash != NextHash)
                        {
                            if (header.Number == (_blockTree.LowestInsertedHeader?.Number ?? _pivotNumber + 1) - 1)
                            {
                                if (_logger.IsWarn) _logger.Warn($"{batch} - ended up IGNORED - different branch");
                                _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.PreviousPeerInfo);
                                break;
                            }

                            if (header.Number == _blockTree.LowestInsertedHeader?.Number)
                            {
                                if (_logger.IsWarn) _logger.Warn($"{batch} - ended up IGNORED - different branch");
                                _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.PreviousPeerInfo);
                                break;
                            }

                            if (_headerDependencies.ContainsKey(header.Number))
                            {
                                _pendingBatches.Push(batch);
                                throw new InvalidOperationException($"Only one header dependency expected ({batch})");
                            }

                            for (int j = 0; j < batch.HeadersSyncBatch.Response.Length; j++)
                            {
                                BlockHeader current = batch.HeadersSyncBatch.Response[j];
                                if (batch.HeadersSyncBatch.Response[j] != null)
                                {
                                    addedEarliest = Math.Min(addedEarliest, current.Number);
                                    addedLast = Math.Max(addedLast, current.Number);
                                }
                                else
                                {
                                    break;
                                }
                            }

                            BlockSyncBatch dependentBatch = BuildDependentBatch(batch, addedLast, addedEarliest);
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
                                _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.PreviousPeerInfo);
                            }

                            break;
                        }
                    }

                    header.TotalDifficulty = NextTotalDifficulty;
                    AddBlockResult addBlockResult = SuggestHeader(header);
                    if (addBlockResult == AddBlockResult.InvalidBlock)
                    {
                        if (batch.Allocation != null)
                        {
                            if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID bad block");
                            _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.PreviousPeerInfo);
                        }

                        break;
                    }

                    addedEarliest = Math.Min(addedEarliest, header.Number);
                    addedLast = Math.Max(addedLast, header.Number);
                }

                int added = (int) (addedLast - addedEarliest + 1);
                int leftFillerSize = (int) (addedEarliest - batch.HeadersSyncBatch.StartNumber);
                int rightFillerSize = (int) (batch.HeadersSyncBatch.EndNumber - addedLast);
                if (added + leftFillerSize + rightFillerSize != batch.HeadersSyncBatch.RequestSize)
                {
                    throw new Exception($"Added {added} + left {leftFillerSize} + right {rightFillerSize} != request size {batch.HeadersSyncBatch.RequestSize} in  {batch}");
                }

                added = Math.Max(0, added);

                if (added < batch.HeadersSyncBatch.RequestSize)
                {
                    if (added <= 0)
                    {
                        batch.HeadersSyncBatch.Response = null;
                        _pendingBatches.Push(batch);
                    }
                    else
                    {
                        if (leftFillerSize > 0)
                        {
                            BlockSyncBatch leftFiller = BuildLeftFiller(batch, leftFillerSize);
                            _pendingBatches.Push(leftFiller);
                            if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {leftFiller}");
                        }

                        if (rightFillerSize > 0)
                        {
                            BlockSyncBatch rightFiller = BuildRightFiller(batch, rightFillerSize);
                            _pendingBatches.Push(rightFiller);
                            if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {rightFiller}");
                        }
                    }
                }

                if (added == 0)
                {
                    if (batch.Allocation != null)
                    {
                        if (_logger.IsWarn) _logger.Warn($"{batch} - reporting no progress");
                        _syncPeerPool.ReportNoSyncProgress(batch.Allocation);
                    }
                }

                batch.MarkHandlingEnd();

                if (_blockTree.LowestInsertedHeader != null)
                {
                    _syncStats.ReportBlocksDownload(_pivotNumber - (_blockTree.LowestInsertedHeader?.Number ?? _pivotNumber), _pivotNumber, ratio);
                }

                if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_blockTree.LowestInsertedHeader?.Number} | HANDLED {batch}");

                return added;
            }
            finally
            {
                _sentBatches.TryRemove(batch, out _);
            }
        }

        private static BlockSyncBatch BuildRightFiller(BlockSyncBatch batch, int rightFillerSize)
        {
            BlockSyncBatch rightFiller = new BlockSyncBatch();
            rightFiller.HeadersSyncBatch = new HeadersSyncBatch();
            rightFiller.HeadersSyncBatch.StartNumber = batch.HeadersSyncBatch.EndNumber - rightFillerSize + 1;
            rightFiller.HeadersSyncBatch.RequestSize = rightFillerSize;
            rightFiller.Allocation = null;
            rightFiller.MinNumber = batch.MinNumber;
            return rightFiller;
        }

        private static BlockSyncBatch BuildLeftFiller(BlockSyncBatch batch, int leftFillerSize)
        {
            BlockSyncBatch leftFiller = new BlockSyncBatch();
            leftFiller.HeadersSyncBatch = new HeadersSyncBatch();
            leftFiller.HeadersSyncBatch.StartNumber = batch.HeadersSyncBatch.StartNumber;
            leftFiller.HeadersSyncBatch.RequestSize = leftFillerSize;
            leftFiller.Allocation = null;
            leftFiller.MinNumber = batch.MinNumber;
            return leftFiller;
        }

        private static BlockSyncBatch BuildDependentBatch(BlockSyncBatch batch, long addedLast, long addedEarliest)
        {
            BlockSyncBatch dependentBatch = new BlockSyncBatch();
            dependentBatch.HeadersSyncBatch = new HeadersSyncBatch();
            dependentBatch.HeadersSyncBatch.StartNumber = batch.HeadersSyncBatch.StartNumber;
            dependentBatch.HeadersSyncBatch.RequestSize = (int) (addedLast - addedEarliest + 1);
            dependentBatch.Allocation = null;
            dependentBatch.MinNumber = batch.MinNumber;
            dependentBatch.HeadersSyncBatch.Response = batch.HeadersSyncBatch.Response
                .Skip((int) (addedEarliest - batch.HeadersSyncBatch.StartNumber))
                .Take((int) (addedLast - addedEarliest + 1)).ToArray();
            dependentBatch.PreviousPeerInfo = batch.Allocation?.Current ?? batch.PreviousPeerInfo;
            return dependentBatch;
        }

        private AddBlockResult SuggestHeader(BlockHeader header)
        {
            if (header.IsGenesis)
            {
                return AddBlockResult.AlreadyKnown;
            }

            AddBlockResult addBlockResult = _blockTree.Insert(header);

            if (addBlockResult == AddBlockResult.Added || addBlockResult == AddBlockResult.AlreadyKnown)
            {
                NextHash = header.ParentHash;
                NextTotalDifficulty = (header.TotalDifficulty ?? 0) - header.Difficulty;
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
                BlockSyncBatch batch = _headerDependencies[parentNumber];
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