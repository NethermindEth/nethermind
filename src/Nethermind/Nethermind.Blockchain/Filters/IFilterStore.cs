using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Newtonsoft.Json.Serialization;

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

        Filter CreateFilter(Block fromBlock, Block toBlock, object address = null, 
            IEnumerable<object> topics = null);
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

        public Filter CreateFilter(Block fromBlock, Block toBlock,
            object address = null, IEnumerable<object> topics = null)
        {
            var filter = new Filter
            {
                FilterId = ++_filterId,
                FromBlock = fromBlock,
                ToBlock = toBlock,
                Address = GetAddress(address),
                Topics = GetTopics(topics),
            };

            return filter;
        }

        private static FilterAddress GetAddress(object address)
            => address is null
                ? null
                : new FilterAddress
                {
                    Data = address is string s ? Bytes.FromHexString(s) : null,
                    Addresses = address is IEnumerable<string> e ? e.Select(a => new Address(a)).ToList() : null
                };

        private static IEnumerable<FilterTopic> GetTopics(IEnumerable<object> topics)
            => topics?.Select(GetTopic);

        private static FilterTopic GetTopic(object obj)
        {
            switch (obj)
            {
                case null:
                    return null;
                case string topic:
                    return new FilterTopic
                    {
                        First = Bytes.FromHexString(topic)
                    };
            }

            var topics = (obj as IEnumerable<string>)?.ToList();
            var first = topics?.FirstOrDefault();
            var second = topics?.Skip(1).FirstOrDefault();

            return new FilterTopic
            {
                First = first is null ? null : Bytes.FromHexString(first),
                Second = second is null ? null : Bytes.FromHexString(second),
            };
        }
    }
}