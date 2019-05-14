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
using System.ComponentModel.Design;
using System.IO.Enumeration;
using System.Linq;
using Nethermind.Blockchain.Synchronization.FastSync;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class BlocksRequestFeed : IBlockRequestFeed
    {
        private readonly IBlockTree _blockTree;
        private readonly IEthSyncPeerPool _syncPeerPool;

        private ConcurrentDictionary<long, List<BlockSyncBatch>> _headerDependencies = new ConcurrentDictionary<long, List<BlockSyncBatch>>();

        public BlocksRequestFeed(IBlockTree blockTree, IEthSyncPeerPool syncPeerPool)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
//            _bestRequestedHeader = blockTree.BestSuggested.Hash;
        }

        private long _bestRequestedHeader = 1;
        private long _bestRequestedBody = 0;

        private ConcurrentQueue<BlockSyncBatch> _pendingBatches = new ConcurrentQueue<BlockSyncBatch>();
        private HashSet<BlockSyncBatch> _sentBatches = new HashSet<BlockSyncBatch>();

        public BlockSyncBatch PrepareRequest()
        {
            UInt256 maxDifficulty = _syncPeerPool.AllPeers.Max(p => p.TotalDifficulty);
            long maxNumber = _syncPeerPool.AllPeers.Max(p => p.HeadNumber);
            if (maxDifficulty <= (_blockTree.BestSuggested?.TotalDifficulty ?? 0))
            {
                return null;
            }

            if (maxNumber <= _bestRequestedHeader && _sentBatches.Count != 0)
            {
                return null;
            }

            bool isReorg = maxNumber <= _bestRequestedHeader;

            if (_pendingBatches.TryDequeue(out BlockSyncBatch enqueuedBatch))
            {
                return enqueuedBatch;
            }

            BlockSyncBatch batch = new BlockSyncBatch();
            batch.HeadersSyncBatch = new HeadersSyncBatch();
            batch.HeadersSyncBatch.StartNumber = isReorg ? maxNumber - RequestSize + 1 : _bestRequestedHeader - 1;
            batch.HeadersSyncBatch.RequestSize = (1 + maxNumber - batch.HeadersSyncBatch.StartNumber.Value) < RequestSize ? (int) (1 + maxNumber - batch.HeadersSyncBatch.StartNumber.Value) : RequestSize;
            batch.IsReorgBatch = isReorg;
            _bestRequestedHeader = Math.Max(batch.HeadersSyncBatch.StartNumber + batch.HeadersSyncBatch.RequestSize ?? 0, _bestRequestedHeader);
            _sentBatches.Add(batch);
            return batch;
        }

        private const int RequestSize = 256;

        public (BlocksDataHandlerResult Result, int BlocksConsumed) HandleResponse(BlockSyncBatch syncBatch)
        {
            if (syncBatch.HeadersSyncBatch != null)
            {
                int added = SuggestBatch(syncBatch);

                return (BlocksDataHandlerResult.OK, added);
            }

            return (BlocksDataHandlerResult.InvalidFormat, 0);
        }

        private int SuggestBatch(BlockSyncBatch syncBatch)
        {
            try
            {
                var headersSyncBatch = syncBatch.HeadersSyncBatch;
                if (headersSyncBatch.Response == null)
                {
                    _pendingBatches.Enqueue(syncBatch);
                    return 0;
                }
                
                int added = 0;
                foreach (BlockHeader header in headersSyncBatch.Response)
                {
                    AddBlockResult addBlockResult = SuggestHeader(header);
                    if (addBlockResult == AddBlockResult.UnknownParent && added == 0)
                    {
                        if ((_blockTree.BestSuggested?.Number ?? 0) >= header.Number || syncBatch.IsReorgBatch)
                        {
                            // reorg
                            BlockSyncBatch reorgBatch = new BlockSyncBatch();
                            reorgBatch.IsReorgBatch = true;
                            reorgBatch.HeadersSyncBatch = new HeadersSyncBatch();
                            if (syncBatch.HeadersSyncBatch.StartNumber == null)
                            {
                                throw new InvalidOperationException();
                            }

                            reorgBatch.HeadersSyncBatch.StartNumber = Math.Max(0, syncBatch.HeadersSyncBatch.StartNumber.Value - RequestSize);
                            reorgBatch.HeadersSyncBatch.RequestSize = RequestSize;
                            _pendingBatches.Enqueue(reorgBatch);
                        }
                        else
                        {
                            if (!_headerDependencies.ContainsKey(header.Number - 1))
                            {
                                _headerDependencies[header.Number - 1] = new List<BlockSyncBatch>();
                            }

                            _headerDependencies[header.Number - 1].Add(syncBatch);
                        }

                        break;
                    }

                    if (addBlockResult == AddBlockResult.InvalidBlock || addBlockResult == AddBlockResult.UnknownParent)
                    {
                        BlockSyncBatch fixedSyncBatch = new BlockSyncBatch();
                        fixedSyncBatch.HeadersSyncBatch = new HeadersSyncBatch();
                        fixedSyncBatch.HeadersSyncBatch.StartNumber = syncBatch.HeadersSyncBatch.StartNumber + added;
                        fixedSyncBatch.HeadersSyncBatch.RequestSize = RequestSize - added;
                        _pendingBatches.Enqueue(fixedSyncBatch);
                    }
                    else
                    {
                        added++;
                    }
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
                    SuggestBatch(batch);
                }
            }

            return addBlockResult;
        }

        public bool IsFullySynced(Keccak stateRoot)
        {
            throw new System.NotImplementedException();
        }

        public int TotalBlocksPending { get; }
    }
}