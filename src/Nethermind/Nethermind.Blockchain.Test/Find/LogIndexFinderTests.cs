

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
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
            var filterBuilder = AllBlockFilter();
            filterBuilder = addresses.Length == 1 ? filterBuilder.WithAddress(addresses[0]) : filterBuilder.WithAddresses(addresses);
            var logFilter = filterBuilder.Build();

            var logs = _logFinder.FindLogs(logFilter).ToArray();

            logs.Length.Should().Be(expectedCount);
        }

        private static FilterBuilder AllBlockFilter() => FilterBuilder.New().FromEarliestBlock().ToPendingBlock();

    }
}
