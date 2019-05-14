using Nethermind.Blockchain.Synchronization.FastSync;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public interface IBlockRequestFeed
    {
        BlockSyncBatch PrepareRequest();

        (BlocksDataHandlerResult Result, int BlocksConsumed) HandleResponse(BlockSyncBatch syncBatch);
        
        bool IsFullySynced(Keccak stateRoot);
        
        int TotalBlocksPending { get; }
    }
}