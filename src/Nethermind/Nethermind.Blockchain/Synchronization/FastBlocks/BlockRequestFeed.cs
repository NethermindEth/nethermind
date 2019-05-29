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
using System.IO;
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Json;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

// ReSharper disable InconsistentlySynchronizedField
namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class BlockRequestFeed : IBlockRequestFeed
    {
        private ILogger _logger;
        private IBlockTree _blockTree;
        private IEthSyncPeerPool _syncPeerPool;
        private ISyncConfig _syncConfig;

        private ConcurrentDictionary<long, BlockSyncBatch> _headerDependencies = new ConcurrentDictionary<long, BlockSyncBatch>();
        private ConcurrentDictionary<BlockSyncBatch, object> _sentBatches = new ConcurrentDictionary<BlockSyncBatch, object>();
        private ConcurrentStack<BlockSyncBatch> _pendingBatches = new ConcurrentStack<BlockSyncBatch>();

        private object _handlerLock = new object();
        private SyncStats _syncStats;
        private SyncStats _bodiesSyncStats;

        private const int _headerRequestSize = 512;
        private const int _bodiesRequestSize = 512;

        public long StartNumber { get; set; }
        public Keccak StartBodyHash { get; set; }
        public Keccak StartHeaderHash { get; set; }
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
            _syncStats = new SyncStats("Headers", logManager);
            _bodiesSyncStats = new SyncStats("Bodies", logManager);

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
                NextHash = StartHeaderHash;
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

            BlockSyncBatch batch;
            if (_pendingBatches.Any())
            {
                _pendingBatches.TryPop(out batch);
                batch.MarkRetry();
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

                    Keccak lastHash = LowestRequestedBodyHash ?? StartBodyHash;
                    BlockHeader lastHeader = _blockTree.FindHeader(lastHash);
                    if (lastHeader == null)
                    {
                        throw new InvalidDataException($"Last header is null for {lastHash} at lowest inserted body: {_blockTree.LowestInsertedBody?.Number}");
                    }
                    
                    batch = new BlockSyncBatch();
                    int thisRequestSize = (int) Math.Min(lastHeader.Number + 1, _bodiesRequestSize);
                    batch.Bodies = new BodiesSyncBatch();
                    batch.Bodies.Request = new Keccak[thisRequestSize];
                    batch.Bodies.Headers = new BlockHeader[thisRequestSize];
                    batch.MinNumber = lastHeader.Number;

                    for (int i = thisRequestSize - 1; i >= 0; i--)
                    {
                        batch.Bodies.Headers[i] = lastHeader;
                        LowestRequestedBodyHash = batch.Bodies.Request[i] = lastHeader.Hash;
                        lastHeader = _blockTree.FindHeader(lastHeader.ParentHash);
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

                    batch = new BlockSyncBatch();
                    batch.MinNumber = LowestRequestedHeaderNumber ?? StartNumber;
                    batch.Headers = new HeadersSyncBatch();
                    batch.Headers.StartNumber = Math.Max(0, (LowestRequestedHeaderNumber - 1 ?? StartNumber) - (_headerRequestSize - 1));
                    batch.Headers.RequestSize = (int) Math.Min(LowestRequestedHeaderNumber ?? StartNumber + 1, _headerRequestSize);
                    LowestRequestedHeaderNumber = batch.Headers.StartNumber;
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
                        .OrderByDescending(d => d.Value.Headers.EndNumber)
                        .ThenByDescending(d => d.Value.Headers.StartNumber))
                    {
                        all.Add(headerDependency.Value.Headers.EndNumber, $"  DEPENDENCY {headerDependency.Value}");
                    }

                    foreach (var pendingBatch in _pendingBatches
                        .Where(b => b.BatchType == BatchType.Headers)
                        .OrderByDescending(d => d.Headers.EndNumber)
                        .ThenByDescending(d => d.Headers.StartNumber))
                    {
                        all.Add(pendingBatch.Headers.EndNumber, $"  PENDING    {pendingBatch}");
                    }

                    foreach (var sentBatch in _sentBatches
                        .Where(sb => sb.Key.BatchType == BatchType.Headers)
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

        public (BlocksDataHandlerResult Result, int BlocksConsumed) HandleResponse(BlockSyncBatch batch)
        {
            lock (_handlerLock)
            {
                try
                {
                    batch.MarkHandlingStart();
                    if (batch.Headers != null)
                    {
                        int added = InsertHeaders(batch);
                        return (BlocksDataHandlerResult.OK, added);
                    }

                    if (batch.Bodies != null)
                    {
                        int added = InsertBodies(batch);
                        return (BlocksDataHandlerResult.OK, added);
                    }

                    return (BlocksDataHandlerResult.InvalidFormat, 0);
                }
                finally
                {
                    batch.MarkHandlingEnd();
                    _sentBatches.TryRemove(batch, out _);
                }
            }
        }

        private int InsertBodies(BlockSyncBatch batch)
        {
            var bodiesSyncBatch = batch.Bodies;
            if (bodiesSyncBatch.Response == null)
            {
                if (_logger.IsTrace) _logger.Trace($"{batch} - came back EMPTY");
                batch.Allocation = null;
                _pendingBatches.Push(batch);
                return 0;
            }

            int added = 0;
            for (int i = 0; i < batch.Bodies.Response.Length; i++)
            {
                BlockBody blockBody = batch.Bodies.Response[i];
                if (blockBody == null)
                {
                    break;
                }

                Block block = new Block(batch.Bodies.Headers[i], blockBody.Transactions, blockBody.Ommers);
                if (block.CalculateTxRoot() != block.TransactionsRoot ||
                    block.CalculateOmmersHash() != block.OmmersHash)
                {
                    if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID - tx or ommers");
                    _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.PreviousPeerInfo);
                    break;
                }

                if (!block.IsGenesis)
                {
                    _blockTree.Insert(block);
                    added++;
                }
            }

            if (added < batch.Bodies.Request.Length)
            {
                BlockSyncBatch fillerBatch = new BlockSyncBatch();
                fillerBatch.MinNumber = batch.MinNumber;
                fillerBatch.Bodies = new BodiesSyncBatch();

                int originalLength = batch.Bodies.Request.Length;
                fillerBatch.Bodies.Request = new Keccak[originalLength - added];
                for (int i = added; i < originalLength; i++)
                {
                    fillerBatch.Bodies.Request[i - added] = batch.Bodies.Request[i];
                }

                if (_logger.IsDebug) _logger.Debug($"{batch} -> FILLER {fillerBatch}");
                _pendingBatches.Push(fillerBatch);
            }

            if (_blockTree.LowestInsertedHeader != null)
            {
                _bodiesSyncStats.ReportDownloadProgress(_pivotNumber - (_blockTree.LowestInsertedBody?.Number ?? _pivotNumber), _pivotNumber);
            }
            
            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_blockTree.LowestInsertedBody?.Number} | HANDLED {batch}");

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
            var headersSyncBatch = batch.Headers;
            if (headersSyncBatch.Response == null)
            {
                if (_logger.IsTrace) _logger.Trace($"{batch} - came back EMPTY");
                batch.Allocation = null;
                _pendingBatches.Push(batch);
                return 0;
            }

            if (headersSyncBatch.Response.Length > batch.Headers.RequestSize)
            {
                if (_logger.IsWarn) _logger.Warn($"Peer sent too long response ({headersSyncBatch.Response.Length}) to {batch}");
                _syncPeerPool.ReportInvalid(batch.Allocation?.Current ?? batch.PreviousPeerInfo);
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

            if (_blockTree.LowestInsertedHeader != null)
            {
                _syncStats.ReportDownloadProgress(_pivotNumber - (_blockTree.LowestInsertedHeader?.Number ?? _pivotNumber), _pivotNumber, ratio);
            }

            if (_logger.IsDebug) _logger.Debug($"LOWEST_INSERTED {_blockTree.LowestInsertedHeader?.Number} | HANDLED {batch}");

            return added;
        }

        private static BlockSyncBatch BuildRightFiller(BlockSyncBatch batch, int rightFillerSize)
        {
            BlockSyncBatch rightFiller = new BlockSyncBatch();
            rightFiller.Headers = new HeadersSyncBatch();
            rightFiller.Headers.StartNumber = batch.Headers.EndNumber - rightFillerSize + 1;
            rightFiller.Headers.RequestSize = rightFillerSize;
            rightFiller.Allocation = null;
            rightFiller.MinNumber = batch.MinNumber;
            return rightFiller;
        }

        private static BlockSyncBatch BuildLeftFiller(BlockSyncBatch batch, int leftFillerSize)
        {
            BlockSyncBatch leftFiller = new BlockSyncBatch();
            leftFiller.Headers = new HeadersSyncBatch();
            leftFiller.Headers.StartNumber = batch.Headers.StartNumber;
            leftFiller.Headers.RequestSize = leftFillerSize;
            leftFiller.Allocation = null;
            leftFiller.MinNumber = batch.MinNumber;
            return leftFiller;
        }

        private static BlockSyncBatch BuildDependentBatch(BlockSyncBatch batch, long addedLast, long addedEarliest)
        {
            BlockSyncBatch dependentBatch = new BlockSyncBatch();
            dependentBatch.Headers = new HeadersSyncBatch();
            dependentBatch.Headers.StartNumber = batch.Headers.StartNumber;
            dependentBatch.Headers.RequestSize = (int) (addedLast - addedEarliest + 1);
            dependentBatch.Allocation = null;
            dependentBatch.MinNumber = batch.MinNumber;
            dependentBatch.Headers.Response = batch.Headers.Response
                .Skip((int) (addedEarliest - batch.Headers.StartNumber))
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