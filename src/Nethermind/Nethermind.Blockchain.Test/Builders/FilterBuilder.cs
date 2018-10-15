using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Test.Builders
{
    public class FilterBuilder
    {
        private int _id;
        private FilterBlock _fromBlock = new FilterBlock(FilterBlockType.Latest);
        private FilterBlock _toBlock = new FilterBlock(FilterBlockType.Latest);
        private FilterAddress _address = new FilterAddress();
        private TopicsFilter _topicsFilter = new TopicsFilter(new TopicExpression[0]);

        private FilterBuilder()
        {
        }

        public static FilterBuilder CreateFilter()
        {
            return new FilterBuilder();
        }
        
        public FilterBuilder WithId(int id)
        {
            _id = id;

            return this;
        }

        public FilterBuilder FromBlockId(UInt256 blockId)
        {
            _fromBlock = new FilterBlock(blockId);

            return this;
        }

        public FilterBuilder FromEarliestBlock()
        {
            _fromBlock = new FilterBlock(FilterBlockType.Earliest);

            return this;
        }

        public FilterBuilder FromLatestBlock()
        {
            _fromBlock = new FilterBlock(FilterBlockType.Latest);

            return this;
        }

        public FilterBuilder FromPendingBlock()
        {
            _fromBlock = new FilterBlock(FilterBlockType.Pending);

            return this;
        }

        public FilterBuilder ToBlockId(UInt256 blockId)
        {
            _toBlock = new FilterBlock(blockId);

            return this;
        }

        public FilterBuilder ToEarliestBlock()
        {
            _toBlock = new FilterBlock(FilterBlockType.Earliest);

            return this;
        }

        public FilterBuilder ToLatestBlock()
        {
            _toBlock = new FilterBlock(FilterBlockType.Latest);

            return this;
        }

        public FilterBuilder ToPendingBlock()
        {
            _toBlock = new FilterBlock(FilterBlockType.Pending);

            return this;
        }

        public FilterBuilder WithAddress(Address address)
        {
            _address = new FilterAddress { Address = address};

            return this;
        }

        public FilterBuilder WithAddresses(IEnumerable<Address> addresses)
        {
            _address = new FilterAddress { Addresses = addresses};

            return this;
        }

        public FilterBuilder WithTopicExpressions(params TopicExpression[] expressions)
        {
            _topicsFilter = new TopicsFilter(expressions.ToArray());

            return this;
        }

        public Filter Build()
        {
            return new Filter(_id, _fromBlock, _toBlock, _address, _topicsFilter);
        }
    }
}