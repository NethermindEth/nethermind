using Nethermind.Core;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class HeadersSyncBatch
    {
        public long Start { get; set; }
        public long Skip { get; set; }
        public bool Reverse { get; set; }
        
        public bool RequestSize { get; set; }
        
        public BlockHeader[] Response { get; set; }
    }
}