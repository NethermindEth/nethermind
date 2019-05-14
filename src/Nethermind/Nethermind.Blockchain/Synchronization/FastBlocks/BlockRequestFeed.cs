using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using Nethermind.Blockchain.Synchronization.FastSync;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class BlocksRequestFeed : IBlockRequestFeed
    {
        private readonly IBlockTree _blockTree;
        private readonly IEthSyncPeerPool _syncPeerPool;

        public BlocksRequestFeed(IBlockTree blockTree, IEthSyncPeerPool syncPeerPool)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
//            _bestRequestedHeader = blockTree.BestSuggested.Hash;
        }

        private long _bestRequestedHeader = 0;
        private long _bestRequestedBody = 0;

        private ConcurrentQueue<BlockSyncBatch> _pendingBatches = new ConcurrentQueue<BlockSyncBatch>();

        public BlockSyncBatch PrepareRequest()
        {
            if (_syncPeerPool.AllPeers.Max(p => p.TotalDifficulty) <= (_blockTree.BestSuggested?.TotalDifficulty ?? 0))
            {
                return null;
            }
            
            if (_pendingBatches.TryDequeue(out BlockSyncBatch enqueuedBatch))
            {
                return enqueuedBatch;
            }

            BlockSyncBatch batch = new BlockSyncBatch();
            batch.HeadersSyncBatch = new HeadersSyncBatch();
            batch.HeadersSyncBatch.Reverse = false;
            batch.HeadersSyncBatch.Skip = 0;
            batch.HeadersSyncBatch.StartNumber = _bestRequestedHeader;
            batch.HeadersSyncBatch.RequestSize = 256;
            _bestRequestedHeader = _bestRequestedHeader + 256;
            return batch;
        }

        public (BlocksDataHandlerResult Result, int BlocksConsumed) HandleResponse(BlockSyncBatch syncBatch)
        {
            if (syncBatch.HeadersSyncBatch != null)
            {
                var headersSyncBatch = syncBatch.HeadersSyncBatch;
                foreach (BlockHeader header in headersSyncBatch.Response)
                {
                    _blockTree.SuggestHeader(header);
                }

                return (BlocksDataHandlerResult.OK, headersSyncBatch.Response.Length);
            }

            return (BlocksDataHandlerResult.InvalidFormat, 0);
        }

        public bool IsFullySynced(Keccak stateRoot)
        {
            throw new System.NotImplementedException();
        }

        public int TotalBlocksPending { get; }
    }
}