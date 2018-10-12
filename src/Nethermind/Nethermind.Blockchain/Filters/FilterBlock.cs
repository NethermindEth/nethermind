using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Filters
{
    public class FilterBlock
    {
        public UInt256 BlockId { get; }
        public FilterBlockType Type { get; }
        
        public FilterBlock(UInt256 blockId)
        {
            BlockId = blockId;
        }
        
        public FilterBlock(FilterBlockType type)
        {
            Type = type;
        }
    }
}