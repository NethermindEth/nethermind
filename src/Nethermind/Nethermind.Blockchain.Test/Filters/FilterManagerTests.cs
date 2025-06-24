// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
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
    private IBlockFinder _blockFinder = null!;
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
        _blockFinder = Substitute.For<IBlockFinder>();
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
    public void logs_should_not_be_empty_for_earliest_filter_parameters()
        => LogsShouldNotBeEmpty(
            static filterBuilder => filterBuilder.FromEarliestBlock().ToEarliestBlock(),
            static _ => { });

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_latest_filter_parameters()
        => LogsShouldNotBeEmpty(
            static filterBuilder => filterBuilder.FromLatestBlock().ToLatestBlock(),
            static _ => { });

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_from_block_number_higher_than_to_block_number()
        => LogsShouldBeEmpty(
            static filterBuilder => filterBuilder.FromBlock(10).ToBlock(5),
            static _ => { });

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_nonexisting_from_block_number()
        => LogsShouldBeEmpty(
            static filterBuilder => filterBuilder.FromFutureBlock(),
            static _ => { });

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_nonexisting_to_block_number()
        => LogsShouldBeEmpty(
            static filterBuilder => filterBuilder.ToBlock(1000000),
            static _ => { });

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_block_number_filter_with_receipt_block_number_too_low()
        => LogsShouldBeEmpty(
            static filterBuilder => filterBuilder.FromBlock(10).ToBlock(10),
            static receiptBuilder => receiptBuilder.WithBlockNumber(5));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_block_number_filter_with_receipt_block_number_too_high()
        => LogsShouldBeEmpty(
            static filterBuilder => filterBuilder.FromBlock(10).ToBlock(10),
            static receiptBuilder => receiptBuilder.WithBlockNumber(15));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_block_number_filter_with_receipt_block_number_in_range()
        => LogsShouldNotBeEmpty(
            static filterBuilder => filterBuilder.FromBlock(10).ToBlock(10),
            static receiptBuilder => receiptBuilder.WithBlockNumber(10));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_not_matching_address_filter()
        => LogsShouldBeEmpty(
            static filterBuilder => filterBuilder.FromBlock(10).ToBlock(10).WithAddress(TestItem.AddressA),
            static receiptBuilder => receiptBuilder.WithBlockNumber(10)
                .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressB).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_matching_address_filter()
        => LogsShouldNotBeEmpty(
            static filterBuilder => filterBuilder.FromBlock(10).ToBlock(10).WithAddress(TestItem.AddressA),
            static receiptBuilder => receiptBuilder.WithBlockNumber(10)
                .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressA).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_not_matching_topic_filter()
        => LogsShouldBeEmpty(
            static filterBuilder => filterBuilder.FromBlock(10).ToBlock(10)
                .WithTopicExpressions(TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB)),
            static receiptBuilder => receiptBuilder.WithBlockNumber(10)
                .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakC).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_matching_topic_filter()
        => LogsShouldNotBeEmpty(
            static filterBuilder => filterBuilder.FromBlock(10).ToBlock(10)
                .WithTopicExpressions(TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB)),
            static receiptBuilder => receiptBuilder.WithBlockNumber(10)
                .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakA).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_any_address_filter()
        => LogsShouldNotBeEmpty(
            static filterBuilder => filterBuilder.FromBlock(10).ToBlock(10).WithAnyAddress(),
            static receiptBuilder => receiptBuilder.WithBlockNumber(10)
                .WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressB).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_any_topics_filter()
        => LogsShouldNotBeEmpty(
            static filterBuilder => filterBuilder.FromBlock(10).ToBlock(10),
            static receiptBuilder => receiptBuilder.WithBlockNumber(10)
                .WithLogs(Build.A.LogEntry.WithTopics(TestItem.KeccakC).TestObject));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_latest_filter_when_receipt_not_from_latest_block()
    {
        // Setup: latest block is number 15
        Block latestBlock = Build.A.Block.WithNumber(15).TestObject;
        _blockFinder.FindLatestBlock().Returns(latestBlock);

        LogsShouldBeEmpty(
            static filterBuilder => filterBuilder.FromLatestBlock().ToLatestBlock(),
            static receiptBuilder => receiptBuilder.WithBlockNumber(10) // Receipt from block 10, not 15
                .WithLogs(Build.A.LogEntry.TestObject));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_not_be_empty_for_latest_filter_when_receipt_from_latest_block()
    {
        // Setup: latest block is number 15
        Block latestBlock = Build.A.Block.WithNumber(15).TestObject;
        _blockFinder.FindLatestBlock().Returns(latestBlock);

        LogsShouldNotBeEmpty(
            static filterBuilder => filterBuilder.FromLatestBlock().ToLatestBlock(),
            static receiptBuilder => receiptBuilder.WithBlockNumber(15) // Receipt from latest block
                .WithLogs(Build.A.LogEntry.TestObject));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void logs_should_be_empty_for_latest_filter_when_latest_block_is_null()
    {
        // Setup: no latest block available
        _blockFinder.FindLatestBlock().Returns((Block?)null);

        LogsShouldBeEmpty(
            static filterBuilder => filterBuilder.FromLatestBlock().ToLatestBlock(),
            static receiptBuilder => receiptBuilder.WithBlockNumber(10)
                .WithLogs(Build.A.LogEntry.TestObject));
    }

    private void LogsShouldBeEmpty(Action<FilterBuilder> filterBuilder, Action<ReceiptBuilder> receiptBuilder)
        => LogsAssertions(
            filterBuilder,
            receiptBuilder,
            static logs => NUnit.Framework.Assert.That(logs.Length, Is.EqualTo(0)));

    private void LogsShouldNotBeEmpty(Action<FilterBuilder> filterBuilder, Action<ReceiptBuilder> receiptBuilder)
        => LogsAssertions(
            filterBuilder,
            receiptBuilder,
            static logs => NUnit.Framework.Assert.That(logs.Length, Is.GreaterThan(0)));

    private void LogsAssertions(
        Action<FilterBuilder> filterBuilder,
        Action<ReceiptBuilder> receiptBuilder,
        Action<FilterLog[]> logsAssertion) =>
        LogsAssertions(new[] { filterBuilder }, new[] { receiptBuilder }, logsAssertion);

    private void LogsAssertions(
        Action<FilterBuilder>[] filterBuilders,
        Action<ReceiptBuilder>[] receiptBuilders,
        Action<FilterLog[]> logsAssertion)
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
        _filterManager = new FilterManager(_filterStore, _blockProcessor, _txPool, _logManager, _blockFinder);

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

    private TxReceipt BuildReceipt(Action<ReceiptBuilder> builder)
    {
        ReceiptBuilder receiptBuilder = new ReceiptBuilder();
        builder(receiptBuilder);

        return receiptBuilder.TestObject;
    }
}
