// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
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
        [Test]
        public void GasPriceEstimate_NoChangeInHeadBlock_ReturnsPreviousGasPrice()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            Block testHeadBlock = Build.A.Block.Genesis.TestObject;
            GasPriceOracle testGasPriceOracle = new(blockFinder, specProvider, LimboLogs.Instance)
            {
                _gasPriceEstimation = new(testHeadBlock.Hash, 7)
            };

            UInt256 result = testGasPriceOracle.GetGasPriceEstimate();

            result.Should().Be((UInt256)7);
        }

        [TestCase(null)]
        [TestCase(100ul)]
        public void GasPriceEstimate_IfPreviousGasPriceDoesNotExist_ShouldBeEmptyPrice(ulong? gasPrice)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            GasPriceOracle testGasPriceOracle = new(blockFinder, specProvider, LimboLogs.Instance, gasPrice);

            UInt256 estimate = testGasPriceOracle.GetGasPriceEstimate();
            UInt256 expectedGasPrice = 110 * (gasPrice ?? 1.Wei()) / 100;
            estimate.Should().BeEquivalentTo(expectedGasPrice);
        }

        [TestCase(3)]
        [TestCase(10)]
        public void GasPriceEstimate_IfPreviousGasPriceExists_ShouldEqualLastGasPrice(int lastGasPrice)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            GasPriceOracle testGasPriceOracle = new(blockFinder, specProvider, LimboLogs.Instance)
            {
                _gasPriceEstimation = new(null, (UInt256)lastGasPrice)
            };

            UInt256 estimate = testGasPriceOracle.GetGasPriceEstimate();

            estimate.Should().BeEquivalentTo((UInt256?)lastGasPrice);
        }

        [TestCase(null)]
        [TestCase(100ul)]
        public void GasPriceEstimate_EmptyChain_BaseFeeIncluded(ulong? gasPrice)
        {
            UInt256 baseFeePerGas = 10.GWei();
            Block headBlock = Build.A.Block.WithBaseFeePerGas(baseFeePerGas).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0).Returns(headBlock);
            blockFinder.Head.Returns(headBlock);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            GasPriceOracle testGasPriceOracle = new(blockFinder, specProvider, LimboLogs.Instance, gasPrice);

            UInt256 estimate = testGasPriceOracle.GetGasPriceEstimate();
            estimate.Should().Be((baseFeePerGas + (gasPrice ?? 1.Wei())) * 110 / 100);
        }

        [Test]
        public void GasPriceEstimate_IfCalculatedGasPriceGreaterThanMax_MaxGasPriceReturned()
        {
            Transaction tx = Build.A.Transaction.WithGasPrice(501.GWei()).TestObject;
            Block headBlock = Build.A.Block.WithTransactions(tx).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0).Returns(headBlock);
            blockFinder.Head.Returns(headBlock);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            GasPriceOracle testGasPriceOracle = new(blockFinder, specProvider, LimboLogs.Instance);

            UInt256 result = testGasPriceOracle.GetGasPriceEstimate();

            result.Should().Be(500.GWei());
        }

        [Test]
        public void GetGasPricesFromRecentBlocks_IfEightBlocksWithTwoTransactions_CheckEightBlocks()
        {
            int blockNumber = 8;
            IBlockFinder blockFinder = BuildTree(blockNumber);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            GasPriceOracle testGasPriceOracle = new(blockFinder, specProvider, LimboLogs.Instance);

            testGasPriceOracle.GetGasPriceEstimate();

            foreach (long receivedBlockNumber in Enumerable.Range(0, blockNumber + 1))
            {
                blockFinder.Received(1).FindBlock(Arg.Is<long>(l => l == receivedBlockNumber));
            }
        }

        private IBlockFinder BuildTree(int maxBlock)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Transaction tx = Build.A.Transaction.TestObject;
            Block blockWithTwoTx = Build.A.Block.WithTransactions(tx, tx).TestObject;
            for (int i = 0; i <= maxBlock; i++)
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

            foreach (long receivedBlockNumber in Enumerable.Range(3, 5))
            {
                blockFinder.Received(1).FindBlock(Arg.Is<long>(l => l == receivedBlockNumber));
            }

            blockFinder.DidNotReceive().FindBlock(Arg.Is<long>(l => l <= 2));
        }

        private IBlockFinder GetBlockFinderForLastFiveBlocksWithThreeTxAndFirstFourWithOne()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Transaction tx = Build.A.Transaction.TestObject;
            Block blockWithOneTx = Build.A.Block.WithTransactions(Enumerable.Repeat(tx, 1).ToArray()).TestObject;
            Block blockWithThreeTx = Build.A.Block.WithTransactions(Enumerable.Repeat(tx, 3).ToArray()).TestObject;
            for (int i = 0; i < 4; i++)
            {
                blockFinder.FindBlock(i).Returns(blockWithOneTx);
            }
            for (int i = 4; i <= 8; i++)
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

            act.Should().NotThrow();
        }

        [Test]
        public void GetGasPricesFromRecentBlocks_IfBlockHasMoreThanThreeValidTx_AddOnlyThreeNew()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Transaction tx = Build.A.Transaction.WithGasPrice(2).TestObject;
            Block headBlock = Build.A.Block.Genesis.WithTransactions(tx, tx, tx, tx, tx).TestObject;
            blockFinder.FindHeadBlock().Returns(headBlock);
            blockFinder.FindBlock(0).Returns(headBlock);
            GasPriceOracle testGasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>(), LimboLogs.Instance);

            IEnumerable<UInt256> results = testGasPriceOracle.GetSortedGasPricesFromRecentBlocks(0);

            results.Count().Should().Be(3);
        }

        [Test]
        public void GetGasPricesFromRecentBlocks_IfBlockHasMoreThanThreeValidTxs_OnlyAddTxsWithLowestGasPrices()
        {
            Block testBlock = Build.A.Block.WithTransactions(GetFiveTransactionsWithDifferentGasPrices()).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0).Returns(testBlock);
            GasPriceOracle testGasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>(), LimboLogs.Instance);
            List<UInt256> expected = new() { 2, 3, 4 };

            IEnumerable<UInt256> results = testGasPriceOracle.GetSortedGasPricesFromRecentBlocks(0);

            results.Should().BeEquivalentTo(expected);
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
            blockFinder.FindBlock(0).Returns(block);
            GasPriceOracle gasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>(), LimboLogs.Instance);
            List<UInt256> expected = new() { 8, 9 };

            IEnumerable<UInt256> results = gasPriceOracle.GetSortedGasPricesFromRecentBlocks(0);
            results.Should().BeEquivalentTo(expected);
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
            blockFinder.FindBlock(0).Returns(eip1559Block);
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(eip1559Enabled), LimboLogs.Instance);

            IEnumerable<UInt256> results = gasPriceOracle.GetSortedGasPricesFromRecentBlocks(0);

            List<UInt256> expectedList = expected.Select(n => (UInt256)n).ToList();
            results.Should().BeEquivalentTo(expectedList);
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
            blockFinder.FindBlock(0).Returns(nonEip1559Block);
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(eip1559Enabled), LimboLogs.Instance);

            IEnumerable<UInt256> results = gasPriceOracle.GetSortedGasPricesFromRecentBlocks(0);

            List<UInt256> expectedList = expected.Select(n => (UInt256)n).ToList();
            results.Should().BeEquivalentTo(expectedList);
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
            blockFinder.FindBlock(0).Returns(block);
            GasPriceOracle gasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>(), LimboLogs.Instance) { _gasPriceEstimation = new(null, 7) };
            List<UInt256> expected = new() { 7 };

            IEnumerable<UInt256> results = gasPriceOracle.GetSortedGasPricesFromRecentBlocks(0);

            results.ToList().Should().BeEquivalentTo(expected);
        }

        [Test]
        public void AddValidTxAndReturnCount_IfNoTxsInABlock_DefaultPriceAddedToListInstead()
        {
            Transaction[] transactions = Array.Empty<Transaction>();
            Block block = Build.A.Block.Genesis.WithTransactions(transactions).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0).Returns(block);
            GasPriceOracle gasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>(), LimboLogs.Instance) { _gasPriceEstimation = new(null, 7) };
            List<UInt256> expected = new() { 7 };

            IEnumerable<UInt256> results = gasPriceOracle.GetSortedGasPricesFromRecentBlocks(0);

            results.ToList().Should().BeEquivalentTo(expected);
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
            blockFinder.FindBlock(0).Returns(testBlock);
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(false), LimboLogs.Instance);
            List<UInt256> expected = new() { 2, 3 };

            IEnumerable<UInt256> results = gasPriceOracle.GetSortedGasPricesFromRecentBlocks(0);

            results.Should().BeEquivalentTo(expected);
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
            blockFinder.FindBlock(0).Returns(testBlock);
            GasPriceOracle gasPriceOracle = new(blockFinder, GetSpecProviderWithEip1559EnabledAs(true), LimboLogs.Instance);
            List<UInt256> expected = new() { 3, 4 };

            IEnumerable<UInt256> results = gasPriceOracle.GetSortedGasPricesFromRecentBlocks(0);

            results.Should().BeEquivalentTo(expected);
        }
    }
}
