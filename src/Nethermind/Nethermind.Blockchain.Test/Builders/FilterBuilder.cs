//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Linq;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;

namespace Nethermind.Blockchain.Test.Builders
{
    public class FilterBuilder
    {
        private static int _id;
        private BlockParameter _fromBlock = new BlockParameter(BlockParameterType.Latest);
        private BlockParameter _toBlock = new BlockParameter(BlockParameterType.Latest);
        private AddressFilter _address = new AddressFilter((Address)null);
        private SequenceTopicsFilter _topicsFilter = new SequenceTopicsFilter();

        private FilterBuilder()
        {
        }

        public static FilterBuilder New()
        {
            int count = 0;
            return New(ref count);
        }

        public static FilterBuilder New(ref int currentFilterIndex)
        {
            _id = currentFilterIndex;
            currentFilterIndex++;
            return new FilterBuilder();
        }
        
        public FilterBuilder WithId(int id)
        {
            _id = id;
            return this;
        }

        public FilterBuilder FromBlock(long number)
        {
            _fromBlock = new BlockParameter(number);
            return this;
        }
        
        public FilterBuilder FromBlock(BlockParameterType blockType)
        {
            _fromBlock = new BlockParameter(blockType);

            return this;
        }

        public FilterBuilder FromEarliestBlock()
        {
            _fromBlock = new BlockParameter(BlockParameterType.Earliest);

            return this;
        }

        public FilterBuilder FromLatestBlock()
        {
            _fromBlock = new BlockParameter(BlockParameterType.Latest);

            return this;
        }
        
        public FilterBuilder FromFutureBlock()
        {
            _fromBlock = new BlockParameter(1000000);

            return this;
        }

        public FilterBuilder FromPendingBlock()
        {
            _fromBlock = new BlockParameter(BlockParameterType.Pending);

            return this;
        }

        public FilterBuilder ToBlock(long number)
        {
            _toBlock = new BlockParameter(number);

            return this;
        }

        public FilterBuilder ToEarliestBlock()
        {
            _toBlock = new BlockParameter(BlockParameterType.Earliest);

            return this;
        }

        public FilterBuilder ToLatestBlock()
        {
            _toBlock = new BlockParameter(BlockParameterType.Latest);

            return this;
        }

        public FilterBuilder ToPendingBlock()
        {
            _toBlock = new BlockParameter(BlockParameterType.Pending);

            return this;
        }

        public FilterBuilder WithAddress(Address address)
        {
            _address = new AddressFilter(address);

            return this;
        }

        public FilterBuilder WithAddresses(params Address[] addresses)
        {
            _address = new AddressFilter(addresses.ToHashSet());

            return this;
        }

        public FilterBuilder WithTopicExpressions(params TopicExpression[] expressions)
        {
            _topicsFilter = new SequenceTopicsFilter(expressions.ToArray());

            return this;
        }

        public LogFilter Build() => new LogFilter(_id, _fromBlock, _toBlock, _address, _topicsFilter);
    }
}
