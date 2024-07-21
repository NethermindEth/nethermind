

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Find
{
    public class LogIndexFinderTests
    {

        private IBlockTree _blockTree = null!;
        private BlockTree _rawBlockTree = null!;
        private IReceiptStorage _receiptStorage = null!;
        private LogFinder _logFinder = null!;
        private IBloomStorage _bloomStorage = null!;
        private IReceiptsRecovery _receiptsRecovery = null!;
        private Block _headTestBlock = null!;
        private LogIndexStorage _logIndexStorage = null!;

        [SetUp]
        public void SetUp()
        {
            SetUp(true);
        }


        [TearDown]
        public void TearDown() => _bloomStorage?.Dispose();

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
            _logIndexStorage = new LogIndexStorage();
            _bloomStorage = new BloomStorage(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());
            _receiptsRecovery = Substitute.For<IReceiptsRecovery>();
            _logFinder = new LogFinder(_blockTree, _receiptStorage, _receiptStorage, _bloomStorage, LimboLogs.Instance, _receiptsRecovery, _logIndexStorage);
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
            StoreLogIndex();
            var filterBuilder = AllBlockFilter();
            filterBuilder = addresses.Length == 1 ? filterBuilder.WithAddress(addresses[0]) : filterBuilder.WithAddresses(addresses);
            var logFilter = filterBuilder.Build();

            var logs = _logFinder.FindLogsTest(logFilter).ToArray();

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
            StoreLogIndex();
            var logFilter = AllBlockFilter().WithTopicExpressions(topics).Build();

            var logs = _logFinder.FindLogs(logFilter).ToArray();

            var blockNumbers = logs.Select((log) => log.BlockNumber).ToArray();
            Assert.That(expectedBlockNumbers, Is.EqualTo(blockNumbers));
        }

        [Test]
        public void filter_by_address_from_file()
        {
            _logIndexStorage.LoadFile(TestItem.AddressA, "/Users/srj31/Documents/Nethermind/nethermind/src/Nethermind/Nethermind.Blockchain.Test/Find/output.bin");

            var logFilter = AllBlockFilter().WithAddress(TestItem.AddressA).FromBlock(1510030).ToBlock(1510031).Build();

            var blocks = _logFinder.FindLogs(logFilter).ToArray();
            Assert.That(blocks.Length, Is.EqualTo(10));
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
            StoreLogIndex();
            var logs = _logFinder.FindLogs(filter).ToArray();
            logs.Length.Should().Be(expectedCount);
        }

        private static FilterBuilder AllBlockFilter() => FilterBuilder.New().FromEarliestBlock().ToPendingBlock();

        private void StoreLogIndex()
        {

            _logIndexStorage.StoreLogIndex(TestItem.AddressA, 1);
            _logIndexStorage.StoreLogIndex(TestItem.AddressB, 4);
            _logIndexStorage.StoreLogIndex(TestItem.AddressC, 4);
            _logIndexStorage.StoreLogIndex(TestItem.AddressD, 4);
        }

    }
}
