using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using Nethermind.Blockchain.Synchronization.FastSync;
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
        }

        private long BestRequestedHeader = 0;
        private long BestRequestedBody = 0;
        private long BestSuggestedHeader = 0;
        private long BestSuggestedBody = 0;

        private ConcurrentQueue<BlockSyncBatch> _pendingBatches = new ConcurrentQueue<BlockSyncBatch>();
        
        public BlockSyncBatch PrepareRequest()
        {
            if(_pendingBatches.TryDequeue(out BlockSyncBatch enqueuedBatch))
            {
                return enqueuedBatch;
            }

            if (BestRequestedBody < BestRequestedHeader)
            {
                BlockSyncBatch batch = new BlockSyncBatch();
                batch.HeadersSyncBatch = new HeadersSyncBatch();
                batch.HeadersSyncBatch.Reverse = false;
                batch.HeadersSyncBatch.Skip = 0;
                batch.HeadersSyncBatch.Start = BestRequestedHeader;
                
            }
        }

        public (NodeDataHandlerResult Result, int BlocksConsumed) HandleResponse(BlockSyncBatch syncBatch)
        {
            throw new System.NotImplementedException();
        }

        public bool IsFullySynced(Keccak stateRoot)
        {
            throw new System.NotImplementedException();
        }

        public int TotalBlocksPending { get; }
    }
}