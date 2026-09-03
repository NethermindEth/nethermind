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
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Db;
using Nethermind.History;
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
    public void Legacy_missing_bloom_and_log_entries_degrade_without_losing_valid_logs()
    {
        Block block = _rawBlockTree.FindBlock(1, BlockTreeLookupOptions.None)!;
        LogEntry validLog = Build.A.LogEntry
            .WithAddress(TestItem.AddressA)
            .WithTopics(TestItem.KeccakA)
            .TestObject;
        TxReceipt receipt = Build.A.Receipt.WithAllFieldsFilled
            .WithBlockHash(block.Hash)
            .WithBlockNumber(block.Number)
            .WithIndex(0)
            .WithLogs(validLog)
            .TestObject;
        ReceiptStorageDecoder decoder = new();
        byte[] receiptRlp = decoder.Encode(
            receipt,
            RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts).Bytes;
        receiptRlp = HeaderRlpTestHelper.ReplaceFieldEncoding(
            receiptRlp,
            fieldIndex: 9,
            [Rlp.EmptyByteArrayByte]);
        receiptRlp = HeaderRlpTestHelper.ReplaceFieldEncoding(
            receiptRlp,
            fieldIndex: 10,
            Rlp.Encode(Rlp.OfEmptyList, Rlp.Encode(validLog)).Bytes);
        int receiptsContentLength = receiptRlp.Length * 2 + 1;
        byte[] receiptsRlp = new byte[Rlp.LengthOfSequence(receiptsContentLength)];
        int receiptPosition = Rlp.StartSequence(receiptsRlp, 0, receiptsContentLength);
        receiptRlp.CopyTo(receiptsRlp.AsSpan(receiptPosition));
        receiptsRlp[receiptPosition + receiptRlp.Length] = Rlp.EmptyListByte;
        receiptRlp.CopyTo(receiptsRlp.AsSpan(receiptPosition + receiptRlp.Length + 1));
        LegacyReceiptFinder receiptFinder = new(receiptsRlp);
        LogFinder logFinder = new(
            _blockTree,
            receiptFinder,
            _receiptStorage,
            LimboLogs.Instance,
            _receiptsRecovery);

        FilterLog[] allLogs = logFinder.FindLogs(FilterBuilder.New().FromBlock(1).ToBlock(1).Build()).ToArray();
        FilterLog[] addressedLogs = logFinder.FindLogs(
            FilterBuilder.New().FromBlock(1).ToBlock(1).WithAddress(TestItem.AddressA).Build()).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allLogs, Has.Length.EqualTo(1));
            Assert.That(allLogs[0].LogIndex, Is.Zero);
            Assert.That(addressedLogs, Has.Length.EqualTo(1));
            Assert.That(addressedLogs[0].Address, Is.EqualTo(TestItem.AddressA));
        }
    }

    [Test]
    [NonParallelizable]
    public void Unenumerated_parallel_getLogs_does_not_leak_parallel_slot()
    {
        SetUp(allowReceiptIterator: true, chainLength: Environment.ProcessorCount + 2);

        bool before = LogFinder.IsParallelScanSlotHeld;

        _ = _logFinder.FindLogs(AllBlockFilter().Build());

        Assert.That(LogFinder.IsParallelScanSlotHeld, Is.EqualTo(before), "building an unenumerated parallel getLogs result must not acquire the process-wide parallel slot");
    }

    // Everything the receipt path lets escape must survive the PLINQ wrapping: several partitions faulting at
    // once arrive as one AggregateException, and the RPC layer maps the bare types onto error codes. A
    // single-block range takes the sequential path and cannot cover this.
    [TestCase(typeof(ResourceNotFoundException))]
    [TestCase(typeof(ConcurrencyLimitReachedException))]
    [MaxTime(Timeout.MaxTestTime)]
    [NonParallelizable]
    public void throw_unwrapped_exception_on_the_parallel_path(Type exceptionType)
    {
        int chainLength = Math.Max(64, Environment.ProcessorCount + 2);
        SetUp(allowReceiptIterator: true, chainLength: chainLength);

        ThrowingReceiptFinder throwing = new(exceptionType);
        LogFinder logFinder = new(_blockTree, throwing, _receiptStorage, LimboLogs.Instance,
            _receiptsRecovery, new ReceiptConfig { DeriveFromState = true });

        Assert.That(() => logFinder.FindLogs(AllBlockFilter().Build()).ToArray(),
            Throws.TypeOf(exceptionType));
        Assert.That(throwing.SawParallelScanSlotHeld, Is.True,
            "the scan must have taken the parallel path, or this test cannot cover the AggregateException unwrap");
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
            yield return new TestCaseData(new[] { TestTopicExpressions.Specific(TestItem.KeccakA) }, new ulong[] { 1ul, 1ul, 4ul }).SetName("filter_by_topic_A");
            yield return new TestCaseData(new[] { TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakB) }, new ulong[] { 1ul, 4ul }).SetName("filter_by_any_then_topic_B");
            yield return new TestCaseData(new[] { TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Any }, new ulong[] { 4ul }).SetName("filter_by_any_A_any");
            yield return new TestCaseData(new[] { TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakE) }, new ulong[] { 4ul }).SetName("filter_by_B_any_E");
            yield return new TestCaseData(new[] { TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB) }, new ulong[] { 1ul, 1ul, 4ul, 4ul }).SetName("filter_by_topic_A_or_B");
            yield return new TestCaseData(new[] { TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakB) }, new ulong[] { 1ul, 4ul }).SetName("filter_by_A_or_B_then_B");
        }
    }

    [TestCaseSource(nameof(FilterByTopicsTestsData))]
    public void filter_by_topics_and_return_logs_in_order(TopicExpression[] topics, ulong[] expectedBlockNumbers)
    {
        LogFilter logFilter = AllBlockFilter().WithTopicExpressions(topics).Build();

        FilterLog[] logs = _logFinder.FindLogs(logFilter).ToArray();

        ulong[] blockNumbers = logs.Select(static (log) => log.BlockNumber).ToArray();
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
        1UL, 2UL,
        null, null,
        null, null
    )]
    [TestCase("No intersection, left",
        1UL, 2UL,
        4, 6,
        null, null
    )]
    [TestCase("No intersection, adjacent left",
        1UL, 3UL,
        4, 6,
        null, null
    )]
    [TestCase("1 block intersection, left",
        1UL, 4UL,
        4, 6,
        4, 4
    )]
    [TestCase("Partial intersection, left",
        1UL, 5UL,
        4, 6,
        4, 5
    )]
    [TestCase("Full containment, border right",
        1UL, 6UL,
        4, 6,
        4, 6
    )]
    [TestCase("Full containment",
        1UL, 9UL,
        4, 6,
        4, 6
    )]
    [TestCase("Full containment, border left",
        4UL, 9UL,
        4, 6,
        4, 6
    )]
    [TestCase("Partial intersection, right",
        5UL, 9UL,
        4, 6,
        5, 6
    )]
    [TestCase("1 block intersection, right",
        6UL, 9UL,
        4, 6,
        6, 6
    )]
    [TestCase("No intersection, adjacent right",
        7UL, 9UL,
        4, 6,
        null, null
    )]
    [TestCase("No intersection, right",
        8UL, 9UL,
        4, 6,
        null, null
    )]
    public void query_intersected_range_from_log_index(string name,
        ulong from, ulong to,
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

    private const ulong BoundaryOldestStored = 50;
    private const int BoundaryFrom = 10;
    private const int BoundaryTo = 200;

    private static IndexedLogFinder CreateBoundaryFinder(out IBlockFinder blockFinder, out ILogIndexStorage index, IPrunedLogsRetention? retention = null, int? indexFrom = 0, ulong lowestStored = BoundaryOldestStored, IHistoryPruner? historyPruner = null)
    {
        blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.GetLowestBlock().Returns(lowestStored);
        index = Substitute.For<ILogIndexStorage>();
        index.Enabled.Returns(true);
        index.MinBlockNumber.Returns(indexFrom);
        index.MaxBlockNumber.Returns(indexFrom is null ? (int?)null : BoundaryTo);
        return new IndexedLogFinder(
            blockFinder, Substitute.For<IReceiptFinder>(), Substitute.For<IReceiptStorage>(), LimboLogs.Instance,
            Substitute.For<IReceiptsRecovery>(), index, prunedLogsRetention: retention, historyPruner: historyPruner);
    }

    private static LogFilter BoundaryFilter(ulong from = BoundaryFrom) =>
        FilterBuilder.New().FromBlock(from).ToBlock(BoundaryTo).WithAddress(TestItem.AddressA).Build();

    private static BlockHeader BoundaryHeader(ulong number) => Build.A.BlockHeader.WithNumber(number).TestObject;

    [Test]
    public void Should_ServeBetweenTheReclaimCursorAndThePublishedBoundary_FromTheIndex()
    {
        IHistoryPruner pruner = Substitute.For<IHistoryPruner>();
        pruner.OldestUnreclaimedBlockNumber.Returns(1UL);
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out ILogIndexStorage index, historyPruner: pruner);

        FilterLog[] logs = finder.FindLogs(BoundaryFilter(), BoundaryHeader(BoundaryFrom), BoundaryHeader(BoundaryTo)).ToArray();

        Assert.That(logs, Is.Empty);
        index.Received().GetEnumerator(TestItem.AddressA, BoundaryFrom, BoundaryTo);
    }

    [Test]
    public void Should_ReportAnInvertedRangeAsInvalid_NotAsPrunedData()
    {
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out _);
        LogFilter inverted = FilterBuilder.New().FromBlock(30UL).ToBlock(10UL).WithAddress(TestItem.AddressA).Build();

        Assert.Throws<ArgumentException>(() =>
            finder.FindLogs(inverted, BoundaryHeader(30), BoundaryHeader(10)).ToArray());
    }

    [Test]
    public void Should_FailClosed_BelowTheReclaimCursor()
    {
        IHistoryPruner pruner = Substitute.For<IHistoryPruner>();
        pruner.OldestUnreclaimedBlockNumber.Returns(BoundaryOldestStored);
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out _, historyPruner: pruner);

        Assert.Throws<ResourceNotFoundException>(() =>
            finder.FindLogs(BoundaryFilter(), BoundaryHeader(BoundaryFrom), BoundaryHeader(BoundaryTo)).ToArray());
    }

    [Test]
    public void Should_AnswerAWholeGenesisQuery_WhenTheBoundaryIsTheAncientBarrier()
    {
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out ILogIndexStorage index, indexFrom: 1, lowestStored: 24_600_000);
        LogFilter genesisFilter = FilterBuilder.New().FromBlock(0UL).ToBlock(0UL).WithAddress(TestItem.AddressA).Build();

        FilterLog[] logs = finder.FindLogs(genesisFilter, BoundaryHeader(0), BoundaryHeader(0)).ToArray();

        Assert.That(logs, Is.Empty);
        index.DidNotReceive().GetEnumerator(Arg.Any<Address>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Test]
    public void Should_ServeBelowTheOldestStoredBlockFromTheIndex_WhenTheRetentionCoversTheFilter()
    {
        IPrunedLogsRetention retention = Substitute.For<IPrunedLogsRetention>();
        retention.RetainsLogsFor(Arg.Any<IReadOnlyCollection<AddressAsKey>>(), Arg.Any<ulong>(), Arg.Any<ulong>()).Returns(true);
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out ILogIndexStorage index, retention);

        FilterLog[] logs = finder.FindLogs(BoundaryFilter(), BoundaryHeader(BoundaryFrom), BoundaryHeader(BoundaryTo)).ToArray();

        Assert.That(logs, Is.Empty);
        index.Received().GetEnumerator(TestItem.AddressA, BoundaryFrom, BoundaryTo);
    }

    [Test]
    public void Should_FailClosedBelowTheOldestStoredBlock_WhenTheRetentionDoesNotCoverTheFilter()
    {
        IPrunedLogsRetention retention = Substitute.For<IPrunedLogsRetention>();
        retention.RetainsLogsFor(Arg.Any<IReadOnlyCollection<AddressAsKey>>(), Arg.Any<ulong>(), Arg.Any<ulong>()).Returns(false);
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out _, retention);

        Assert.Throws<ResourceNotFoundException>(() =>
            finder.FindLogs(BoundaryFilter(), BoundaryHeader(BoundaryFrom), BoundaryHeader(BoundaryTo)).ToArray());
    }

    [Test]
    public void Should_FailClosedBelowTheOldestStoredBlock_WhenNoRetentionIsConfigured()
    {
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out _);

        Assert.Throws<ResourceNotFoundException>(() =>
            finder.FindLogs(BoundaryFilter(), BoundaryHeader(BoundaryFrom), BoundaryHeader(BoundaryTo)).ToArray());
    }

    [Test]
    public void Should_FailClosedBelowTheOldestStoredBlock_EvenWhenBothEndpointsCarryNoReceipts()
    {
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out _);
        BlockHeader emptyFrom = BoundaryHeader(BoundaryFrom);
        BlockHeader emptyTo = BoundaryHeader(BoundaryTo);

        Assert.That(emptyFrom.ReceiptsRoot, Is.EqualTo(Keccak.EmptyTreeHash),
            "the scenario needs endpoints the endpoint probe cannot see, so their receipt roots must be empty");

        Assert.Throws<ResourceNotFoundException>(() =>
            finder.FindLogs(BoundaryFilter(), emptyFrom, emptyTo).ToArray());
    }

    [Test]
    public void Should_FailClosedBelowTheOldestStoredBlock_EvenWhenTheIndexStartsExactlyAtTheBoundary()
    {
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out _, indexFrom: (int)BoundaryOldestStored);

        Assert.Throws<ResourceNotFoundException>(() =>
            finder.FindLogs(BoundaryFilter(), BoundaryHeader(BoundaryFrom), BoundaryHeader(BoundaryTo)).ToArray());
    }

    [Test]
    public void Should_FailClosedBelowTheOldestStoredBlock_OnEveryPollOfAStoredFilter()
    {
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out _);
        LogFilter stored = BoundaryFilter();

        Assert.Throws<ResourceNotFoundException>(() =>
            finder.FindLogs(stored, BoundaryHeader(BoundaryFrom), BoundaryHeader(BoundaryTo)).ToArray());
        Assert.That(stored.UseIndex, Is.True,
            "eth_getFilterLogs reuses the stored LogFilter instance, so a throw must not leave UseIndex cleared and route the retry around the guard");
        Assert.Throws<ResourceNotFoundException>(() =>
            finder.FindLogs(stored, BoundaryHeader(BoundaryFrom), BoundaryHeader(BoundaryTo)).ToArray());
    }

    [Test]
    public void Should_FallBackToThePlainScan_ForAnAddressLessFilter()
    {
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out ILogIndexStorage index);
        LogFilter addressLess = FilterBuilder.New().FromBlock((ulong)BoundaryFrom).ToBlock(BoundaryTo).Build();

        FilterLog[] logs = finder.FindLogs(addressLess, BoundaryHeader(BoundaryFrom), BoundaryHeader(BoundaryTo)).ToArray();

        Assert.That(logs, Is.Empty);
        index.DidNotReceiveWithAnyArgs().GetEnumerator(Arg.Any<Address>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Test]
    public void Should_FailClosedBelowTheOldestStoredBlock_ForATopicOnlyFilter()
    {
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out _);
        LogFilter topicOnly = FilterBuilder.New()
            .FromBlock((ulong)BoundaryFrom).ToBlock(BoundaryTo)
            .WithAnyAddress()
            .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA))
            .Build();

        Assert.Throws<ResourceNotFoundException>(() =>
            finder.FindLogs(topicOnly, BoundaryHeader(BoundaryFrom), BoundaryHeader(BoundaryTo)).ToArray(),
            "a sliced index holds fabricated empties below the boundary, so a topic-only filter there must refuse rather than answer silently short");
    }

    [Test]
    public void Should_FallBackToThePlainScan_WhenNothingIsIndexedYet()
    {
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out ILogIndexStorage index, indexFrom: null);

        FilterLog[] logs = finder.FindLogs(BoundaryFilter(), BoundaryHeader(BoundaryFrom), BoundaryHeader(BoundaryTo)).ToArray();

        Assert.That(logs, Is.Empty);
        index.DidNotReceiveWithAnyArgs().GetEnumerator(Arg.Any<Address>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Test]
    public void Should_UseTheFullIndexRange_WhenTheQueryDoesNotReachBelowTheOldestStoredBlock()
    {
        IndexedLogFinder finder = CreateBoundaryFinder(out _, out ILogIndexStorage index, lowestStored: 1UL);

        FilterLog[] logs = finder.FindLogs(BoundaryFilter(), BoundaryHeader(BoundaryFrom), BoundaryHeader(BoundaryTo)).ToArray();

        Assert.That(logs, Is.Empty);
        index.Received().GetEnumerator(TestItem.AddressA, BoundaryFrom, BoundaryTo);
    }

    [Test]
    public void Should_AnswerAFromGenesisQueryUnchanged_OnANodeThatNeverPrunedHistory()
    {
        IndexedLogFinder finder = CreateBoundaryFinder(out IBlockFinder blockFinder, out ILogIndexStorage index, lowestStored: 1UL);
        BlockHeader genesis = BoundaryHeader(0);
        blockFinder.FindHeader(0UL).Returns(genesis);

        FilterLog[] logs = finder.FindLogs(BoundaryFilter(from: 0), genesis, BoundaryHeader(BoundaryTo)).ToArray();

        Assert.That(logs, Is.Empty);
        index.Received().GetEnumerator(TestItem.AddressA, 1, BoundaryTo);
    }

    private static FilterBuilder AllBlockFilter() => FilterBuilder.New().FromEarliestBlock().ToPendingBlock();

    // NSubstitute cannot stub a method with a ref-struct out parameter, so the throwing finder is hand-rolled.
    private sealed class ThrowingReceiptFinder(Type exceptionType) : IReceiptFinder
    {
        public bool SawParallelScanSlotHeld { get; private set; }

        public Hash256? FindBlockHash(Hash256 txHash) => null;
        public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true) => throw Create();
        public TxReceipt[] Get(Hash256 blockHash, bool recover = true) => throw Create();
        public bool CanGetReceiptsByHash(ulong blockNumber) => true;

        public bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
        {
            // Recorded from inside a worker: proves the enumeration really went through the PLINQ path rather than
            // silently degrading to sequential (where the bare exception propagates without any unwrap).
            SawParallelScanSlotHeld |= LogFinder.IsParallelScanSlotHeld;
            throw Create();
        }

        private Exception Create() => (Exception)Activator.CreateInstance(exceptionType, "receipts path failure")!;
    }

    private sealed class LegacyReceiptFinder(byte[] receiptsData) : IReceiptFinder
    {
        private readonly TestMemDb _blocksDb = new();

        public Hash256? FindBlockHash(Hash256 txHash) => null;
        public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true) => [];
        public TxReceipt[] Get(Hash256 blockHash, bool recover = true) => [];
        public bool CanGetReceiptsByHash(ulong blockNumber) => true;

        public bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
        {
            iterator = new ReceiptsIterator(
                receiptsData,
                _blocksDb,
                recoveryContextFactory: null,
                new ReceiptStorageDecoder());
            return true;
        }
    }

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
