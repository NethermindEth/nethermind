using System.Collections.Generic;
using Nethermind.Core;
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

        Filter CreateFilter(FilterBlock fromBlock, FilterBlock toBlock,
            FilterAddress address = null, IEnumerable<FilterData> topics = null);
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

        public Filter CreateFilter(FilterBlock fromBlock, FilterBlock toBlock,
            FilterAddress address = null, IEnumerable<FilterData> topics = null)
        {
            //TODO: check type 
            var filter = new Filter
            {
                FromBlock = fromBlock,
                ToBlock = toBlock,
                Address = address,
                Topics = topics
            };
            filter.FilterId = ++_filterId;

            return filter;
        }
    }
}