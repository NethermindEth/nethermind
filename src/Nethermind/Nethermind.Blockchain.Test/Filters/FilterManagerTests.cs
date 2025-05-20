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

namespace Nethermind.Blockchain.Test.Filters;

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

    [TearDown]
    public void TearDown()
    {
        _filterStore.Dispose();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void removing_filter_removes_data()
    {
        LogsShouldNotBeEmpty(static _ => { }, static _ => { });
        _filterManager.GetLogs(0).Should().NotBeEmpty();
        _filterStore.FilterRemoved += Raise.EventWith(new FilterEventArgs(0));
        _filterManager.GetLogs(0).Should().BeEmpty();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_default_filter_parameters()
        => LogsShouldNotBeEmpty(static _ => { }, static _ => { });

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void many_logs_should_not_be_empty_for_default_filters_parameters()
        => LogsShouldNotBeEmpty([static _ => { }, static _ => { }, static _ => { }],
            [static _ => { }, static _ => { }, static _ => { }]);

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_from_block_earliest_type()
        => LogsShouldNotBeEmpty(static filter => filter.FromEarliestBlock(), static _ => { });

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_from_block_pending_type()
        => LogsShouldNotBeEmpty(static filter => filter.FromPendingBlock(), static _ => { });

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_from_block_latest_type()
        => LogsShouldNotBeEmpty(static filter => filter.FromLatestBlock(), static _ => { });

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_to_block_earliest_type()
        => LogsShouldNotBeEmpty(static filter => filter.ToEarliestBlock(), static _ => { });

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_to_block_pending_type()
        => LogsShouldNotBeEmpty(static filter => filter.ToPendingBlock(), static _ => { });

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_to_block_latest_type()
        => LogsShouldNotBeEmpty(static filter => filter.ToLatestBlock(), static _ => { });

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_from_block_number_in_range()
        => LogsShouldNotBeEmpty(static filter => filter.FromBlock(1L),
            static receipt => receipt.WithBlockNumber(2L));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void many_logs_should_not_be_empty_for_from_blocks_numbers_in_range()
        => LogsShouldNotBeEmpty(
            [
                static filter => filter.FromBlock(1L),
                static filter => filter.FromBlock(2L),
                static filter => filter.FromBlock(3L)
            ],
            [
                static receipt => receipt.WithBlockNumber(1L),
                static receipt => receipt.WithBlockNumber(5L),
                static receipt => receipt.WithBlockNumber(10L)
            ]);

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_from_block_number_not_in_range()
        => LogsShouldBeEmpty(static filter => filter.FromBlock(1L),
            static receipt => receipt.WithBlockNumber(0L));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_to_block_number_in_range()
        => LogsShouldNotBeEmpty(static filter => filter.ToBlock(2L),
            static receipt => receipt.WithBlockNumber(1L));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void many_logs_should_not_be_empty_for_to_blocks_numbers_in_range()
        => LogsShouldNotBeEmpty(
            [
                static filter => filter.ToBlock(1L),
                static filter => filter.ToBlock(5L),
                static filter => filter.ToBlock(10L)
            ],
            [
                static receipt => receipt.WithBlockNumber(1L),
                static receipt => receipt.WithBlockNumber(2L),
                static receipt => receipt.WithBlockNumber(3L)
            ]);

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_to_block_number_not_in_range()
        => LogsShouldBeEmpty(static filter => filter.ToBlock(1L),
            static receipt => receipt.WithBlockNumber(2L));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_from_block_number_in_range_and_to_block_number_in_range()
        => LogsShouldNotBeEmpty(static filter => filter.FromBlock(2L).ToBlock(6L),
            static receipt => receipt.WithBlockNumber(4L));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_from_block_number_in_range_and_to_block_number_not_in_range()
        => LogsShouldBeEmpty(static filter => filter.FromBlock(2L).ToBlock(3L),
            static receipt => receipt.WithBlockNumber(4L));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_from_block_number_not_in_range_and_to_block_number_in_range()
        => LogsShouldBeEmpty(static filter => filter.FromBlock(5L).ToBlock(7L),
            static receipt => receipt.WithBlockNumber(4L));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_from_block_number_not_in_range_and_to_block_number_not_in_range()
        => LogsShouldBeEmpty(static filter => filter.FromBlock(2L).ToBlock(3L),
            static receipt => receipt.WithBlockNumber(4L));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_existing_address()
        => LogsShouldNotBeEmpty(static filter => filter.WithAddress(TestItem.AddressA),
            static receipt => receipt.WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressA).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_non_existing_address()
        => LogsShouldBeEmpty(static filter => filter.WithAddress(TestItem.AddressA),
            static receipt => receipt
                .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressB).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_existing_addresses()
        => LogsShouldNotBeEmpty(static filter => filter.WithAddresses(TestItem.AddressA, TestItem.AddressB),
            static receipt => receipt
                .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressB).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_non_existing_addresses()
        => LogsShouldBeEmpty(static filter => filter.WithAddresses(TestItem.AddressA, TestItem.AddressB),
            static receipt => receipt
                .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressC).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_existing_specific_topic()
        => LogsShouldNotBeEmpty(static filter => filter
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA)),
            static receipt => receipt
                .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakA, TestItem.KeccakB).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_non_existing_specific_topic()
        => LogsShouldBeEmpty(static filter => filter
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA)),
            static receipt => receipt
                .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_existing_any_topic()
        => LogsShouldNotBeEmpty(static filter => filter
                .WithTopicExpressions(TestTopicExpressions.Any),
            static receipt => receipt
                .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakA, TestItem.KeccakB).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_existing_or_topic()
        => LogsShouldNotBeEmpty(static filter => filter
                .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakD))),
            static receipt => receipt
                .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_non_existing_or_topic()
        => LogsShouldBeEmpty(static filter => filter
                .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Specific(TestItem.KeccakD))),
            static receipt => receipt
                .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_existing_block_and_address_and_topics()
        => LogsShouldNotBeEmpty(static filter => filter
                .FromBlock(1L)
                .ToBlock(10L)
                .WithAddress(TestItem.AddressA)
                .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakD))),
            static receipt => receipt
                .WithBlockNumber(6L)
                .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressA)
                    .WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_existing_block_and_addresses_and_topics()
        => LogsShouldNotBeEmpty(static filter => filter
                .FromBlock(1L)
                .ToBlock(10L)
                .WithAddresses(TestItem.AddressA, TestItem.AddressB)
                .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakD))),
            static receipt => receipt
                .WithBlockNumber(6L)
                .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressA)
                    .WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_existing_block_and_addresses_and_non_existing_topic()
        => LogsShouldBeEmpty(static filter => filter
                .FromBlock(1L)
                .ToBlock(10L)
                .WithAddresses(TestItem.AddressA, TestItem.AddressB)
                .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakC), TestTopicExpressions.Specific(TestItem.KeccakD))),
            static receipt => receipt
                .WithBlockNumber(6L)
                .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressA)
                    .WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    [TestCase(1, 1)]
    [TestCase(5, 3)]
    public void logs_should_have_correct_log_indexes(int filtersCount, int logsPerTx)
    {
        const int txCount = 10;

        Assert(
            filterBuilder: (builder, _) => builder
                .FromBlock(1L)
                .ToBlock(10L)
                .WithAddresses(TestItem.AddressA, TestItem.AddressB)
                .WithTopicExpressions(TestTopicExpressions.Or(
                    TestTopicExpressions.Specific(TestItem.KeccakB),
                    TestTopicExpressions.Specific(TestItem.KeccakD)
                )),
            filterCount: filtersCount,
            receiptBuilder: (builder, _) => builder
                .WithBlockNumber(6L)
                .WithLogs(Enumerable.Range(0, logsPerTx).Select(_ =>
                    Build.A.LogEntry
                        .WithAddress(TestItem.AddressA)
                        .WithTopics(TestItem.KeccakB, TestItem.KeccakC).TestObject
                ).ToArray()),
            receiptCount: txCount,
            logsAssertion: logs => logs.Select(l => l.LogIndex).Should().BeEquivalentTo(Enumerable.Range(0, txCount * logsPerTx))
        );
    }


    private void LogsShouldNotBeEmpty(Action<FilterBuilder> filterBuilder, Action<ReceiptBuilder> receiptBuilder)
        => LogsShouldNotBeEmpty([filterBuilder], [receiptBuilder]);

    private void LogsShouldBeEmpty(Action<FilterBuilder> filterBuilder, Action<ReceiptBuilder> receiptBuilder)
        => LogsShouldBeEmpty([filterBuilder], [receiptBuilder]);

    private void LogsShouldNotBeEmpty(IEnumerable<Action<FilterBuilder>> filterBuilders, IEnumerable<Action<ReceiptBuilder>> receiptBuilders)
        => Assert(filterBuilders, receiptBuilders, static logs => logs.Should().NotBeEmpty());

    private void LogsShouldBeEmpty(IEnumerable<Action<FilterBuilder>> filterBuilders, IEnumerable<Action<ReceiptBuilder>> receiptBuilders)
        => Assert(filterBuilders, receiptBuilders, static logs => logs.Should().BeEmpty());

    private void Assert(Action<FilterBuilder, int> filterBuilder, int filterCount,
        Action<ReceiptBuilder, int> receiptBuilder, int receiptCount,
        Action<IEnumerable<FilterLog>> logsAssertion)
        => Assert(
            Enumerable.Range(0, filterCount).Select<int, Action<FilterBuilder>>(i => builder => filterBuilder(builder, i)),
            Enumerable.Range(0, receiptCount).Select<int, Action<ReceiptBuilder>>(i => builder => receiptBuilder(builder, i)),
            logsAssertion
        );

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

        _blockProcessor.BlockProcessed += Raise.EventWith(_blockProcessor, new BlockProcessedEventArgs(block, []));

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

            Hash256[] hashes = _filterManager.GetBlocksHashes(blockFilter.Id);
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
