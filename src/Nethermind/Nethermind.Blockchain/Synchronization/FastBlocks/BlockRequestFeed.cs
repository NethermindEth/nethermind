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
using System.ComponentModel;
using System.ComponentModel.Design;
using System.IO.Enumeration;
using System.Linq;
using Nethermind.Blockchain.Synchronization.FastSync;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class BlocksRequestFeed : IBlockRequestFeed
    {
        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        private readonly IEthSyncPeerPool _syncPeerPool;

        private ConcurrentDictionary<long, List<BlockSyncBatch>> _headerDependencies = new ConcurrentDictionary<long, List<BlockSyncBatch>>();

        public BlocksRequestFeed(IBlockTree blockTree, IEthSyncPeerPool syncPeerPool, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
//            _bestRequestedHeader = blockTree.BestSuggested.Hash;
            _syncStats = new SyncStats(logManager);

            StartNewRound();
        }

        private UInt256 _totalDifficultyOfBestHeaderProvider = UInt256.Zero;
        private long _bestRequestedHeader = 0;
        private long _bestRequestedBody = 0;
        private long _maxKnownNumber = 0;

        private Stack<BlockSyncBatch> _pendingBatches = new Stack<BlockSyncBatch>();
        private HashSet<BlockSyncBatch> _sentBatches = new HashSet<BlockSyncBatch>();

        public BlockSyncBatch PrepareRequest(int threshold)
        {
            UInt256 maxDifficulty = _syncPeerPool.AllPeers.Max(p => p.TotalDifficulty);
            long maxNumber = _syncPeerPool.AllPeers.Max(p => p.HeadNumber);
            _maxKnownNumber = maxNumber;
            if (maxDifficulty <= (_blockTree.BestSuggested?.TotalDifficulty ?? 0))
            {
                return null;
            }

            if (_pendingBatches.TryPop(out BlockSyncBatch stackedBatch))
            {
                stackedBatch.AssignedPeer = null;
                _sentBatches.Add(stackedBatch);
                return stackedBatch;
            }

            maxNumber -= threshold;
            if (maxNumber <= 0)
            {
                return null;
            }

            if (maxNumber <= _bestRequestedHeader
                && _pendingBatches.Count == 0
                && maxDifficulty <= _totalDifficultyOfBestHeaderProvider)
            {
                return null;
            }

            bool isReorg = maxNumber == (_blockTree.BestSuggested?.Number ?? 0) && maxDifficulty > _totalDifficultyOfBestHeaderProvider;
            if (isReorg && _sentBatches.Count > 0)
            {
                return null;
            }

            if (isReorg)
            {
                foreach (KeyValuePair<long, List<BlockSyncBatch>> headerDependency in _headerDependencies.OrderByDescending(hd => hd.Key))
                {
                    BlockSyncBatch reorgBatch = new BlockSyncBatch();
                    reorgBatch.HeadersSyncBatch = new HeadersSyncBatch();
                    reorgBatch.HeadersSyncBatch.StartNumber = Math.Max(0, headerDependency.Key - RequestSize + 1);
                    reorgBatch.HeadersSyncBatch.RequestSize = (int) Math.Min(headerDependency.Key + 1, RequestSize);
                    reorgBatch.IsReorgBatch = true;
                    reorgBatch.MinTotalDifficulty = _totalDifficultyOfBestHeaderProvider + 1;
                    if (!_headerDependencies.ContainsKey(reorgBatch.HeadersSyncBatch.StartNumber.Value - 1))
                    {
                        reorgBatch.AssignedPeer = null;
                        _pendingBatches.Push(reorgBatch);
                    }
                }

                if (_pendingBatches.Count == 0)
                {
                    BlockSyncBatch firstReorgBatch = new BlockSyncBatch();
                    firstReorgBatch.HeadersSyncBatch = new HeadersSyncBatch();
                    firstReorgBatch.HeadersSyncBatch.StartNumber = maxNumber;
                    firstReorgBatch.HeadersSyncBatch.RequestSize = 1;
                    firstReorgBatch.IsReorgBatch = true;
                    firstReorgBatch.MinTotalDifficulty = _totalDifficultyOfBestHeaderProvider + 1;
                    firstReorgBatch.AssignedPeer = null;
                    _pendingBatches.Push(firstReorgBatch);
                }
            }

            if (_pendingBatches.TryPop(out stackedBatch))
            {
                stackedBatch.AssignedPeer = null;
                _sentBatches.Add(stackedBatch);
                return stackedBatch;
            }

            if (isReorg)
            {
                throw new InvalidOperationException("Unexpected reorg true");
            }

            if (_bestRequestedHeader > (_blockTree.BestSuggested?.Number ?? 0) + 512 * 40)
            {
                return null;
            }

            BlockSyncBatch batch = new BlockSyncBatch();
            batch.HeadersSyncBatch = new HeadersSyncBatch();
            batch.HeadersSyncBatch.StartNumber = _bestRequestedHeader;
            batch.HeadersSyncBatch.RequestSize = (1 + maxNumber - batch.HeadersSyncBatch.StartNumber.Value) < RequestSize ? (int) (1 + maxNumber - batch.HeadersSyncBatch.StartNumber.Value) : RequestSize;
            _bestRequestedHeader = Math.Max(batch.HeadersSyncBatch.EndNumber ?? 0, _bestRequestedHeader);

            if (_sentBatches.Count > 0 && batch.HeadersSyncBatch.RequestSize == 1)
            {
                return null;
            }

            _sentBatches.Add(batch);
            return batch;
        }

        public int RequestSize { get; set; } = 256;

        private object _handlerLock = new object();
        private SyncStats _syncStats;

        public (BlocksDataHandlerResult Result, int BlocksConsumed) HandleResponse(BlockSyncBatch syncBatch)
        {
            lock (_handlerLock)
            {
                if (syncBatch.HeadersSyncBatch != null)
                {
                    int added = SuggestBatch(syncBatch);

                    return (BlocksDataHandlerResult.OK, added);
                }

                return (BlocksDataHandlerResult.InvalidFormat, 0);
            }
        }

        public void StartNewRound()
        {
            _bestRequestedHeader = _blockTree.BestSuggested?.Number ?? 0;
            _pendingBatches.Clear();
        }

        private int SuggestBatch(BlockSyncBatch syncBatch)
        {
            try
            {
                var headersSyncBatch = syncBatch.HeadersSyncBatch;
                if (headersSyncBatch.Response == null)
                {
                    syncBatch.AssignedPeer = null;
                    _pendingBatches.Push(syncBatch);
                    return 0;
                }

                bool stackRemaining = true;
                int added = 0;
                for (int i = 0; i < headersSyncBatch.Response.Length && i < headersSyncBatch.RequestSize; i++)
                {
                    BlockHeader header = headersSyncBatch.Response[i];
                    AddBlockResult? addBlockResult = null;
                    if (header != null)
                    {
                        _syncStats.ReportBlocksDownload(_blockTree.BestSuggested?.Number ?? 0, _bestRequestedHeader, _maxKnownNumber);
                        
                        if (i != 0 && header.ParentHash != headersSyncBatch.Response[i - 1].Hash)
                        {
                            _syncPeerPool.ReportInvalid(syncBatch.AssignedPeer);
                            break;
                        }

                        if (added == 0 && header.Number != headersSyncBatch.StartNumber)
                        {
                            _syncPeerPool.ReportInvalid(syncBatch.AssignedPeer);
                            break;
                        }

                        addBlockResult = SuggestHeader(header);
                        if (addBlockResult == AddBlockResult.UnknownParent && added == 0)
                        {
                            BlockHeader alternative = _blockTree.FindHeader(header.Number);
                            if (alternative?.TotalDifficulty != null && header.Difficulty > alternative.Difficulty
                                || syncBatch.IsReorgBatch)
                            {
                                if (!_headerDependencies.ContainsKey(header.Number - 1))
                                {
                                    _headerDependencies[header.Number - 1] = new List<BlockSyncBatch>();
                                }

                                syncBatch.AssignedPeer = null;
                                _headerDependencies[header.Number - 1].Add(syncBatch);
                                stackRemaining = false;
                            }
                            else if (alternative?.TotalDifficulty != null && header.Difficulty < alternative.Difficulty)
                            {
                                stackRemaining = true;
                            }
                            else
                            {
                                if (!_headerDependencies.ContainsKey(header.Number - 1))
                                {
                                    _headerDependencies[header.Number - 1] = new List<BlockSyncBatch>();
                                }

                                syncBatch.AssignedPeer = null;
                                _headerDependencies[header.Number - 1].Add(syncBatch);
                                stackRemaining = false;
                            }

                            break;
                        }
                    }

                    if (addBlockResult == null || addBlockResult.Value == AddBlockResult.InvalidBlock || addBlockResult.Value == AddBlockResult.UnknownParent)
                    {
                        break;
                    }

                    if (header.Number == _bestRequestedHeader)
                    {
                        _totalDifficultyOfBestHeaderProvider = UInt256.Max(_totalDifficultyOfBestHeaderProvider, syncBatch.AssignedPeer?.Current?.TotalDifficulty ?? 0);
                    }

                    added++;
                }

                if (added < syncBatch.HeadersSyncBatch.RequestSize && stackRemaining)
                {
                    if (added != 0)
                    {
                        added--;
                    }

                    BlockSyncBatch fixedSyncBatch = new BlockSyncBatch();
                    fixedSyncBatch.HeadersSyncBatch = new HeadersSyncBatch();
                    fixedSyncBatch.HeadersSyncBatch.StartNumber = syncBatch.HeadersSyncBatch.StartNumber + added;
                    fixedSyncBatch.HeadersSyncBatch.RequestSize = syncBatch.HeadersSyncBatch.RequestSize - added;
                    fixedSyncBatch.AssignedPeer = null;
                    _pendingBatches.Push(fixedSyncBatch);
                }

                if (added == 0 && stackRemaining && syncBatch.AssignedPeer != null)
                {
                    _syncPeerPool.ReportNoSyncProgress(syncBatch.AssignedPeer);
                }
                else
                {
                    added = syncBatch.HeadersSyncBatch.RequestSize;
                }

                return added;
            }
            finally
            {
                _sentBatches.Remove(syncBatch);
            }
        }

        private AddBlockResult SuggestHeader(BlockHeader header)
        {
            if (header.IsGenesis)
            {
                return AddBlockResult.AlreadyKnown;
            }

            AddBlockResult addBlockResult = _blockTree.SuggestHeader(header);
            if (addBlockResult == AddBlockResult.InvalidBlock)
            {
                return addBlockResult;
            }

            if (addBlockResult == AddBlockResult.UnknownParent)
            {
                return addBlockResult;
            }

            if (_headerDependencies.ContainsKey(header.Number))
            {
                foreach (BlockSyncBatch batch in _headerDependencies[header.Number].ToArray())
                {
                    batch.AssignedPeer = null;
                    SuggestBatch(batch);
                    _headerDependencies[header.Number].Remove(batch);
                }

                if (_headerDependencies[header.Number].Count == 0)
                {
                    _headerDependencies.Remove(header.Number, out _);
                }
            }

            return addBlockResult;
        }
    }
}