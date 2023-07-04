// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private BlockParameter _fromBlock = new(BlockParameterType.Latest);
        private BlockParameter _toBlock = new(BlockParameterType.Latest);
        private AddressFilter _address = new((Address)null);
        private SequenceTopicsFilter _topicsFilter = new();

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

        public LogFilter Build() => new(_id, _fromBlock, _toBlock, _address, _topicsFilter);
    }
}
