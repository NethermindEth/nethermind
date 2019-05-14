using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class HeadersSyncBatch
    {
        public Keccak StartHash { get; set; }
        public long? StartNumber { get; set; }
        public int Skip { get; set; }
        public bool Reverse { get; set; }
        
        public int RequestSize { get; set; }
        
        public BlockHeader[] Response { get; set; }
    }
}