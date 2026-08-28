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

    private static IEnumerable<TestCaseData> LogIndexRangeCases()
    {
        yield return Case("Empty index", 1UL, 2UL, null, null, null, null);
        yield return Case("No intersection, left", 1UL, 2UL, 4, 6, null, null);
        yield return Case("No intersection, adjacent left", 1UL, 3UL, 4, 6, null, null);
        yield return Case("1 block intersection, left", 1UL, 4UL, 4, 6, 4, 4);
        yield return Case("Partial intersection, left", 1UL, 5UL, 4, 6, 4, 5);
        yield return Case("Full containment, border right", 1UL, 6UL, 4, 6, 4, 6);
        yield return Case("Full containment", 1UL, 9UL, 4, 6, 4, 6);
        yield return Case("Full containment, border left", 4UL, 9UL, 4, 6, 4, 6);
        yield return Case("Partial intersection, right", 5UL, 9UL, 4, 6, 5, 6);
        yield return Case("1 block intersection, right", 6UL, 9UL, 4, 6, 6, 6);
        yield return Case("No intersection, adjacent right", 7UL, 9UL, 4, 6, null, null);
        yield return Case("No intersection, right", 8UL, 9UL, 4, 6, null, null);

        static TestCaseData Case(string name, ulong from, ulong to, int? indexFrom, int? indexTo, int? exFrom, int? exTo) =>
            new TestCaseData(from, to, indexFrom, indexTo, exFrom, exTo).SetName($"{{m}}({name})");
    }

    [TestCaseSource(nameof(LogIndexRangeCases))]
    public void query_intersected_range_from_log_index(ulong from, ulong to, int? indexFrom, int? indexTo, int? exFrom, int? exTo)
    {
        SetUp(true, chainLength: 10);

        ILogIndexStorage logIndexStorage = CreateLogIndexStorage(indexFrom, indexTo);

        Address address = TestItem.AddressA;
        BlockHeader fromHeader = Build.A.BlockHeader.WithNumber(from).TestObject;
        BlockHeader toHeader = Build.A.BlockHeader.WithNumber(to).TestObject;
        LogFilter filter = FilterBuilder.New()
            .FromBlock(from).ToBlock(to)
            .WithAddress(address)
            .Build();

        IndexedLogFinder logFinder = CreateIndexedLogFinder(logIndexStorage);
        _ = logFinder.FindLogs(filter, fromHeader, toHeader).ToArray();

        if (exTo is not null && exFrom is not null)
            logIndexStorage.Received(1).GetEnumerator(address, exFrom.Value, exTo.Value);
        else
            logIndexStorage.DidNotReceiveWithAnyArgs().GetEnumerator(Arg.Any<Address>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [TestCase(2, true, TestName = "range 5 > limit 2 -> throws")]
    [TestCase(5, false, TestName = "range 5 <= limit 5 -> allowed")]
    [TestCase(0, false, TestName = "limit disabled -> allowed")]
    public void FindLogs_enforces_max_block_depth(int maxBlockDepth, bool shouldThrow)
    {
        SetUp(true);
        LogFinder logFinder = CreateLogFinder(receiptConfig: new ReceiptConfig { MaxBlockDepth = maxBlockDepth });
        LogFilter filter = FilterBuilder.New().FromBlock(0UL).ToBlock(4).Build();

        if (shouldThrow)
        {
            Assert.That(() => logFinder.FindLogs(filter).ToArray(),
                Throws.TypeOf<ArgumentException>().With.Message.Contains(nameof(IReceiptConfig.MaxBlockDepth)));
        }
        else
        {
            Assert.That(() => logFinder.FindLogs(filter).ToArray(), Throws.Nothing);
        }
    }

    [TestCaseSource(nameof(LogIndexRangeCases))]
    public void FindLogs_ignores_max_block_depth_when_log_index_enabled(ulong from, ulong to, int? indexFrom, int? indexTo, int? exFrom, int? exTo)
    {
        SetUp(true, chainLength: 10);

        ILogIndexStorage logIndexStorage = CreateLogIndexStorage(indexFrom, indexTo);

        LogFilter filter = FilterBuilder.New().FromBlock(from).ToBlock(to).WithAddress(TestItem.AddressA).Build();

        IndexedLogFinder logFinder = CreateIndexedLogFinder(logIndexStorage, new ReceiptConfig { MaxBlockDepth = 1 });

        Assert.That(() => logFinder.FindLogs(filter).ToArray(), Throws.Nothing);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindLogs_enforces_max_block_depth_when_index_is_opted_out()
    {
        SetUp(true, chainLength: 10);

        ILogIndexStorage logIndexStorage = CreateLogIndexStorage(4, 6);

        LogFilter filter = FilterBuilder.New().FromBlock(0UL).ToBlock(9).WithAddress(TestItem.AddressA).Build();
        filter.UseIndex = false;

        IndexedLogFinder logFinder = CreateIndexedLogFinder(logIndexStorage, new ReceiptConfig { MaxBlockDepth = 1 });

        Assert.That(() => logFinder.FindLogs(filter).ToArray(),
            Throws.TypeOf<ArgumentException>().With.Message.Contains(nameof(IReceiptConfig.MaxBlockDepth)));
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

    private LogFinder CreateLogFinder(IBlockFinder? blockFinder = null, IReceiptStorage? receiptStorage = null, IReceiptConfig? receiptConfig = null) =>
        new(blockFinder ?? _blockTree, receiptStorage ?? _receiptStorage, receiptStorage ?? _receiptStorage, LimboLogs.Instance, _receiptsRecovery, receiptConfig ?? new ReceiptConfig());

    private IndexedLogFinder CreateIndexedLogFinder(ILogIndexStorage logIndexStorage, IReceiptConfig? receiptConfig = null) =>
        new(_blockTree, _receiptStorage, _receiptStorage, LimboLogs.Instance, _receiptsRecovery, receiptConfig ?? new ReceiptConfig(), logIndexStorage, minBlocksToUseIndex: 1);

    private static ILogIndexStorage CreateLogIndexStorage(int? indexFrom, int? indexTo)
    {
        ILogIndexStorage logIndexStorage = Substitute.For<ILogIndexStorage>();
        logIndexStorage.Enabled.Returns(true);
        logIndexStorage.MinBlockNumber.Returns(indexFrom);
        logIndexStorage.MaxBlockNumber.Returns(indexTo);
        logIndexStorage.GetEnumerator(Arg.Any<Address>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(_ => Array.Empty<int>().Cast<int>().GetEnumerator());
        return logIndexStorage;
    }

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
