using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class BodiesSyncBatch
    {
        public Keccak[] Request { get; set; }
        public Block[] Response { get; set; }
        public SyncPeerAllocation AssignedPeer { get; set; }
    }
}