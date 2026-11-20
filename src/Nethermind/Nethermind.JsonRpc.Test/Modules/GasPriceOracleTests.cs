// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class GasPriceOracleTests
    {
        [TestCaseSource(nameof(SelectKthSmallestInPlace_Cases))]
        public void SelectKthSmallestInPlace_ReturnsCorrectKthElement(uint[] values, int k)
        {
            List<UInt256> list = values.Select(static v => (UInt256)v).ToList();
            UInt256 expected = values.OrderBy(static v => v).Select(static v => (UInt256)v).ElementAt(k);

            UInt256 result = GasPriceOracle.SelectKthSmallestInPlace(list, k);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(expected));
                Assert.That(list, Has.Count.EqualTo(values.Length));
            }
            Assert.That(list, Is.EquivalentTo(values.Select(static v => (UInt256)v))); // ensures mutation doesn't lose/duplicate items
        }

        [TestCaseSource(nameof(SelectKthSmallestInPlace_InvalidKCases))]
        public void SelectKthSmallestInPlace_InvalidK_Throws(uint[] values, int k)
        {
            List<UInt256> list = values.Select(static v => (UInt256)v).ToList();

            Action act = () => GasPriceOracle.SelectKthSmallestInPlace(list, k);

            Assert.That(act, Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        private static IEnumerable<TestCaseData> SelectKthSmallestInPlace_Cases()
        {
            // single element
            yield return new TestCaseData(new uint[] { 7 }, 0).SetName("SelectKthSmallestInPlace_SingleElement_k0");

            // two elements
            yield return new TestCaseData(new uint[] { 2, 1 }, 0).SetName("SelectKthSmallestInPlace_TwoElements_k0");
            yield return new TestCaseData(new uint[] { 2, 1 }, 1).SetName("SelectKthSmallestInPlace_TwoElements_kLast");

            // already sorted
            yield return new TestCaseData(new uint[] { 1, 2, 3, 4, 5 }, 0).SetName("SelectKthSmallestInPlace_Sorted_k0");
            yield return new TestCaseData(new uint[] { 1, 2, 3, 4, 5 }, 4).SetName("SelectKthSmallestInPlace_Sorted_kLast");
            yield return new TestCaseData(new uint[] { 1, 2, 3, 4, 5 }, 2).SetName("SelectKthSmallestInPlace_Sorted_kMid");

            // reverse sorted (forces lots of swaps)
            yield return new TestCaseData(new uint[] { 5, 4, 3, 2, 1 }, 0).SetName("SelectKthSmallestInPlace_Reverse_k0");
            yield return new TestCaseData(new uint[] { 5, 4, 3, 2, 1 }, 4).SetName("SelectKthSmallestInPlace_Reverse_kLast");
            yield return new TestCaseData(new uint[] { 5, 4, 3, 2, 1 }, 2).SetName("SelectKthSmallestInPlace_Reverse_kMid");

            // duplicates (partition uses < pivot, so equal elements are a good edge case)
            yield return new TestCaseData(new uint[] { 7, 7, 7, 7 }, 0).SetName("SelectKthSmallestInPlace_AllEqual_k0");
            yield return new TestCaseData(new uint[] { 7, 7, 7, 7 }, 3).SetName("SelectKthSmallestInPlace_AllEqual_kLast");
            yield return new TestCaseData(new uint[] { 5, 1, 5, 3, 5, 2 }, 2).SetName("SelectKthSmallestInPlace_Duplicates_kMid");

            // pivot-in-the-middle style layouts (median-of-range pivot)
            yield return new TestCaseData(new uint[] { 9, 1, 8, 2, 7, 3, 6, 4, 5 }, 4).SetName("SelectKthSmallestInPlace_ZigZag_kMid");
            yield return new TestCaseData(new uint[] { 100, 1, 50, 2, 49, 3, 48, 4 }, 3).SetName("SelectKthSmallestInPlace_ZigZagEven_kMid");
        }

        private static IEnumerable<TestCaseData> SelectKthSmallestInPlace_InvalidKCases()
        {
            yield return new TestCaseData(Array.Empty<uint>(), 0).SetName("SelectKthSmallestInPlace_Empty_Throws");
            yield return new TestCaseData(new uint[] { 1 }, -1).SetName("SelectKthSmallestInPlace_kNegative_Throws");
            yield return new TestCaseData(new uint[] { 1 }, 1).SetName("SelectKthSmallestInPlace_kEqualsCount_Throws");
            yield return new TestCaseData(new uint[] { 1, 2 }, 2).SetName("SelectKthSmallestInPlace_kGreaterThanLast_Throws");
        }

        [Test]
        public async ValueTask GasPriceEstimate_NoChangeInHeadBlock_ReturnsPreviousGasPrice()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            Block testHeadBlock = Build.A.Block.Genesis.TestObject;
            GasPriceOracle testGasPriceOracle = new(blockFinder, specProvider, LimboLogs.Instance)
            {
                _gasPriceEstimation = new(testHeadBlock.Hash, 7)
            };

            UInt256 result = await testGasPriceOracle.GetGasPriceEstimate();

            Assert.That(result, Is.EqualTo((UInt256)7));
        }

        [Test]
        public async ValueTask GasPriceEstimate_IfPreviousGasPriceDoesNotExist_ShouldBeEmptyPrice([Values(null, 100ul)] ulong? gasPrice)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            GasPriceOracle testGasPriceOracle = new(blockFinder, specProvider, LimboLogs.Instance, gasPrice);

            UInt256 estimate = await testGasPriceOracle.GetGasPriceEstimate();
            UInt256 expectedGasPrice = 110 * (gasPrice ?? 1.Wei) / 100;
            Assert.That(estimate, Is.EqualTo(expectedGasPrice));
        }

        [Test]
        public async ValueTask GasPriceEstimate_IfPreviousGasPriceExists_ShouldEqualLastGasPrice([Values(3, 10)] int lastGasPrice)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            GasPriceOracle testGasPriceOracle = new(blockFinder, specProvider, LimboLogs.Instance)
            {
                _gasPriceEstimation = new(null, (UInt256)lastGasPrice)
            };

            UInt256 estimate = await testGasPriceOracle.GetGasPriceEstimate();

            Assert.That(estimate, Is.EqualTo((UInt256?)lastGasPrice));
        }

        [Test]
        public async ValueTask GasPriceEstimate_EmptyChain_BaseFeeIncluded([Values(null, 100ul)] ulong? gasPrice)
        {
            UInt256 baseFeePerGas = 10.GWei;
            Block headBlock = Build.A.Block.WithBaseFeePerGas(baseFeePerGas).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0UL).Returns(headBlock);
            blockFinder.Head.Returns(headBlock);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            GasPriceOracle testGasPriceOracle = new(blockFinder, specProvider, LimboLogs.Instance, gasPrice);

            UInt256 estimate = await testGasPriceOracle.GetGasPriceEstimate();
            Assert.That(estimate, Is.EqualTo((baseFeePerGas + (gasPrice ?? 1.Wei)) * 110 / 100));
        }

        [Test]
        public async ValueTask GasPriceEstimate_IfCalculatedGasPriceGreaterThanMax_MaxGasPriceReturned()
        {
            Transaction tx = Build.A.Transaction.WithGasPrice(501.GWei).TestObject;
            Block headBlock = Build.A.Block.WithTransactions(tx).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0UL).Returns(headBlock);
            blockFinder.Head.Returns(headBlock);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            GasPriceOracle testGasPriceOracle = new(blockFinder, specProvider, LimboLogs.Instance);

            UInt256 result = await testGasPriceOracle.GetGasPriceEstimate();

            Assert.That(result, Is.EqualTo(500.GWei));
        }

        /// <summary>
        /// Blocks 0..20 each carry three legacy transactions paying their base fee plus 1 gwei. The 60th percentile of the
        /// sampled blocks 1..20 is 14 gwei, which with a base fee rising 1 gwei per block is below the head's 21 gwei, so
        /// the estimate is floored at the head base fee plus the minimum gas price after London.
        /// </summary>
        [TestCase(true, true, 21_000_000_001ul, TestName = "GasPriceEstimate_RisingBaseFee_FlooredAtHeadBaseFee")]
        [TestCase(false, true, 11_000_000_000ul, TestName = "GasPriceEstimate_FlatBaseFee_Unchanged")]
        [TestCase(true, false, 14_000_000_000ul, TestName = "GasPriceEstimate_RisingBaseFeeBeforeLondon_Unchanged")]
        public async Task GasPriceEstimate_FloorsAtHeadBaseFeeAfterLondon(bool risingBaseFee, bool eip1559Enabled, ulong expected)
        {
            const ulong headNumber = 20;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Block? block = null;
            for (ulong number = 0; number <= headNumber; number++)
            {
                UInt256 baseFee = risingBaseFee ? (number + 1) * 1.GWei : 10.GWei;
                Transaction[] transactions = [.. Enumerable.Range(0, 3).Select(_ => Build.A.Transaction.WithGasPrice(baseFee + 1.GWei).TestObject)];
                block = Build.A.Block.WithNumber(number).WithBaseFeePerGas(baseFee).WithTransactions(transactions).TestObject;
                blockFinder.FindBlock(number).Returns(block);
            }

            blockFinder.Head.Returns(block);
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(eip1559Enabled), LimboLogs.Instance);

            UInt256 estimate = await gasPriceOracle.GetGasPriceEstimate();

            Assert.That(estimate, Is.EqualTo((UInt256)expected));
        }

        [Test]
        public void GetGasPricesFromRecentBlocks_IfEightBlocksWithTwoTransactions_CheckEightBlocks()
        {
            ulong blockNumber = 8;
            IBlockFinder blockFinder = BuildTree(blockNumber);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            GasPriceOracle testGasPriceOracle = new(blockFinder, specProvider, LimboLogs.Instance);

            testGasPriceOracle.GetGasPriceEstimate();

            foreach (ulong receivedBlockNumber in TestRange.ULongRange(0, blockNumber + 1))
            {
                blockFinder.Received(1).FindBlock(Arg.Is<ulong>(l => l == receivedBlockNumber));
            }
        }

        private IBlockFinder BuildTree(ulong maxBlock)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Transaction tx = Build.A.Transaction.TestObject;
            Block blockWithTwoTx = Build.A.Block.WithTransactions(tx, tx).TestObject;
            for (ulong i = 0; i <= maxBlock; i++)
            {
                blockFinder.FindBlock(i).Returns(blockWithTwoTx);
            }

            blockFinder.Head.Returns(Build.A.Block.WithNumber(maxBlock).TestObject);

            return blockFinder;
        }

        [Test]
        public void GetGasPricesFromRecentBlocks_IfLastFiveBlocksWithThreeTxAndFirstFourWithOne_CheckSixBlocks()
        {
            IBlockFinder blockFinder = GetBlockFinderForLastFiveBlocksWithThreeTxAndFirstFourWithOne();
            GasPriceOracle testGasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>(), LimboLogs.Instance)
            {
                IgnoreUnder = 1,
                BlockLimit = 6
            };

            testGasPriceOracle.GetGasPriceEstimate();

            foreach (ulong receivedBlockNumber in TestRange.ULongRange(3, 5))
            {
                blockFinder.Received(1).FindBlock(Arg.Is<ulong>(l => l == receivedBlockNumber));
            }

            blockFinder.DidNotReceive().FindBlock(Arg.Is<ulong>(l => l <= 2));
        }

        private IBlockFinder GetBlockFinderForLastFiveBlocksWithThreeTxAndFirstFourWithOne()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Transaction tx = Build.A.Transaction.TestObject;
            Block blockWithOneTx = Build.A.Block.WithTransactions(Enumerable.Repeat(tx, 1).ToArray()).TestObject;
            Block blockWithThreeTx = Build.A.Block.WithTransactions(Enumerable.Repeat(tx, 3).ToArray()).TestObject;
            for (ulong i = 0; i < 4; i++)
            {
                blockFinder.FindBlock(i).Returns(blockWithOneTx);
            }
            for (ulong i = 4; i <= 8; i++)
            {
                blockFinder.FindBlock(i).Returns(blockWithThreeTx);
            }

            blockFinder.Head.Returns(Build.A.Block.WithNumber(8).TestObject);

            return blockFinder;
        }

        [Test]
        public void GetGasPriceEstimate_IfLastGasPriceIsNull_WillNotReturnLastGasPrice()
        {
            GasPriceOracle testGasPriceOracle = new(Substitute.For<IBlockFinder>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance);

            Action act = () => testGasPriceOracle.GetGasPriceEstimate();

            Assert.That(act, Throws.Nothing);
        }

        [Test]
        public void GetGasPricesFromRecentBlocks_IfBlockHasMoreThanThreeValidTx_AddOnlyThreeNew()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Transaction tx = Build.A.Transaction.WithGasPrice(2).TestObject;
            Block headBlock = Build.A.Block.Genesis.WithTransactions(tx, tx, tx, tx, tx).TestObject;
            blockFinder.FindHeadBlock().Returns(headBlock);
            blockFinder.FindBlock(0UL).Returns(headBlock);
            GasPriceOracle testGasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>(), LimboLogs.Instance);

            IEnumerable<UInt256> results = testGasPriceOracle.GetGasPricesFromRecentBlocks(0);

            Assert.That(results.Count(), Is.EqualTo(3));
        }

        [Test]
        public void GetGasPricesFromRecentBlocks_IfBlockHasMoreThanThreeValidTxs_OnlyAddTxsWithLowestGasPrices()
        {
            Block testBlock = Build.A.Block.WithTransactions(GetFiveTransactionsWithDifferentGasPrices()).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0UL).Returns(testBlock);
            GasPriceOracle testGasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>(), LimboLogs.Instance);
            List<UInt256> expected = [2, 3, 4];

            IEnumerable<UInt256> results = testGasPriceOracle.GetGasPricesFromRecentBlocks(0);

            Assert.That(results, Is.EquivalentTo(expected));
        }

        [Test]
        public async Task GetGasPricesFromRecentBlocks_IfBlockHasMoreThanThreeValidTxs_ReturnsLowestGasPricesInAscendingOrder()
        {
            Transaction[] transactions =
            [
                Build.A.Transaction.WithGasPrice(6).TestObject,
                Build.A.Transaction.WithGasPrice(3).TestObject,
                Build.A.Transaction.WithGasPrice(4).TestObject,
                Build.A.Transaction.WithGasPrice(1).TestObject,
                Build.A.Transaction.WithGasPrice(3).TestObject,
                Build.A.Transaction.WithGasPrice(5).TestObject
            ];
            await using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
            IBlockTree blockTree = container.Resolve<IBlockTree>();
            Block genesis = Build.A.Block.Genesis.TestObject;
            Block testBlock = Build.A.Block.WithParent(genesis).WithTransactions(transactions).TestObject;
            foreach (Block block in (Block[])[genesis, testBlock])
            {
                blockTree.SuggestBlock(block);
                blockTree.TryUpdateMainChain(block.Header, wereProcessed: true, forceUpdateHeadBlock: true, block);
            }

            GasPriceOracle testGasPriceOracle = (GasPriceOracle)container.Resolve<IGasPriceOracle>();

            IEnumerable<UInt256> results = testGasPriceOracle.GetGasPricesFromRecentBlocks(testBlock.Number);

            Assert.That(results.Take(EthGasPriceConstants.TxLimitFromABlock), Is.EqualTo((UInt256[])[3, 3, 4]), "the empty genesis block after it adds its own price");
        }

        private Transaction[] GetFiveTransactionsWithDifferentGasPrices()
        {
            Transaction[] transactions =
            {
                Build.A.Transaction.WithGasPrice(6).TestObject,
                Build.A.Transaction.WithGasPrice(2).TestObject,
                Build.A.Transaction.WithGasPrice(4).TestObject,
                Build.A.Transaction.WithGasPrice(5).TestObject,
                Build.A.Transaction.WithGasPrice(3).TestObject
            };
            return transactions;
        }

        [Test]
        public void GetGasPricesFromRecentBlocks_TxsSentByMiner_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            Address minerAddress = TestItem.PrivateKeyA.Address;
            Transaction[] transactions =
            {
                Build.A.Transaction.WithGasPrice(7).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(0).TestObject,
                Build.A.Transaction.WithGasPrice(8).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(0).TestObject,
                Build.A.Transaction.WithGasPrice(9).SignedAndResolved(TestItem.PrivateKeyC).WithNonce(0).TestObject,
            };
            Block block = Build.A.Block.Genesis.WithBeneficiary(minerAddress).WithTransactions(transactions).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0UL).Returns(block);
            GasPriceOracle gasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>(), LimboLogs.Instance);
            List<UInt256> expected = [8, 9];

            IEnumerable<UInt256> results = gasPriceOracle.GetGasPricesFromRecentBlocks(0);
            Assert.That(results, Is.EquivalentTo(expected));
        }

        [TestCase(true, new ulong[] { 26, 27, 27 })]
        [TestCase(false, new ulong[] { 25, 26, 27 })]
        public void AddValidTxAndReturnCount_GivenEip1559Txs_EffectiveGasPriceProperlyCalculated(bool eip1559Enabled, ulong[] expected)
        {
            Transaction[] eip1559TxGroup = {
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(25).WithType(TxType.EIP1559).TestObject,
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(26).WithType(TxType.EIP1559).TestObject,
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(27).WithType(TxType.EIP1559).TestObject
            };
            Block eip1559Block = Build.A.Block.Genesis.WithTransactions(eip1559TxGroup).WithBaseFeePerGas(1).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0UL).Returns(eip1559Block);
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(eip1559Enabled), LimboLogs.Instance);

            IEnumerable<UInt256> results = gasPriceOracle.GetGasPricesFromRecentBlocks(0);

            List<UInt256> expectedList = expected.Select(static n => (UInt256)n).ToList();
            Assert.That(results, Is.EquivalentTo(expectedList));
        }

        [TestCase(true, new ulong[] { 25, 26, 27 })]
        [TestCase(false, new ulong[] { 25, 26, 27 })]
        public void AddValidTxAndReturnCount_GivenNonEip1559Txs_EffectiveGasPriceProperlyCalculated(bool eip1559Enabled, ulong[] expected)
        {
            Transaction[] nonEip1559TxGroup = {
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(25).TestObject,
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(26).TestObject,
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(27).TestObject
            };
            Block nonEip1559Block = Build.A.Block.Genesis.WithTransactions(nonEip1559TxGroup).WithBaseFeePerGas(1).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0UL).Returns(nonEip1559Block);
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(eip1559Enabled), LimboLogs.Instance);

            IEnumerable<UInt256> results = gasPriceOracle.GetGasPricesFromRecentBlocks(0);

            List<UInt256> expectedList = expected.Select(static n => (UInt256)n).ToList();
            Assert.That(results, Is.EquivalentTo(expectedList));
        }

        public static ISpecProvider GetSpecProviderWithEip1559EnabledAs(bool isEip1559) =>
            new CustomSpecProvider(((ForkActivation)0, isEip1559 ? London.Instance : Berlin.Instance));

        [Test]
        public void GetGasPricesFromRecentBlocks_IfNoValidTxsInABlock_DefaultPriceAddedToListInstead()
        {
            Transaction[] transactions =
            {
                Build.A.Transaction.WithGasPrice(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                Build.A.Transaction.WithGasPrice(2).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                Build.A.Transaction.WithGasPrice(3).SignedAndResolved(TestItem.PrivateKeyA).TestObject
            };
            Block block = Build.A.Block.Genesis.WithTransactions(transactions).WithBeneficiary(TestItem.PrivateKeyA.Address).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0UL).Returns(block);
            GasPriceOracle gasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>(), LimboLogs.Instance) { _gasPriceEstimation = new(null, 7) };
            List<UInt256> expected = [7];

            IEnumerable<UInt256> results = gasPriceOracle.GetGasPricesFromRecentBlocks(0);

            Assert.That(results.ToList(), Is.EquivalentTo(expected));
        }

        [Test]
        public void AddValidTxAndReturnCount_IfNoTxsInABlock_DefaultPriceAddedToListInstead()
        {
            Transaction[] transactions = [];
            Block block = Build.A.Block.Genesis.WithTransactions(transactions).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0UL).Returns(block);
            GasPriceOracle gasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>(), LimboLogs.Instance) { _gasPriceEstimation = new(null, 7) };
            List<UInt256> expected = [7];

            IEnumerable<UInt256> results = gasPriceOracle.GetGasPricesFromRecentBlocks(0);

            Assert.That(results.ToList(), Is.EquivalentTo(expected));
        }

        [Test]
        public void AddValidTxAndReturnCount_Eip1559NotEnabled_EffectiveGasPricesShouldBeMoreThanIgnoreUnder()
        {
            Transaction[] transactions =
            {
                Build.A.Transaction.WithGasPrice(1).TestObject, //1
                Build.A.Transaction.WithGasPrice(2).WithType(TxType.EIP1559).TestObject, //2
                Build.A.Transaction.WithGasPrice(3).WithType(TxType.EIP1559).TestObject, //3
            };
            Block testBlock = Build.A.Block.Genesis.WithBaseFeePerGas(1).WithTransactions(transactions).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0UL).Returns(testBlock);
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(false), LimboLogs.Instance);
            List<UInt256> expected = [2, 3];

            IEnumerable<UInt256> results = gasPriceOracle.GetGasPricesFromRecentBlocks(0);

            Assert.That(results, Is.EquivalentTo(expected));
        }

        [Test]
        public void AddValidTxAndReturnCount_Eip1559Enabled_EffectiveGasPricesShouldBeMoreThanIgnoreUnder()
        {
            Transaction[] transactions =
            {
                Build.A.Transaction.WithMaxFeePerGas(3).WithGasPrice(1).TestObject, //Min(1, 1+1) => 1
                Build.A.Transaction.WithMaxFeePerGas(4).WithGasPrice(2).WithType(TxType.EIP1559).TestObject, //Min(4, 2 + 1) => 3
                Build.A.Transaction.WithMaxFeePerGas(5).WithGasPrice(3).WithType(TxType.EIP1559).TestObject, //Min(5, 3 + 1) => 4
            };
            Block testBlock = Build.A.Block.Genesis.WithBaseFeePerGas(1).WithTransactions(transactions).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0UL).Returns(testBlock);
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(true), LimboLogs.Instance);
            List<UInt256> expected = [3, 4];

            IEnumerable<UInt256> results = gasPriceOracle.GetGasPricesFromRecentBlocks(0);

            Assert.That(results, Is.EquivalentTo(expected));
        }

        [TestCase(null, 1UL)]
        [TestCase(5UL, 5UL)]
        public void GetMaxPriorityGasFeeEstimate_IfNoTipAboveIgnoreUnder_DoesNotUseGasPriceEstimate(ulong? lastMaxPriorityFeePerGas, ulong expected)
        {
            Transaction[] transactions =
            [
                Build.A.Transaction.WithMaxFeePerGas(100.GWei).WithMaxPriorityFeePerGas(1).WithType(TxType.EIP1559).TestObject,
                Build.A.Transaction.WithMaxFeePerGas(100.GWei).WithMaxPriorityFeePerGas(1).WithType(TxType.EIP1559).TestObject
            ];
            Block headBlock = Build.A.Block.Genesis.WithTransactions(transactions).WithBaseFeePerGas(10.GWei).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0UL).Returns(headBlock);
            blockFinder.Head.Returns(headBlock);
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(true), LimboLogs.Instance)
            {
                _gasPriceEstimation = new(null, 100.GWei),
                _maxPriorityFeePerGasEstimation = new(null, lastMaxPriorityFeePerGas)
            };

            UInt256 estimate = gasPriceOracle.GetMaxPriorityGasFeeEstimate();

            Assert.That(estimate, Is.EqualTo((UInt256)expected));
        }

        [TestCase(null, 20UL)]
        [TestCase(25UL, 25UL)]
        public void GetMaxPriorityGasFeeEstimate_IfSomeBlocksHaveNoTips_SubstituteIsSampledAlongsideTheTips(ulong? lastMaxPriorityFeePerGas, ulong expected)
        {
            Transaction[] transactions =
            [
                Build.A.Transaction.WithMaxFeePerGas(100).WithMaxPriorityFeePerGas(10).WithType(TxType.EIP1559).TestObject,
                Build.A.Transaction.WithMaxFeePerGas(100).WithMaxPriorityFeePerGas(20).WithType(TxType.EIP1559).TestObject,
                Build.A.Transaction.WithMaxFeePerGas(100).WithMaxPriorityFeePerGas(30).WithType(TxType.EIP1559).TestObject
            ];
            Block headBlock = Build.A.Block.WithNumber(1).WithTransactions(transactions).WithBaseFeePerGas(10).TestObject;
            Block blockWithoutTransactions = Build.A.Block.Genesis.WithBaseFeePerGas(10).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(1UL).Returns(headBlock);
            blockFinder.FindBlock(0UL).Returns(blockWithoutTransactions);
            blockFinder.Head.Returns(headBlock);
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(true), LimboLogs.Instance)
            {
                _gasPriceEstimation = new(null, 100),
                _maxPriorityFeePerGasEstimation = new(null, lastMaxPriorityFeePerGas)
            };

            UInt256 estimate = gasPriceOracle.GetMaxPriorityGasFeeEstimate();

            Assert.That(estimate, Is.EqualTo((UInt256)expected));
        }

        [Test]
        public void GetMaxPriorityGasFeeEstimate_EmptyChain_BaseFeeNotIncluded([Values(null, 100ul)] ulong? minGasPrice)
        {
            Block headBlock = Build.A.Block.WithBaseFeePerGas(10.GWei).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0UL).Returns(headBlock);
            blockFinder.Head.Returns(headBlock);
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(true), LimboLogs.Instance, minGasPrice);

            UInt256 estimate = gasPriceOracle.GetMaxPriorityGasFeeEstimate();

            Assert.That(estimate, Is.EqualTo(minGasPrice ?? 1.Wei));
        }

        [Test]
        public async ValueTask GasPriceEstimate_IfHeadBodyIsMissing_FallsBackToMinimumGasPrice()
        {
            IBlockFinder blockFinder = GetBlockFinderWithoutBodies();
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(true), LimboLogs.Instance);

            UInt256 estimate = await gasPriceOracle.GetGasPriceEstimate();

            Assert.That(estimate, Is.EqualTo((10.GWei + 1.Wei) * 110 / 100));
        }

        [Test]
        public void GetMaxPriorityGasFeeEstimate_IfHeadBodyIsMissing_FallsBackToMinGasPrice()
        {
            IBlockFinder blockFinder = GetBlockFinderWithoutBodies();
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(true), LimboLogs.Instance);

            UInt256 estimate = gasPriceOracle.GetMaxPriorityGasFeeEstimate();

            Assert.That(estimate, Is.EqualTo(1.Wei));
        }

        private static IEnumerable<TestCaseData> PriceCacheCases()
        {
            PriceCache set = default;
            set.Set(TestItem.KeccakA, 5);
            PriceCache setAgain = set;
            setAgain.Set(TestItem.KeccakA, 6);
            yield return new TestCaseData(default(PriceCache), TestItem.KeccakA, null, null).SetName("PriceCache_Default_MissesWithoutLastPrice");
            yield return new TestCaseData(new PriceCache(null, 7), TestItem.KeccakA, null, (UInt256?)7).SetName("PriceCache_SeededWithoutHead_MissesWithLastPrice");
            yield return new TestCaseData(set, TestItem.KeccakA, (UInt256?)5, (UInt256?)5).SetName("PriceCache_SetHead_Hits");
            yield return new TestCaseData(set, TestItem.KeccakB, null, (UInt256?)5).SetName("PriceCache_OtherHead_Misses");
            // A subclass may refresh the price of the head already stored.
            yield return new TestCaseData(setAgain, TestItem.KeccakA, (UInt256?)6, (UInt256?)6).SetName("PriceCache_SetHeadAgain_HitsTheNewPrice");
        }

        [TestCaseSource(nameof(PriceCacheCases))]
        public void PriceCache_HitsOnlyTheHeadItWasSetFor(PriceCache cache, Hash256 head, UInt256? expectedHit, UInt256? expectedLastPrice)
        {
            bool hit = cache.TryGetPrice(head, out UInt256? price);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(hit, Is.EqualTo(expectedHit is not null));
                Assert.That(price, Is.EqualTo(expectedHit));
                Assert.That(cache.LastPrice, Is.EqualTo(expectedLastPrice));
            }
        }

        /// <remarks>
        /// The oracle's caches are shared by every request thread. A hit must carry the price set for its head, so a reader
        /// between the two stores of a cache that wrote the head and its price apart would get the other head's price, or
        /// none on the first store. Time-boxed: it can only fail where the pair is not published as one.
        /// </remarks>
        [Test]
        public void PriceCache_ConcurrentReaders_NeverPairAHeadWithAnotherHeadsPrice()
        {
            PriceCache cache = default; // captured by the threads, so one shared location, as the oracle's field is
            UInt256 priceA = 1, priceB = 2;
            using CancellationTokenSource stop = new(TimeSpan.FromMilliseconds(300));
            int mispaired = 0;

            void Write()
            {
                while (!stop.IsCancellationRequested)
                {
                    cache.Set(TestItem.KeccakA, priceA);
                    cache.Set(TestItem.KeccakB, priceB);
                }
            }

            void Read()
            {
                while (!stop.IsCancellationRequested)
                {
                    if (cache.TryGetPrice(TestItem.KeccakA, out UInt256? a) && !IsPrice(a, priceA)) Interlocked.Increment(ref mispaired);
                    if (cache.TryGetPrice(TestItem.KeccakB, out UInt256? b) && !IsPrice(b, priceB)) Interlocked.Increment(ref mispaired);
                }
            }

            static bool IsPrice(UInt256? price, UInt256 expected) => price is { } value && value == expected;

            Task.WaitAll(Task.Run(Write), Task.Run(Read), Task.Run(Read));

            Assert.That(mispaired, Is.Zero, "hits that carried another head's price or none");
        }

        /// <summary>Head header is known but every body lookup fails, as on a node whose bodies were pruned.</summary>
        private static IBlockFinder GetBlockFinderWithoutBodies()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.Head.Returns(Build.A.Block.WithNumber(1).WithBaseFeePerGas(10.GWei).TestObject);
            return blockFinder;
        }

        /// <remarks>
        /// The first caller is held in its scan. The others start after it, and the scan is released once each of them has
        /// read the head and then waits or has finished, so each has missed the cache before the first scan publishes:
        /// they wait on the estimate's lock, or without one each runs a scan of its own.
        /// </remarks>
        [Test]
        public void Estimate_ConcurrentMissesOnANewHead_ShareOneScan([Values] bool priorityFee)
        {
            using HeldScan scan = new();
            HeldScan.Caller first = scan.Start(priorityFee);
            bool held = scan.WaitUntilScanHeld();
            HeldScan.Caller[] followers = [scan.Start(priorityFee), scan.Start(priorityFee), scan.Start(priorityFee)];
            bool parked = scan.WaitUntilAllParked();
            int scansWhileHeld = scan.Scans;
            UInt256? newHeadPriceWhileHeld = scan.CachedForNewHead(priorityFee);
            UInt256? lastPriceWhileHeld = scan.Cache(priorityFee).LastPrice;
            scan.Release();
            UInt256?[] estimates = [.. followers.Prepend(first).Select(static caller => caller.Wait())];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(held && parked, Is.True, "the first caller is held in its scan and the others wait or have finished");
                Assert.That(scansWhileHeld, Is.EqualTo(1), "the others scanned while the first scan was held");
                Assert.That(newHeadPriceWhileHeld, Is.Null, "the new head was published before its scan ended");
                Assert.That(lastPriceWhileHeld, Is.EqualTo(HeldScan.PreviousPrice(priorityFee)));
                Assert.That(estimates, Is.All.EqualTo(HeldScan.ExpectedPrice(priorityFee)));
                Assert.That(scan.Scans, Is.EqualTo(1), "one scan for every caller");
                Assert.That(scan.HeadReads, Is.EqualTo(4), "each caller reads the head once");
                Assert.That(scan.CachedForNewHead(priorityFee), Is.EqualTo(HeldScan.ExpectedPrice(priorityFee)));
            }
        }

        /// <remarks>
        /// While a scan is held, calls that do not need it complete: the other estimate on its own cold cache, a cache hit and
        /// a call without a head. A lock shared by the two estimates, or taken before the head or cache checks, holds them up.
        /// </remarks>
        [Test]
        public void Estimate_WhileAScanIsHeld_CallsThatDoNotNeedItComplete([Values] bool priorityFee)
        {
            using HeldScan scan = new();
            HeldScan.Caller first = scan.Start(priorityFee);
            bool held = scan.WaitUntilScanHeld();
            UInt256? otherEstimate = scan.Start(!priorityFee).Wait();
            scan.SetHead(scan.PreviousHead);
            UInt256? cacheHit = scan.Start(priorityFee).Wait();
            scan.SetHead(null);
            UInt256? noHead = scan.Start(priorityFee).Wait();
            bool stillHeld = first.IsRunning;
            scan.Release();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(held && stillHeld, Is.True, "the first scan is held throughout");
                Assert.That(otherEstimate, Is.EqualTo(HeldScan.ExpectedPrice(!priorityFee)), "the other estimate, on its own cold cache");
                Assert.That(cacheHit, Is.EqualTo(HeldScan.PreviousPrice(priorityFee)), "a cache hit");
                Assert.That(noHead, Is.EqualTo(priorityFee ? EthGasPriceConstants.FallbackMaxPriorityFeePerGas : HeldScan.PreviousPrice(false)), "no head");
                Assert.That(first.Wait(), Is.EqualTo(HeldScan.ExpectedPrice(priorityFee)));
                Assert.That(scan.Scans, Is.EqualTo(2), "one scan per estimate");
            }
        }

        /// <remarks>
        /// The held scan throws. Its caller gets the exception, and a caller that waited for it scans again, while the
        /// previous price stays cached until that scan publishes.
        /// </remarks>
        [Test]
        public void Estimate_ScanThrows_WaitingCallerScansAgain([Values] bool priorityFee)
        {
            InvalidOperationException failure = new("The scan failed.");
            using HeldScan scan = new() { FirstScanFailure = failure };
            PriceCache? cacheDuringRetry = null;
            scan.OnLaterScan = () => cacheDuringRetry = scan.Cache(priorityFee);
            HeldScan.Caller first = scan.Start(priorityFee);
            bool held = scan.WaitUntilScanHeld();
            HeldScan.Caller follower = scan.Start(priorityFee);
            bool parked = scan.WaitUntilAllParked();
            scan.Release();
            Exception? firstError = first.WaitForError();
            UInt256? retried = follower.Wait();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(held && parked, Is.True, "the first caller is held in its scan and the other waits or has finished");
                Assert.That(firstError, Is.SameAs(failure), "the first caller gets its scan's exception");
                Assert.That(retried, Is.EqualTo(HeldScan.ExpectedPrice(priorityFee)), "the waiting caller scans again");
                Assert.That(scan.Scans, Is.EqualTo(2));
                Assert.That(cacheDuringRetry?.LastPrice, Is.EqualTo(HeldScan.PreviousPrice(priorityFee)), "the failed scan replaced the previous price");
                Assert.That(cacheDuringRetry?.TryGetPrice(scan.NewHead.Hash!, out _), Is.False, "the failed scan published a price");
                Assert.That(scan.CachedForNewHead(priorityFee), Is.EqualTo(HeldScan.ExpectedPrice(priorityFee)));
            }
        }

        /// <summary>
        /// An oracle on a new head at number 0 whose one legacy transaction pays 20 gwei over a 10 gwei London base fee, so a
        /// scan is one <c>FindBlock</c> call and estimates a 20 gwei gas price and a 10 gwei tip. Both caches still hold the
        /// price of a previous head at the same number. The first <c>FindBlock</c> call waits for <c>Release</c>; later
        /// ones return at once.
        /// </summary>
        private sealed class HeldScan : IDisposable
        {
            private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
            private readonly ManualResetEventSlim _scanHeld = new();
            private readonly ManualResetEventSlim _release = new();
            private readonly List<Caller> _callers = [];
            private Block? _head;
            private int _scans;
            private int _headReads;

            public HeldScan()
            {
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                blockFinder.Head.Returns(_ =>
                {
                    Interlocked.Increment(ref _headReads);
                    return Volatile.Read(ref _head);
                });
                blockFinder.FindBlock(0UL).Returns(_ => Scan());
                _head = NewHead;
                Oracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(true), LimboLogs.Instance)
                {
                    _gasPriceEstimation = new(PreviousHead.Hash, PreviousPrice(priorityFee: false)),
                    _maxPriorityFeePerGasEstimation = new(PreviousHead.Hash, PreviousPrice(priorityFee: true))
                };
            }

            public Block NewHead { get; } = Build.A.Block.Genesis.WithBaseFeePerGas(10.GWei).WithTransactions(Build.A.Transaction.WithGasPrice(20.GWei).TestObject).TestObject;
            public Block PreviousHead { get; } = Build.A.Block.Genesis.WithTimestamp(1).TestObject;
            public GasPriceOracle Oracle { get; }
            public Exception? FirstScanFailure { get; init; }
            public Action? OnLaterScan { get; set; }
            public int Scans => Volatile.Read(ref _scans);
            public int HeadReads => Volatile.Read(ref _headReads);

            public static UInt256 ExpectedPrice(bool priorityFee) => priorityFee ? 10.GWei : 20.GWei;
            public static UInt256 PreviousPrice(bool priorityFee) => priorityFee ? 5.GWei : 7.GWei;

            public Caller Start(bool priorityFee)
            {
                Caller caller = new(Oracle, priorityFee);
                _callers.Add(caller);
                return caller;
            }

            public bool WaitUntilScanHeld() => _scanHeld.Wait(Guard);

            /// <summary>Waits until every caller has read the head and then waits, on a lock or an event, or has finished.</summary>
            public bool WaitUntilAllParked() => SpinWait.SpinUntil(() => HeadReads >= _callers.Count && _callers.All(static caller => caller.IsParked), Guard);

            public void SetHead(Block? head) => Volatile.Write(ref _head, head);
            public void Release() => _release.Set();
            public PriceCache Cache(bool priorityFee) => priorityFee ? Oracle._maxPriorityFeePerGasEstimation : Oracle._gasPriceEstimation;
            public UInt256? CachedForNewHead(bool priorityFee) => Cache(priorityFee).TryGetPrice(NewHead.Hash!, out UInt256? price) ? price : null;

            public void Dispose()
            {
                Release();
                // Only once every caller has finished, so none waits on a disposed event.
                if (_callers.Count(static caller => !caller.Join()) == 0)
                {
                    _scanHeld.Dispose();
                    _release.Dispose();
                }
            }

            private Block Scan()
            {
                if (Interlocked.Increment(ref _scans) > 1)
                {
                    OnLaterScan?.Invoke();
                    return NewHead;
                }

                _scanHeld.Set();
                if (!_release.Wait(TimeSpan.FromMinutes(1)))
                {
                    throw new TimeoutException("The held scan was not released.");
                }

                return FirstScanFailure is null ? NewHead : throw FirstScanFailure;
            }

            /// <summary>One estimate on a thread of its own, whose state tells whether it waits.</summary>
            public sealed class Caller
            {
                private readonly Thread _thread;
                private UInt256 _result;
                private Exception? _error;

                public Caller(GasPriceOracle oracle, bool priorityFee)
                {
                    _thread = new(() =>
                    {
                        try
                        {
                            _result = priorityFee ? oracle.GetMaxPriorityGasFeeEstimate() : oracle.GetGasPriceEstimate().GetAwaiter().GetResult();
                        }
                        catch (Exception exception)
                        {
                            _error = exception;
                        }
                    });
                    _thread.IsBackground = true;
                    _thread.Start();
                }

                public bool IsParked => !_thread.IsAlive || (_thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
                public bool IsRunning => _thread.IsAlive;
                public bool Join() => _thread.Join(Guard);

                /// <returns>The estimate, or <c>null</c> if it is still running.</returns>
                public UInt256? Wait() => !Join() ? null : _error is null ? _result : throw new InvalidOperationException("The estimate failed.", _error);

                /// <returns>The exception the estimate threw, or <c>null</c>.</returns>
                public Exception? WaitForError() => Join() ? _error : null;
            }
        }
    }
}
