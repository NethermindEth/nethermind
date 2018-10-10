using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Filters
{
    public class FilterBlock
    {
        public UInt256 BlockId { get; set;}
        public FilterBlockType Type { get; set;}
    }
}