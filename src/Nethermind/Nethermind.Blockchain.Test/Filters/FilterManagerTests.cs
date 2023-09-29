// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Filters;
using Nethermind.Logging;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Filters
{
    public class FilterManagerTests
    {
        private IFilterStore _filterStore = null!;
        private IBlockProcessor _blockProcessor = null!;
        private ITxPool _txPool = null!;
        private ILogManager _logManager = null!;
        private FilterManager _filterManager = null!;

        private int _currentFilterId;

        [SetUp]
        public void Setup()
        {
            _currentFilterId = 0;
            _filterStore = Substitute.For<IFilterStore>();
            _blockProcessor = Substitute.For<IBlockProcessor>();
            _txPool = Substitute.For<ITxPool>();
            _logManager = LimboLogs.Instance;
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_default_filter_parameters()
            => LogsShouldNotBeEmpty(_ => { }, _ => { });

        [Test, Timeout(Timeout.MaxTestTime)]
        public void many_logs_should_not_be_empty_for_default_filters_parameters()
            => LogsShouldNotBeEmpty(new Action<FilterBuilder>[] { _ => { }, _ => { }, _ => { } },
                new Action<ReceiptBuilder>[] { _ => { }, _ => { }, _ => { } });

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_from_block_earliest_type()
            => LogsShouldNotBeEmpty(filter => filter.FromEarliestBlock(), _ => { });

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_from_block_pending_type()
            => LogsShouldNotBeEmpty(filter => filter.FromPendingBlock(), _ => { });

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_from_block_latest_type()
            => LogsShouldNotBeEmpty(filter => filter.FromLatestBlock(), _ => { });

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_to_block_earliest_type()
            => LogsShouldNotBeEmpty(filter => filter.ToEarliestBlock(), _ => { });

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_to_block_pending_type()
            => LogsShouldNotBeEmpty(filter => filter.ToPendingBlock(), _ => { });

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_to_block_latest_type()
            => LogsShouldNotBeEmpty(filter => filter.ToLatestBlock(), _ => { });

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_from_block_number_in_range()
            => LogsShouldNotBeEmpty(filter => filter.FromBlock(1L),
                receipt => receipt.WithBlockNumber(2L));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void many_logs_should_not_be_empty_for_from_blocks_numbers_in_range()
            => LogsShouldNotBeEmpty(
                new Action<FilterBuilder>[]
                {
                    filter => filter.FromBlock(1L),
                    filter => filter.FromBlock(2L),
                    filter => filter.FromBlock(3L)
                },
                new Action<ReceiptBuilder>[]
                {
                    receipt => receipt.WithBlockNumber(1L),
                    receipt => receipt.WithBlockNumber(5L),
                    receipt => receipt.WithBlockNumber(10L)
                });

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_be_empty_for_from_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter.FromBlock(1L),
                receipt => receipt.WithBlockNumber(0L));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_to_block_number_in_range()
            => LogsShouldNotBeEmpty(filter => filter.ToBlock(2L),
                receipt => receipt.WithBlockNumber(1L));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void many_logs_should_not_be_empty_for_to_blocks_numbers_in_range()
            => LogsShouldNotBeEmpty(
                new Action<FilterBuilder>[]
                {
                    filter => filter.ToBlock(1L),
                    filter => filter.ToBlock(5L),
                    filter => filter.ToBlock(10L)
                },
                new Action<ReceiptBuilder>[]
                {
                    receipt => receipt.WithBlockNumber(1L),
                    receipt => receipt.WithBlockNumber(2L),
                    receipt => receipt.WithBlockNumber(3L)
                });

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_be_empty_for_to_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter.ToBlock(1L),
                receipt => receipt.WithBlockNumber(2L));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_from_block_number_in_range_and_to_block_number_in_range()
            => LogsShouldNotBeEmpty(filter => filter.FromBlock(2L).ToBlock(6L),
                receipt => receipt.WithBlockNumber(4L));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_be_empty_for_from_block_number_in_range_and_to_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter.FromBlock(2L).ToBlock(3L),
                receipt => receipt.WithBlockNumber(4L));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_be_empty_for_from_block_number_not_in_range_and_to_block_number_in_range()
            => LogsShouldBeEmpty(filter => filter.FromBlock(5L).ToBlock(7L),
                receipt => receipt.WithBlockNumber(4L));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_be_empty_for_from_block_number_not_in_range_and_to_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter.FromBlock(2L).ToBlock(3L),
                receipt => receipt.WithBlockNumber(4L));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_existing_address()
            => LogsShouldNotBeEmpty(filter => filter.WithAddress(TestItem.AddressA),
                receipt => receipt.WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressA).TestObject));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_be_empty_for_non_existing_address()
            => LogsShouldBeEmpty(filter => filter.WithAddress(TestItem.AddressA),
                receipt => receipt
                    .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressB).TestObject));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_existing_addresses()
            => LogsShouldNotBeEmpty(filter => filter.WithAddresses(TestItem.AddressA, TestItem.AddressB),
                receipt => receipt
                    .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressB).TestObject));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_be_empty_for_non_existing_addresses()
            => LogsShouldBeEmpty(filter => filter.WithAddresses(TestItem.AddressA, TestItem.AddressB),
                receipt => receipt
                    .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressC).TestObject));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_existing_specific_topic()
            => LogsShouldNotBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA)),
                receipt => receipt
                    .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakA, TestItem.KeccakB).TestObject));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_be_empty_for_non_existing_specific_topic()
            => LogsShouldBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA)),
                receipt => receipt
                    .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_existing_any_topic()
            => LogsShouldNotBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Any),
                receipt => receipt
                    .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakA, TestItem.KeccakB).TestObject));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_existing_or_topic()
            => LogsShouldNotBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakD))),
                receipt => receipt
                    .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_be_empty_for_non_existing_or_topic()
            => LogsShouldBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Specific(TestItem.KeccakD))),
                receipt => receipt
                    .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_existing_block_and_address_and_topics()
            => LogsShouldNotBeEmpty(filter => filter
                    .FromBlock(1L)
                    .ToBlock(10L)
                    .WithAddress(TestItem.AddressA)
                    .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakD))),
                receipt => receipt
                    .WithBlockNumber(6L)
                    .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressA)
                        .WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_not_be_empty_for_existing_block_and_addresses_and_topics()
            => LogsShouldNotBeEmpty(filter => filter
                    .FromBlock(1L)
                    .ToBlock(10L)
                    .WithAddresses(TestItem.AddressA, TestItem.AddressB)
                    .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakD))),
                receipt => receipt
                    .WithBlockNumber(6L)
                    .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressA)
                        .WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject));

        [Test, Timeout(Timeout.MaxTestTime)]
        public void logs_should_be_empty_for_existing_block_and_addresses_and_non_existing_topic()
            => LogsShouldBeEmpty(filter => filter
                    .FromBlock(1L)
                    .ToBlock(10L)
                    .WithAddresses(TestItem.AddressA, TestItem.AddressB)
                    .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakC), TestTopicExpressions.Specific(TestItem.KeccakD))),
                receipt => receipt
                    .WithBlockNumber(6L)
                    .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressA)
                        .WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject));


        private void LogsShouldNotBeEmpty(Action<FilterBuilder> filterBuilder,
            Action<ReceiptBuilder> receiptBuilder)
            => LogsShouldNotBeEmpty(new[] { filterBuilder }, new[] { receiptBuilder });

        private void LogsShouldBeEmpty(Action<FilterBuilder> filterBuilder,
            Action<ReceiptBuilder> receiptBuilder)
            => LogsShouldBeEmpty(new[] { filterBuilder }, new[] { receiptBuilder });

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
            List<FilterBase> filters = new List<FilterBase>();
            List<TxReceipt> receipts = new List<TxReceipt>();
            foreach (Action<FilterBuilder> filterBuilder in filterBuilders)
            {
                filters.Add(BuildFilter(filterBuilder));
            }

            foreach (Action<ReceiptBuilder> receiptBuilder in receiptBuilders)
            {
                receipts.Add(BuildReceipt(receiptBuilder));
            }

            // adding always a simple block filter and test
            Block block = Build.A.Block.TestObject;
            BlockFilter blockFilter = new(_currentFilterId++, 0);
            filters.Add(blockFilter);

            _filterStore.GetFilters<LogFilter>().Returns(filters.OfType<LogFilter>().ToArray());
            _filterStore.GetFilters<BlockFilter>().Returns(filters.OfType<BlockFilter>().ToArray());
            _filterManager = new FilterManager(_filterStore, _blockProcessor, _txPool, _logManager);

            _blockProcessor.BlockProcessed += Raise.EventWith(_blockProcessor, new BlockProcessedEventArgs(block, Array.Empty<TxReceipt>()));

            int index = 1;
            foreach (TxReceipt receipt in receipts)
            {
                _blockProcessor.TransactionProcessed += Raise.EventWith(_blockProcessor,
                    new TxProcessedEventArgs(index, Build.A.Transaction.TestObject, receipt));
                index++;
            }

            NUnit.Framework.Assert.Multiple(() =>
            {
                foreach (LogFilter filter in filters.OfType<LogFilter>())
                {
                    FilterLog[] logs = _filterManager.GetLogs(filter.Id);
                    logsAssertion(logs);
                }

                Keccak[] hashes = _filterManager.GetBlocksHashes(blockFilter.Id);
                NUnit.Framework.Assert.That(hashes.Length, Is.EqualTo(1));
            });
        }

        private LogFilter BuildFilter(Action<FilterBuilder> builder)
        {
            FilterBuilder builderInstance = FilterBuilder.New(ref _currentFilterId);
            builder(builderInstance);

            return builderInstance.Build();
        }

        private static TxReceipt BuildReceipt(Action<ReceiptBuilder> builder)
        {
            ReceiptBuilder builderInstance = new();
            builder(builderInstance);

            return builderInstance.TestObject;
        }
    }
}
