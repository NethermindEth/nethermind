// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Db.Blooms;
using Nethermind.Facade.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Find
{
    public class LogFinderTests
    {
        private IBlockTree _blockTree = null!;
        private BlockTree _rawBlockTree = null!;
        private IReceiptStorage _receiptStorage = null!;
        private LogFinder _logFinder = null!;
        private IBloomStorage _bloomStorage = null!;
        private IReceiptsRecovery _receiptsRecovery = null!;
        private Block _headTestBlock = null!;

        [SetUp]
        public void SetUp()
        {
            SetUp(true);
        }

        private void SetUp(bool allowReceiptIterator)
        {
            var specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<BlockHeader>()).IsEip155Enabled.Returns(true);
            specProvider.GetSpec(Arg.Any<ForkActivation>()).IsEip155Enabled.Returns(true);
            _receiptStorage = new InMemoryReceiptStorage(allowReceiptIterator);
            _rawBlockTree = Build.A.BlockTree()
                .WithTransactions(_receiptStorage, LogsForBlockBuilder)
                .OfChainLength(out _headTestBlock, 5)
                .TestObject;
            _blockTree = _rawBlockTree;
            _bloomStorage = new BloomStorage(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());
            _receiptsRecovery = Substitute.For<IReceiptsRecovery>();
            _logFinder = new LogFinder(_blockTree, _receiptStorage, _receiptStorage, _bloomStorage, LimboLogs.Instance, _receiptsRecovery);
        }

        private void SetupHeadWithNoTransaction()
        {
            Block blockWithNoTransaction = Build.A.Block
                .WithParent(_headTestBlock)
                .TestObject;
            _rawBlockTree.SuggestBlock(blockWithNoTransaction)
                .Should().Be(AddBlockResult.Added);
            _rawBlockTree.UpdateMainChain(blockWithNoTransaction);
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

        public static IEnumerable WithBloomValues
        {
            get
            {
                yield return false;
                yield return true;
            }
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void filter_all_logs([ValueSource(nameof(WithBloomValues))] bool withBloomDb, [Values(false, true)] bool allowReceiptIterator)
        {
            SetUp(allowReceiptIterator);
            StoreTreeBlooms(withBloomDb);
            var logFilter = AllBlockFilter().Build();
            var logs = _logFinder.FindLogs(logFilter).ToArray();
            logs.Length.Should().Be(5);
            var indexes = logs.Select(l => (int)l.LogIndex).ToArray();
            // indexes[0].Should().Be(0);
            // indexes[1].Should().Be(1);
            // indexes[2].Should().Be(0);
            // indexes[3].Should().Be(1);
            // indexes[4].Should().Be(2);
            indexes.Should().BeEquivalentTo(new[] { 0, 1, 0, 1, 2 });
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void filter_all_logs_iteratively([ValueSource(nameof(WithBloomValues))] bool withBloomDb, [Values(false, true)] bool allowReceiptIterator)
        {
            SetUp(allowReceiptIterator);
            LogFilter logFilter = AllBlockFilter().Build();
            FilterLog[] logs = _logFinder.FindLogs(logFilter).ToArray();
            logs.Length.Should().Be(5);
            var indexes = logs.Select(l => (int)l.LogIndex).ToArray();
            // indexes[0].Should().Be(0);
            // indexes[1].Should().Be(1);
            // indexes[2].Should().Be(0);
            // indexes[3].Should().Be(1);
            // indexes[4].Should().Be(2);
            // BeEquivalentTo does not check the ordering!!! :O
            indexes.Should().BeEquivalentTo(new[] { 0, 1, 0, 1, 2 });
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void throw_exception_when_receipts_are_missing([ValueSource(nameof(WithBloomValues))] bool withBloomDb)
        {
            StoreTreeBlooms(withBloomDb);
            _receiptStorage = NullReceiptStorage.Instance;
            _logFinder = new LogFinder(_blockTree, _receiptStorage, _receiptStorage, _bloomStorage, LimboLogs.Instance, _receiptsRecovery);

            var logFilter = AllBlockFilter().Build();

            _logFinder.Invoking(it => it.FindLogs(logFilter))
                .Should()
                .Throw<ResourceNotFoundException>();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void when_receipts_are_missing_and_header_has_no_receipt_root_do_not_throw_exception_()
        {
            _receiptStorage = NullReceiptStorage.Instance;
            _logFinder = new LogFinder(_blockTree, _receiptStorage, _receiptStorage, _bloomStorage, LimboLogs.Instance, _receiptsRecovery);

            SetupHeadWithNoTransaction();

            var logFilter = AllBlockFilter().Build();

            _logFinder.Invoking(it => it.FindLogs(logFilter))
                .Should()
                .NotThrow<ResourceNotFoundException>();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void filter_all_logs_should_throw_when_to_block_is_not_found([ValueSource(nameof(WithBloomValues))] bool withBloomDb)
        {
            StoreTreeBlooms(withBloomDb);
            var blockFinder = Substitute.For<IBlockFinder>();
            _logFinder = new LogFinder(blockFinder, _receiptStorage, _receiptStorage, _bloomStorage, LimboLogs.Instance, _receiptsRecovery);
            var logFilter = AllBlockFilter().Build();
            var action = new Func<IEnumerable<FilterLog>>(() => _logFinder.FindLogs(logFilter));
            action.Should().Throw<ResourceNotFoundException>();
            blockFinder.Received().FindHeader(logFilter.ToBlock, false);
            blockFinder.DidNotReceive().FindHeader(logFilter.FromBlock);
        }

        public static IEnumerable FilterByAddressTestsData
        {
            get
            {
                yield return new TestCaseData(new[] { TestItem.AddressA }, 2, false);
                yield return new TestCaseData(new[] { TestItem.AddressB }, 1, false);
                yield return new TestCaseData(new[] { TestItem.AddressC }, 1, false);
                yield return new TestCaseData(new[] { TestItem.AddressD }, 1, false);
                yield return new TestCaseData(new[] { TestItem.AddressA, TestItem.AddressC, TestItem.AddressD }, 4, false);

                yield return new TestCaseData(new[] { TestItem.AddressA }, 2, true);
                yield return new TestCaseData(new[] { TestItem.AddressB }, 1, true);
                yield return new TestCaseData(new[] { TestItem.AddressC }, 1, true);
                yield return new TestCaseData(new[] { TestItem.AddressD }, 1, true);
                yield return new TestCaseData(new[] { TestItem.AddressA, TestItem.AddressC, TestItem.AddressD }, 4, true);
            }
        }

        [TestCaseSource(nameof(FilterByAddressTestsData))]
        public void filter_by_address(Address[] addresses, int expectedCount, bool withBloomDb)
        {
            StoreTreeBlooms(withBloomDb);
            var filterBuilder = AllBlockFilter();
            filterBuilder = addresses.Length == 1 ? filterBuilder.WithAddress(addresses[0]) : filterBuilder.WithAddresses(addresses);
            var logFilter = filterBuilder.Build();

            var logs = _logFinder.FindLogs(logFilter).ToArray();

            logs.Length.Should().Be(expectedCount);
        }

        public static IEnumerable FilterByTopicsTestsData
        {
            get
            {
                yield return new TestCaseData(new[] { TestTopicExpressions.Specific(TestItem.KeccakA) }, false, new long[] { 1, 1, 4 });
                yield return new TestCaseData(new[] { TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakB) }, false, new long[] { 1, 4 });
                yield return new TestCaseData(new[] { TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Any }, false, new long[] { 4 });
                yield return new TestCaseData(new[] { TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakE) }, false, new long[] { 4 });
                yield return new TestCaseData(new[] { TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB) }, false, new long[] { 1, 1, 4, 4 });
                yield return new TestCaseData(new[] { TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakB) }, false, new long[] { 1, 4 });

                yield return new TestCaseData(new[] { TestTopicExpressions.Specific(TestItem.KeccakA) }, true, new long[] { 1, 1, 4 });
                yield return new TestCaseData(new[] { TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakB) }, true, new long[] { 1, 4 });
                yield return new TestCaseData(new[] { TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Any }, true, new long[] { 4 });
                yield return new TestCaseData(new[] { TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakE) }, true, new long[] { 4 });
                yield return new TestCaseData(new[] { TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB) }, true, new long[] { 1, 1, 4, 4 });
                yield return new TestCaseData(new[] { TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakB) }, true, new long[] { 1, 4 });
            }
        }

        [TestCaseSource(nameof(FilterByTopicsTestsData))]
        public void filter_by_topics_and_return_logs_in_order(TopicExpression[] topics, bool withBloomDb, long[] expectedBlockNumbers)
        {
            StoreTreeBlooms(withBloomDb);
            var logFilter = AllBlockFilter().WithTopicExpressions(topics).Build();

            var logs = _logFinder.FindLogs(logFilter).ToArray();

            var blockNumbers = logs.Select((log) => log.BlockNumber).ToArray();
            Assert.AreEqual(blockNumbers, expectedBlockNumbers);
        }

        public static IEnumerable FilterByBlocksTestsData
        {
            get
            {
                yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToLatestBlock().Build(), 3, false);
                yield return new TestCaseData(FilterBuilder.New().FromEarliestBlock().ToLatestBlock().Build(), 5, false);
                yield return new TestCaseData(FilterBuilder.New().FromEarliestBlock().ToPendingBlock().Build(), 5, false);
                yield return new TestCaseData(FilterBuilder.New().FromEarliestBlock().ToEarliestBlock().Build(), 0, false);
                yield return new TestCaseData(FilterBuilder.New().FromBlock(1).ToBlock(1).Build(), 2, false);
                yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToEarliestBlock().Build(), 0, false); //wrong order test

                yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToLatestBlock().Build(), 3, true);
                yield return new TestCaseData(FilterBuilder.New().FromEarliestBlock().ToLatestBlock().Build(), 5, true);
                yield return new TestCaseData(FilterBuilder.New().FromEarliestBlock().ToPendingBlock().Build(), 5, true);
                yield return new TestCaseData(FilterBuilder.New().FromEarliestBlock().ToEarliestBlock().Build(), 0, true);
                yield return new TestCaseData(FilterBuilder.New().FromBlock(1).ToBlock(1).Build(), 2, true);
                yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToEarliestBlock().Build(), 0, true); //wrong order test
            }
        }

        [TestCaseSource(nameof(FilterByBlocksTestsData))]
        public void filter_by_blocks(LogFilter filter, int expectedCount, bool withBloomDb)
        {
            StoreTreeBlooms(withBloomDb);
            var logs = _logFinder.FindLogs(filter).ToArray();
            logs.Length.Should().Be(expectedCount);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void filter_by_blocks_with_limit([ValueSource(nameof(WithBloomValues))] bool withBloomDb)
        {
            StoreTreeBlooms(withBloomDb);
            _logFinder = new LogFinder(_blockTree, _receiptStorage, _receiptStorage, _bloomStorage, LimboLogs.Instance, _receiptsRecovery, 2);
            var filter = FilterBuilder.New().FromLatestBlock().ToLatestBlock().Build();
            var logs = _logFinder.FindLogs(filter).ToArray();

            logs.Length.Should().Be(3);
        }


        public static IEnumerable ComplexFilterTestsData
        {
            get
            {
                yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToLatestBlock()
                    .WithTopicExpressions(TestTopicExpressions.Or(TestItem.KeccakD, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA))
                    .WithAddresses(TestItem.AddressC, TestItem.AddressD).Build(), 2, false);

                yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToLatestBlock()
                    .WithTopicExpressions(TestTopicExpressions.Or(TestItem.KeccakD, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA))
                    .WithAddresses(TestItem.AddressC).Build(), 1, false);

                yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToLatestBlock()
                    .WithTopicExpressions(TestTopicExpressions.Or(TestItem.KeccakD, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA))
                    .WithAddresses(TestItem.AddressC, TestItem.AddressD).Build(), 2, true);

                yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToLatestBlock()
                    .WithTopicExpressions(TestTopicExpressions.Or(TestItem.KeccakD, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA))
                    .WithAddresses(TestItem.AddressC).Build(), 1, true);
            }
        }

        [TestCaseSource(nameof(ComplexFilterTestsData))]
        public void complex_filter(LogFilter filter, int expectedCount, bool withBloomDb)
        {
            StoreTreeBlooms(withBloomDb);
            var logs = _logFinder.FindLogs(filter).ToArray();
            logs.Length.Should().Be(expectedCount);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task Throw_log_finder_operation_canceled_after_given_timeout([Values(2, 0.01)] double waitTime)
        {
            var timeout = TimeSpan.FromMilliseconds(Timeout.MaxWaitTime);
            using CancellationTokenSource cancellationTokenSource = new(timeout);
            CancellationToken cancellationToken = cancellationTokenSource.Token;

            StoreTreeBlooms(true);
            _logFinder = new LogFinder(_blockTree, _receiptStorage, _receiptStorage, _bloomStorage, LimboLogs.Instance, _receiptsRecovery);
            var logFilter = AllBlockFilter().Build();
            var logs = _logFinder.FindLogs(logFilter, cancellationToken);

            await Task.Delay(timeout * waitTime);

            Func<FilterLog[]> action = () => logs.ToArray();

            if (waitTime > 1)
            {
                action.Should().Throw<AggregateException>().WithInnerException<OperationCanceledException>();
            }
            else
            {
                action.Should().NotThrow();
            }
        }

        private static FilterBuilder AllBlockFilter() => FilterBuilder.New().FromEarliestBlock().ToPendingBlock();

        private void StoreTreeBlooms(bool withBloomDb)
        {
            if (withBloomDb)
            {
                for (int i = 0; i <= _blockTree.Head.Number; i++)
                {
                    _bloomStorage.Store(i, _blockTree.FindHeader(i).Bloom);
                }
            }
        }

    }
}
