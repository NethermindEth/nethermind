/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Filters
{
    public class FilterManagerTests
    {
        private IFilterStore _filterStore;
        private IBlockProcessor _blockProcessor;
        private FilterManager _filterManager;
        private ILogManager _logManager;
        private int _currentFilterId;

        [SetUp]
        public void Setup()
        {
            _currentFilterId = 0;
            _filterStore = Substitute.For<IFilterStore>();
            _blockProcessor = Substitute.For<IBlockProcessor>();
            _logManager = Substitute.For<ILogManager>();
        }

        [Test]
        public void logs_should_not_be_empty_for_default_filter_parameters()
            => LogsShouldNotBeEmpty(filter => { }, receipt => { });

        [Test]
        public void many_logs_should_not_be_empty_for_default_filters_parameters()
            => LogsShouldNotBeEmpty(new Action<FilterBuilder>[] {filter => { }, filter => { }, filter => { }},
                new Action<ReceiptBuilder>[] {receipt => { }, receipt => { }, receipt => { }});

        [Test]
        public void logs_should_not_be_empty_for_from_block_earliest_type()
            => LogsShouldNotBeEmpty(filter => filter.FromEarliestBlock(), receipt => { });

        [Test]
        public void logs_should_not_be_empty_for_from_block_pending_type()
            => LogsShouldNotBeEmpty(filter => filter.FromPendingBlock(), receipt => { });

        [Test]
        public void logs_should_not_be_empty_for_from_block_latest_type()
            => LogsShouldNotBeEmpty(filter => filter.FromLatestBlock(), receipt => { });

        [Test]
        public void logs_should_not_be_empty_for_to_block_earliest_type()
            => LogsShouldNotBeEmpty(filter => filter.ToEarliestBlock(), receipt => { });

        [Test]
        public void logs_should_not_be_empty_for_to_block_pending_type()
            => LogsShouldNotBeEmpty(filter => filter.ToPendingBlock(), receipt => { });

        [Test]
        public void logs_should_not_be_empty_for_to_block_latest_type()
            => LogsShouldNotBeEmpty(filter => filter.ToLatestBlock(), receipt => { });

        [Test]
        public void logs_should_not_be_empty_for_from_block_number_in_range()
            => LogsShouldNotBeEmpty(filter => filter.FromBlock(UInt256.One),
                receipt => receipt.WithBlockNumber(new UInt256(2)));

        [Test]
        public void many_logs_should_not_be_empty_for_from_blocks_numbers_in_range()
            => LogsShouldNotBeEmpty(
                new Action<FilterBuilder>[]
                {
                    filter => filter.FromBlock(UInt256.One),
                    filter => filter.FromBlock(new UInt256(2)),
                    filter => filter.FromBlock(new UInt256(3))
                },
                new Action<ReceiptBuilder>[]
                {
                    receipt => receipt.WithBlockNumber(new UInt256(1)),
                    receipt => receipt.WithBlockNumber(new UInt256(5)),
                    receipt => receipt.WithBlockNumber(new UInt256(10))
                });

        [Test]
        public void logs_should_be_empty_for_from_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter.FromBlock(UInt256.One),
                receipt => receipt.WithBlockNumber(UInt256.Zero));

        [Test]
        public void logs_should_not_be_empty_for_to_block_number_in_range()
            => LogsShouldNotBeEmpty(filter => filter.ToBlock(new UInt256(2)),
                receipt => receipt.WithBlockNumber(UInt256.One));

        [Test]
        public void many_logs_should_not_be_empty_for_to_blocks_numbers_in_range()
            => LogsShouldNotBeEmpty(
                new Action<FilterBuilder>[]
                {
                    filter => filter.ToBlock(UInt256.One),
                    filter => filter.ToBlock(new UInt256(5)),
                    filter => filter.ToBlock(new UInt256(10))
                },
                new Action<ReceiptBuilder>[]
                {
                    receipt => receipt.WithBlockNumber(new UInt256(1)),
                    receipt => receipt.WithBlockNumber(new UInt256(2)),
                    receipt => receipt.WithBlockNumber(new UInt256(3))
                });

        [Test]
        public void logs_should_be_empty_for_to_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter.ToBlock(UInt256.One),
                receipt => receipt.WithBlockNumber(new UInt256(2)));

        [Test]
        public void logs_should_not_be_empty_for_from_block_number_in_range_and_to_block_number_in_range()
            => LogsShouldNotBeEmpty(filter => filter.FromBlock(new UInt256(2)).ToBlock(new UInt256(6)),
                receipt => receipt.WithBlockNumber(new UInt256(4)));

        [Test]
        public void logs_should_be_empty_for_from_block_number_in_range_and_to_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter.FromBlock(new UInt256(2)).ToBlock(new UInt256(3)),
                receipt => receipt.WithBlockNumber(new UInt256(4)));

        [Test]
        public void logs_should_be_empty_for_from_block_number_not_in_range_and_to_block_number_in_range()
            => LogsShouldBeEmpty(filter => filter.FromBlock(new UInt256(5)).ToBlock(new UInt256(7)),
                receipt => receipt.WithBlockNumber(new UInt256(4)));

        [Test]
        public void logs_should_be_empty_for_from_block_number_not_in_range_and_to_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter.FromBlock(new UInt256(2)).ToBlock(new UInt256(3)),
                receipt => receipt.WithBlockNumber(new UInt256(4)));

        [Test]
        public void logs_should_not_be_empty_for_existing_address()
            => LogsShouldNotBeEmpty(filter => filter.WithAddress(TestObject.AddressA),
                receipt => receipt.WithLogs(new[] {Build.A.LogEntry.WithAddress(TestObject.AddressA).TestObject}));

        [Test]
        public void logs_should_be_empty_for_non_existing_address()
            => LogsShouldBeEmpty(filter => filter.WithAddress(TestObject.AddressA),
                receipt => receipt
                    .WithLogs(new[] {Build.A.LogEntry.WithAddress(TestObject.AddressB).TestObject}));

        [Test]
        public void logs_should_not_be_empty_for_existing_addresses()
            => LogsShouldNotBeEmpty(filter => filter.WithAddresses(new[] {TestObject.AddressA, TestObject.AddressB}),
                receipt => receipt
                    .WithLogs(new[] {Build.A.LogEntry.WithAddress(TestObject.AddressB).TestObject}));

        [Test]
        public void logs_should_be_empty_for_non_existing_addresses()
            => LogsShouldBeEmpty(filter => filter.WithAddresses(new[] {TestObject.AddressA, TestObject.AddressB}),
                receipt => receipt
                    .WithLogs(new[] {Build.A.LogEntry.WithAddress(TestObject.AddressC).TestObject}));

        [Test]
        public void logs_should_not_be_empty_for_existing_specific_topic()
            => LogsShouldNotBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Specific(TestObject.KeccakA)),
                receipt => receipt
                    .WithLogs(new[]
                        {Build.A.LogEntry.WithTopics(new[] {TestObject.KeccakA, TestObject.KeccakB}).TestObject}));

        [Test]
        public void logs_should_be_empty_for_non_existing_specific_topic()
            => LogsShouldBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Specific(TestObject.KeccakA)),
                receipt => receipt
                    .WithLogs(new[]
                        {Build.A.LogEntry.WithTopics(new[] {TestObject.KeccakB, TestObject.KeccakC}).TestObject}));

        [Test]
        public void logs_should_not_be_empty_for_existing_any_topic()
            => LogsShouldNotBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Any),
                receipt => receipt
                    .WithLogs(new[]
                        {Build.A.LogEntry.WithTopics(new[] {TestObject.KeccakA, TestObject.KeccakB}).TestObject}));

        [Test]
        public void logs_should_not_be_empty_for_existing_or_topic()
            => LogsShouldNotBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Or(new[]
                    {
                        TestTopicExpressions.Specific(TestObject.KeccakB),
                        TestTopicExpressions.Specific(TestObject.KeccakD)
                    })),
                receipt => receipt
                    .WithLogs(new[]
                        {Build.A.LogEntry.WithTopics(new[] {TestObject.KeccakB, TestObject.KeccakC}).TestObject}));

        [Test]
        public void logs_should_be_empty_for_non_existing_or_topic()
            => LogsShouldBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Or(new[]
                    {
                        TestTopicExpressions.Specific(TestObject.KeccakA),
                        TestTopicExpressions.Specific(TestObject.KeccakD)
                    })),
                receipt => receipt
                    .WithLogs(new[]
                        {Build.A.LogEntry.WithTopics(new[] {TestObject.KeccakB, TestObject.KeccakC}).TestObject}));

        [Test]
        public void logs_should_not_be_empty_for_existing_block_and_address_and_topics()
            => LogsShouldNotBeEmpty(filter => filter
                    .FromBlock(UInt256.One)
                    .ToBlock(new UInt256(10))
                    .WithAddress(TestObject.AddressA)
                    .WithTopicExpressions(TestTopicExpressions.Or(new[]
                    {
                        TestTopicExpressions.Specific(TestObject.KeccakB),
                        TestTopicExpressions.Specific(TestObject.KeccakD)
                    })),
                receipt => receipt
                    .WithBlockNumber(new UInt256(6))
                    .WithLogs(new[]
                    {
                        Build.A.LogEntry.WithAddress(TestObject.AddressA)
                            .WithTopics(new[] {TestObject.KeccakB, TestObject.KeccakC}).TestObject
                    }));

        [Test]
        public void logs_should_not_be_empty_for_existing_block_and_addresses_and_topics()
            => LogsShouldNotBeEmpty(filter => filter
                    .FromBlock(UInt256.One)
                    .ToBlock(new UInt256(10))
                    .WithAddresses(new[] {TestObject.AddressA, TestObject.AddressB})
                    .WithTopicExpressions(TestTopicExpressions.Or(new[]
                    {
                        TestTopicExpressions.Specific(TestObject.KeccakB),
                        TestTopicExpressions.Specific(TestObject.KeccakD)
                    })),
                receipt => receipt
                    .WithBlockNumber(new UInt256(6))
                    .WithLogs(new[]
                    {
                        Build.A.LogEntry.WithAddress(TestObject.AddressA)
                            .WithTopics(new[] {TestObject.KeccakB, TestObject.KeccakC}).TestObject
                    }));

        [Test]
        public void logs_should_be_empty_for_existing_block_and_addresses_and_non_existing_topic()
            => LogsShouldBeEmpty(filter => filter
                    .FromBlock(UInt256.One)
                    .ToBlock(new UInt256(10))
                    .WithAddresses(new[] {TestObject.AddressA, TestObject.AddressB})
                    .WithTopicExpressions(TestTopicExpressions.Or(new[]
                    {
                        TestTopicExpressions.Specific(TestObject.KeccakC),
                        TestTopicExpressions.Specific(TestObject.KeccakD)
                    })),
                receipt => receipt
                    .WithBlockNumber(new UInt256(6))
                    .WithLogs(new[]
                    {
                        Build.A.LogEntry.WithAddress(TestObject.AddressA)
                            .WithTopics(new[] {TestObject.KeccakB, TestObject.KeccakC}).TestObject
                    }));


        private void LogsShouldNotBeEmpty(Action<FilterBuilder> filterBuilder,
            Action<ReceiptBuilder> receiptBuilder)
            => LogsShouldNotBeEmpty(new[] {filterBuilder}, new[] {receiptBuilder});

        private void LogsShouldBeEmpty(Action<FilterBuilder> filterBuilder,
            Action<ReceiptBuilder> receiptBuilder)
            => LogsShouldBeEmpty(new[] {filterBuilder}, new[] {receiptBuilder});

        private void LogsShouldNotBeEmpty(IEnumerable<Action<FilterBuilder>> filterBuilders,
            IEnumerable<Action<ReceiptBuilder>> receiptBuilders)
            => Assert(filterBuilders, receiptBuilders, logs => logs.Should().NotBeEmpty());

        private void LogsShouldBeEmpty(IEnumerable<Action<FilterBuilder>> filterBuilders,
            IEnumerable<Action<ReceiptBuilder>> receiptBuilders)
            => Assert(filterBuilders, receiptBuilders, logs => logs.Should().BeEmpty());

        private void Assert(IEnumerable<Action<FilterBuilder>> filterBuilders,
            IEnumerable<Action<ReceiptBuilder>> receiptBuilders,
            Action<IEnumerable<FilterLog>> logsAssertion)
        {
            var filters = new List<FilterBase>();
            var receipts = new List<TransactionReceipt>();
            foreach (var filterBuilder in filterBuilders)
            {
                filters.Add(BuildFilter(filterBuilder));
            }

            foreach (var receiptBuilder in receiptBuilders)
            {
                receipts.Add(BuildReceipt(receiptBuilder));
            }

            // adding always a simple block filter and test
            Block block = Build.A.Block.TestObject;
            BlockFilter blockFilter = new BlockFilter(_currentFilterId++, 0);
            filters.Add(blockFilter);

            _filterStore.GetFilters<LogFilter>().Returns(filters.OfType<LogFilter>().ToArray());
            _filterStore.GetFilters<BlockFilter>().Returns(filters.OfType<BlockFilter>().ToArray());
            _filterManager = new FilterManager(_filterStore, _blockProcessor, _logManager);

            _blockProcessor.BlockProcessed += Raise.EventWith(_blockProcessor, new BlockProcessedEventArgs(block));

            foreach (var receipt in receipts)
            {
                _blockProcessor.TransactionProcessed +=
                    Raise.EventWith(_blockProcessor, new TransactionProcessedEventArgs(receipt));
            }

            NUnit.Framework.Assert.Multiple(() =>
            {
                foreach (var filter in filters.OfType<LogFilter>())
                {
                    var logs = _filterManager.GetLogs(filter.Id);
                    logsAssertion(logs);
                }

                var hashes = _filterManager.GetBlocksHashes(blockFilter.Id);
                NUnit.Framework.Assert.AreEqual(1, hashes.Length);
            });
        }

        private LogFilter BuildFilter(Action<FilterBuilder> builder)
        {
            var builderInstance = FilterBuilder.New(ref _currentFilterId);
            builder(builderInstance);

            return builderInstance.Build();
        }

        private static TransactionReceipt BuildReceipt(Action<ReceiptBuilder> builder)
        {
            var builderInstance = new ReceiptBuilder();
            builder(builderInstance);

            return builderInstance.TestObject;
        }
    }
}