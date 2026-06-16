// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Find;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Find;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class LogFinderTests
{
    private IBlockTree _blockTree = null!;
    private BlockTree _rawBlockTree = null!;
    private IReceiptStorage _receiptStorage = null!;
    private LogFinder _logFinder = null!;
    private IReceiptsRecovery _receiptsRecovery = null!;
    private Block _headTestBlock = null!;
    private ISpecProvider? _specProvider;

    [SetUp]
    public void SetUp() => SetUp(true);

    private void SetUp(bool allowReceiptIterator, int chainLength = 5)
    {
        _specProvider = Substitute.For<ISpecProvider>();
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).IsEip155Enabled.Returns(true);
        _receiptStorage = new InMemoryReceiptStorage(allowReceiptIterator);
        _rawBlockTree = Build.A.BlockTree()
            .WithTransactions(_receiptStorage, LogsForBlockBuilder)
            .OfChainLength(out _headTestBlock, chainLength)
            .TestObject;
        _blockTree = _rawBlockTree;
        _receiptsRecovery = Substitute.For<IReceiptsRecovery>();
        _logFinder = CreateLogFinder();
    }

    private void SetupHeadWithNoTransaction()
    {
        Block blockWithNoTransaction = Build.A.Block
            .WithParent(_headTestBlock)
            .TestObject;
        Assert.That(_rawBlockTree.SuggestBlock(blockWithNoTransaction), Is.EqualTo(AddBlockResult.Added));
        _rawBlockTree.TryUpdateMainChain(blockWithNoTransaction.Header, true, preloadedBlocks: new[] { blockWithNoTransaction });
    }

    private IEnumerable<LogEntry> LogsForBlockBuilder(Block block, Transaction transaction)
    {
        if (block.Number == 1)
        {
            if (transaction.Value == 1)
            {
                yield return Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).TestObject;
            }
            else if (transaction.Value == 2)
            {
                yield return Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA, TestItem.KeccakB).TestObject;
            }
        }
        else if (block.Number == 4)
        {
            if (transaction.Value == 1)
            {
                yield return Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakA, TestItem.KeccakB).TestObject;
            }
            else if (transaction.Value == 2)
            {
                yield return Build.A.LogEntry.WithAddress(TestItem.AddressC).WithTopics(TestItem.KeccakB, TestItem.KeccakA, TestItem.KeccakE).TestObject;
                yield return Build.A.LogEntry.WithAddress(TestItem.AddressD).WithTopics(TestItem.KeccakD, TestItem.KeccakA).TestObject;
            }
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void filter_all_logs([Values(false, true)] bool allowReceiptIterator)
    {
        SetUp(allowReceiptIterator);
        LogFilter logFilter = AllBlockFilter().Build();
        FilterLog[] logs = _logFinder.FindLogs(logFilter).ToArray();
        Assert.That(logs.Length, Is.EqualTo(5));
        int[] indexes = logs.Select(static l => (int)l.LogIndex).ToArray();
        Assert.That(indexes, Is.EqualTo(new[] { 0, 1, 0, 1, 2 }));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void filter_all_logs_iteratively([Values(false, true)] bool allowReceiptIterator)
    {
        SetUp(allowReceiptIterator);
        LogFilter logFilter = AllBlockFilter().Build();
        FilterLog[] logs = _logFinder.FindLogs(logFilter).ToArray();
        int[] indexes = logs.Select(static l => (int)l.LogIndex).ToArray();
        Assert.That(indexes, Is.EqualTo([0, 1, 0, 1, 2]));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void throw_exception_when_receipts_are_missing()
    {
        _receiptStorage = NullReceiptStorage.Instance;
        _logFinder = CreateLogFinder();

        LogFilter logFilter = AllBlockFilter().Build();

        Assert.That(() => _logFinder.FindLogs(logFilter), Throws.TypeOf<ResourceNotFoundException>());
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void when_receipts_are_missing_and_header_has_no_receipt_root_do_not_throw_exception_()
    {
        _receiptStorage = NullReceiptStorage.Instance;
        _logFinder = CreateLogFinder();

        SetupHeadWithNoTransaction();

        LogFilter logFilter = AllBlockFilter().Build();

        Assert.That(() => _logFinder.FindLogs(logFilter), Throws.Nothing);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void filter_all_logs_should_throw_when_to_block_is_not_found()
    {
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        _logFinder = CreateLogFinder(blockFinder);
        LogFilter logFilter = AllBlockFilter().Build();
        Func<IEnumerable<FilterLog>> action = new(() => _logFinder.FindLogs(logFilter));
        Assert.That(action, Throws.TypeOf<ResourceNotFoundException>());
        blockFinder.Received().FindHeader(logFilter.ToBlock, false);
        blockFinder.DidNotReceive().FindHeader(logFilter.FromBlock);
    }

    public static IEnumerable FilterByAddressTestsData
    {
        get
        {
            yield return new TestCaseData(new[] { TestItem.AddressA }, 2).SetName("filter_by_address_A");
            yield return new TestCaseData(new[] { TestItem.AddressB }, 1).SetName("filter_by_address_B");
            yield return new TestCaseData(new[] { TestItem.AddressC }, 1).SetName("filter_by_address_C");
            yield return new TestCaseData(new[] { TestItem.AddressD }, 1).SetName("filter_by_address_D");
            yield return new TestCaseData(new[] { TestItem.AddressA, TestItem.AddressC, TestItem.AddressD }, 4).SetName("filter_by_addresses_A_C_D");
        }
    }

    [TestCaseSource(nameof(FilterByAddressTestsData))]
    public void filter_by_address(Address[] addresses, int expectedCount)
    {
        FilterBuilder filterBuilder = AllBlockFilter();
        filterBuilder = addresses.Length == 1 ? filterBuilder.WithAddress(addresses[0]) : filterBuilder.WithAddresses(addresses);
        LogFilter logFilter = filterBuilder.Build();

        FilterLog[] logs = _logFinder.FindLogs(logFilter).ToArray();

        Assert.That(logs.Length, Is.EqualTo(expectedCount));
    }

    public static IEnumerable FilterByTopicsTestsData
    {
        get
        {
            yield return new TestCaseData(new[] { TestTopicExpressions.Specific(TestItem.KeccakA) }, new long[] { 1, 1, 4 }).SetName("filter_by_topic_A");
            yield return new TestCaseData(new[] { TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakB) }, new long[] { 1, 4 }).SetName("filter_by_any_then_topic_B");
            yield return new TestCaseData(new[] { TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Any }, new long[] { 4 }).SetName("filter_by_any_A_any");
            yield return new TestCaseData(new[] { TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakE) }, new long[] { 4 }).SetName("filter_by_B_any_E");
            yield return new TestCaseData(new[] { TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB) }, new long[] { 1, 1, 4, 4 }).SetName("filter_by_topic_A_or_B");
            yield return new TestCaseData(new[] { TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakB) }, new long[] { 1, 4 }).SetName("filter_by_A_or_B_then_B");
        }
    }

    [TestCaseSource(nameof(FilterByTopicsTestsData))]
    public void filter_by_topics_and_return_logs_in_order(TopicExpression[] topics, long[] expectedBlockNumbers)
    {
        LogFilter logFilter = AllBlockFilter().WithTopicExpressions(topics).Build();

        FilterLog[] logs = _logFinder.FindLogs(logFilter).ToArray();

        long[] blockNumbers = logs.Select(static (log) => log.BlockNumber).ToArray();
        Assert.That(expectedBlockNumbers, Is.EqualTo(blockNumbers));
    }

    public static IEnumerable FilterByBlocksTestsData
    {
        get
        {
            yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToLatestBlock().Build(), 3).SetName("filter_by_latest_to_latest");
            yield return new TestCaseData(FilterBuilder.New().FromEarliestBlock().ToLatestBlock().Build(), 5).SetName("filter_by_earliest_to_latest");
            yield return new TestCaseData(FilterBuilder.New().FromEarliestBlock().ToPendingBlock().Build(), 5).SetName("filter_by_earliest_to_pending");
            yield return new TestCaseData(FilterBuilder.New().FromEarliestBlock().ToEarliestBlock().Build(), 0).SetName("filter_by_earliest_to_earliest");
            yield return new TestCaseData(FilterBuilder.New().FromBlock(1).ToBlock(1).Build(), 2).SetName("filter_by_block_one");
            yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToEarliestBlock().Build(), 0).SetName("filter_by_wrong_order");
        }
    }

    [TestCaseSource(nameof(FilterByBlocksTestsData))]
    public void filter_by_blocks(LogFilter filter, int expectedCount)
    {
        FilterLog[] logs = _logFinder.FindLogs(filter).ToArray();
        Assert.That(logs.Length, Is.EqualTo(expectedCount));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void filter_by_blocks_with_limit()
    {
        _logFinder = CreateLogFinder();
        LogFilter filter = FilterBuilder.New().FromLatestBlock().ToLatestBlock().Build();
        FilterLog[] logs = _logFinder.FindLogs(filter).ToArray();

        Assert.That(logs.Length, Is.EqualTo(3));
    }

    public static IEnumerable ComplexFilterTestsData
    {
        get
        {
            yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToLatestBlock()
                .WithTopicExpressions(TestTopicExpressions.Or(TestItem.KeccakD, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA))
                .WithAddresses(TestItem.AddressC, TestItem.AddressD).Build(), 2).SetName("complex_filter_C_D");

            yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToLatestBlock()
                .WithTopicExpressions(TestTopicExpressions.Or(TestItem.KeccakD, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA))
                .WithAddresses(TestItem.AddressC).Build(), 1).SetName("complex_filter_C");
        }
    }

    [TestCaseSource(nameof(ComplexFilterTestsData))]
    public void complex_filter(LogFilter filter, int expectedCount)
    {
        FilterLog[] logs = _logFinder.FindLogs(filter).ToArray();
        Assert.That(logs.Length, Is.EqualTo(expectedCount));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    [NonParallelizable]
    public async Task Throw_log_finder_operation_canceled_after_given_timeout([Values(2, 0.01)] double waitTime)
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(Timeout.MaxWaitTime);
        using CancellationTokenSource cancellationTokenSource = new(timeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        _logFinder = CreateLogFinder();
        LogFilter logFilter = AllBlockFilter().Build();
        IEnumerable<FilterLog> logs = _logFinder.FindLogs(logFilter, cancellationToken);

        await Task.Delay(timeout * waitTime);

        Action action = () => _ = logs.ToArray();

        if (waitTime > 1)
        {
            Assert.That(action, Throws
                .Exception.InstanceOf<OperationCanceledException>()
                .Or.InnerException.InstanceOf<OperationCanceledException>() // PLINQ can wrap into AggregateException
            );
        }
        else
        {
            Assert.DoesNotThrow(action);
        }
    }

    [TestCase("Empty index",
        1, 2,
        null, null,
        null, null
    )]
    [TestCase("No intersection, left",
        1, 2,
        4, 6,
        null, null
    )]
    [TestCase("No intersection, adjacent left",
        1, 3,
        4, 6,
        null, null
    )]
    [TestCase("1 block intersection, left",
        1, 4,
        4, 6,
        4, 4
    )]
    [TestCase("Partial intersection, left",
        1, 5,
        4, 6,
        4, 5
    )]
    [TestCase("Full containment, border right",
        1, 6,
        4, 6,
        4, 6
    )]
    [TestCase("Full containment",
        1, 9,
        4, 6,
        4, 6
    )]
    [TestCase("Full containment, border left",
        4, 9,
        4, 6,
        4, 6
    )]
    [TestCase("Partial intersection, right",
        5, 9,
        4, 6,
        5, 6
    )]
    [TestCase("1 block intersection, right",
        6, 9,
        4, 6,
        6, 6
    )]
    [TestCase("No intersection, adjacent right",
        7, 9,
        4, 6,
        null, null
    )]
    [TestCase("No intersection, right",
        8, 9,
        4, 6,
        null, null
    )]
    public void query_intersected_range_from_log_index(string name,
        int from, int to,
        int? indexFrom, int? indexTo,
        int? exFrom, int? exTo
    )
    {
        SetUp(true, chainLength: 10);

        ILogIndexStorage logIndexStorage = Substitute.For<ILogIndexStorage>();
        logIndexStorage.Enabled.Returns(true);
        logIndexStorage.MinBlockNumber.Returns(indexFrom);
        logIndexStorage.MaxBlockNumber.Returns(indexTo);
        logIndexStorage.GetEnumerator(Arg.Any<Address>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(_ => Array.Empty<int>().Cast<int>().GetEnumerator());

        Address address = TestItem.AddressA;
        BlockHeader fromHeader = Build.A.BlockHeader.WithNumber(from).TestObject;
        BlockHeader toHeader = Build.A.BlockHeader.WithNumber(to).TestObject;
        LogFilter filter = FilterBuilder.New()
            .FromBlock(from).ToBlock(to)
            .WithAddress(address)
            .Build();

        IndexedLogFinder logFinder = new(
            _blockTree, _receiptStorage, _receiptStorage, LimboLogs.Instance, _receiptsRecovery,
            logIndexStorage, minBlocksToUseIndex: 1
        );
        _ = logFinder.FindLogs(filter, fromHeader, toHeader).ToArray();

        if (exTo is not null && exFrom is not null)
            logIndexStorage.Received(1).GetEnumerator(address, exFrom.Value, exTo.Value);
        else
            logIndexStorage.DidNotReceiveWithAnyArgs().GetEnumerator(Arg.Any<Address>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void filter_throws_descriptive_exception_when_receipts_exist_in_compact_encoding_but_block_missing()
    {
        PersistentReceiptStorage receiptStorage = CreateCompactEncodedReceiptStorage();
        Block block = _rawBlockTree.FindBlock(1, BlockTreeLookupOptions.None)!;

        receiptStorage.Insert(block, [
            Build.A.Receipt.WithLogs(Build.A.LogEntry.WithAddress(TestItem.AddressA).TestObject).TestObject,
            Build.A.Receipt.TestObject
        ]);
        receiptStorage.ClearCache();

        Assert.That(() => CreateLogFinder(_rawBlockTree, receiptStorage).FindLogs(FilterBuilder.New().FromBlock(1).ToBlock(1).Build()).ToArray(), Throws.TypeOf<InvalidOperationException>().With.Message.Contains(@"missing block data"));
    }

    private static FilterBuilder AllBlockFilter() => FilterBuilder.New().FromEarliestBlock().ToPendingBlock();

    private LogFinder CreateLogFinder(IBlockFinder? blockFinder = null, IReceiptStorage? receiptStorage = null) =>
        new(blockFinder ?? _blockTree, receiptStorage ?? _receiptStorage, receiptStorage ?? _receiptStorage, LimboLogs.Instance, _receiptsRecovery);

    private PersistentReceiptStorage CreateCompactEncodedReceiptStorage()
    {
        TestMemColumnsDb<ReceiptsColumns> receiptsDb = new();
        receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, []);

        return new PersistentReceiptStorage(
            receiptsDb, _specProvider!, _receiptsRecovery, _rawBlockTree, new BlockStore(new MemDb()),
            new ReceiptConfig(), new ReceiptArrayStorageDecoder(true)
        )
        { MigratedBlockNumber = 0 };
    }
}
