using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Filters
{
    public abstract class FilterBase
    {
        public int FilterId { get; set; }
    }
    
    public class BlockFilter : FilterBase
    {
        public BlockFilter(UInt256 startBlockNumber)
        {
            StartBlockNumber = startBlockNumber;
        }

        public UInt256 StartBlockNumber { get; set; }
    }
    
    public interface IFilterStore
    {
        BlockFilter CreateBlockFilter(UInt256 startBlockNumber);
    }

    public class FilterStore : IFilterStore
    {
        private int _filterId;

        public BlockFilter CreateBlockFilter(UInt256 startBlockNumber)
        {
            BlockFilter blockFilter = new BlockFilter(startBlockNumber);
            blockFilter.FilterId = ++_filterId;
            return blockFilter;
        }
    }
}