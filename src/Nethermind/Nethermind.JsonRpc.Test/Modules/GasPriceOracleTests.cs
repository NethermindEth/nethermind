//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

#nullable enable
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
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    partial class GasPriceOracleTests
    {
        [Test]
        public void GasPriceEstimate_NoChangeInHeadBlock_ReturnsPreviousGasPrice()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Block testHeadBlock = Build.A.Block.Genesis.TestObject;
            GasPriceOracle testGasPriceOracle =
                new(blockFinder, Substitute.For<ISpecProvider>()) {LastHeadBlock = testHeadBlock, LastGasPrice = 7};
            
            UInt256 result = testGasPriceOracle.GetGasPriceEstimate();
            
            result.Should().Be((UInt256) 7);
        }

        [Test]
        public void GasPriceEstimate_IfPreviousGasPriceDoesNotExist_FallbackGasPriceSetToDefaultGasPrice()
        {
            GasPriceOracle testGasPriceOracle = new(Substitute.For<IBlockFinder>(), Substitute.For<ISpecProvider>()){LastGasPrice = null};
            
            testGasPriceOracle.GetGasPriceEstimate();
            
            testGasPriceOracle.FallbackGasPrice.Should().BeEquivalentTo((UInt256?) EthGasPriceConstants.DefaultGasPrice);
        }

        [TestCase(3)]
        [TestCase(10)]
        public void GasPriceEstimate_IfPreviousGasPriceExists_FallbackGasPriceIsSetToPreviousGasPrice(int lastGasPrice)
        {
            GasPriceOracle testGasPriceOracle = new(Substitute.For<IBlockFinder>(), Substitute.For<ISpecProvider>()){LastGasPrice = (UInt256) lastGasPrice};
            
            testGasPriceOracle.GetGasPriceEstimate();
            
            testGasPriceOracle.FallbackGasPrice.Should().BeEquivalentTo((UInt256?) lastGasPrice);
        }

        [Test]
        public void GasPriceEstimate_IfCalculatedGasPriceGreaterThanMax_MaxGasPriceReturned()
        {
            Transaction tx = Build.A.Transaction.WithGasPrice(501.GWei()).TestObject;
            Block headBlock = Build.A.Block.WithTransactions(tx).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0).Returns(headBlock);
            blockFinder.FindHeadBlock().Returns(headBlock);
            GasPriceOracle testGasPriceOracle = new GasPriceOracle(blockFinder, Substitute.For<ISpecProvider>());

            UInt256 result = testGasPriceOracle.GetGasPriceEstimate();
            
            result.Should().Be((UInt256) 500);
        }
        
        [Test]
        public void GetGasPricesFromRecentBlocks_IfEightBlocksWithTwoTransactions_CheckEightBlocks()
        {
            IBlockFinder blockFinder = GetBlockFinderForNineBlocksWithTwoTransactions();
            GasPriceOracle testGasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>());

            testGasPriceOracle.GetGasPricesFromRecentBlocks(8);
            
            long[] receivedBlockNumbers = {1,2,3,4,5,6,7,8};
            receivedBlockNumbers
                .Select(x => blockFinder.Received(1).FindBlock(Arg.Is<long>(l => l == x)));
            blockFinder.DidNotReceive().FindBlock(Arg.Is<long>(l => l == 0));
        }

        private IBlockFinder GetBlockFinderForNineBlocksWithTwoTransactions()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Transaction tx = Build.A.Transaction.TestObject;
            Block blockWithTwoTx = Build.A.Block.WithTransactions(tx, tx).TestObject;
            for (int i = 0; i <= 8; i++)
            {
                blockFinder.FindBlock(i).Returns(blockWithTwoTx);
            }

            blockFinder.FindHeadBlock().Returns(Build.A.Block.WithNumber(8).TestObject);

            return blockFinder;
        }

        [Test]
        public void GetGasPricesFromRecentBlocks_IfLastFiveBlocksWithThreeTxAndFirstFourWithOne_CheckSixBlocks()
        {
            IBlockFinder blockFinder = GetBlockFinderForLastFiveBlocksWithThreeTxAndFirstFourWithOne();
            GasPriceOracle testGasPriceOracle = new GasPriceOracle(blockFinder,Substitute.For<ISpecProvider>());
            
            IEnumerable<UInt256> result = testGasPriceOracle.GetGasPricesFromRecentBlocks(8);


            long[] receivedBlockNumbers = {3,4,5,6,7,8};
            receivedBlockNumbers
                .Select(x => blockFinder.Received(1).FindBlock(Arg.Is<long>(l => l == x)));
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

            blockFinder.FindHeadBlock().Returns(Build.A.Block.WithNumber(8).TestObject);

            return blockFinder;
        }

        [Test]
        public void GetGasPriceEstimate_IfLastGasPriceIsNull_WillNotReturnLastGasPrice()
        {
            GasPriceOracle testGasPriceOracle = new(Substitute.For<IBlockFinder>(), Substitute.For<ISpecProvider>());
            
            Action act = () => testGasPriceOracle.GetGasPriceEstimate();

            act.Should().NotThrow();
        }
        
        
        [Test]
        public void GetGasPricesFromRecentBlocks_IfBlockHasMoreThanThreeValidTx_AddOnlyThreeNew()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Transaction tx = Build.A.Transaction.WithGasPrice(1).TestObject;
            Block headBlock = Build.A.Block.Genesis.WithTransactions(tx,tx,tx,tx,tx).TestObject;
            blockFinder.FindHeadBlock().Returns(headBlock);
            blockFinder.FindBlock(0).Returns(headBlock);
            GasPriceOracle testGasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>());
            
            IEnumerable<UInt256> results = testGasPriceOracle.GetGasPricesFromRecentBlocks(0);

            results.Count().Should().Be(3);
        }
        
        [Test]
        public void GetGasPricesFromRecentBlocks_IfBlockHasMoreThanThreeValidTxs_OnlyAddTxsWithLowestGasPrices()
        {
            Block testBlock = Build.A.Block.WithTransactions(GetFiveTransactionsWithDifferentGasPrices()).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0).Returns(testBlock);
            GasPriceOracle testGasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>());
            List<UInt256> expected = new() {1,2,3};
            
            IEnumerable<UInt256> results = testGasPriceOracle.GetGasPricesFromRecentBlocks(0);

            results.Should().BeEquivalentTo(expected);
        }

        public Transaction[] GetFiveTransactionsWithDifferentGasPrices()
        {
            Transaction[] transactions = 
            {
                Build.A.Transaction.WithGasPrice(1).TestObject,
                Build.A.Transaction.WithGasPrice(2).TestObject,
                Build.A.Transaction.WithGasPrice(4).TestObject,
                Build.A.Transaction.WithGasPrice(5).TestObject,
                Build.A.Transaction.WithGasPrice(3).TestObject
            } ;
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
            GasPriceOracle gasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>());
            List<UInt256> expected = new() {8,9};
            
            IEnumerable<UInt256> results = gasPriceOracle.GetGasPricesFromRecentBlocks(0);
            
            results.Should().BeEquivalentTo(expected);
        }
        
        [TestCase(true, new ulong[]{26,27,27})]
        [TestCase(false, new ulong[]{25,26,27})]
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
            ISpecProvider specProvider = SpecProviderWithEip1559EnabledAs(eip1559Enabled);
            GasPriceOracle gasPriceOracle = new(blockFinder, specProvider);

            IEnumerable<UInt256> results = gasPriceOracle.GetGasPricesFromRecentBlocks(0);

            List<UInt256> expectedList = expected.Select(n => (UInt256) n).ToList();
            results.Should().BeEquivalentTo(expectedList);
        }
        
        [TestCase(true, new ulong[]{25,26,27})]
        [TestCase(false, new ulong[]{25,26,27})]
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
            ISpecProvider specProvider = SpecProviderWithEip1559EnabledAs(eip1559Enabled);
            GasPriceOracle gasPriceOracle = new(blockFinder, specProvider);

            IEnumerable<UInt256> results = gasPriceOracle.GetGasPricesFromRecentBlocks(0);

            List<UInt256> expectedList = expected.Select(n => (UInt256) n).ToList();
            results.Should().BeEquivalentTo(expectedList);
        }
        
        public static ISpecProvider SpecProviderWithEip1559EnabledAs(bool isEip1559) => 
            new CustomSpecProvider((0, isEip1559 ? London.Instance : Berlin.Instance));
        
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
            GasPriceOracle gasPriceOracle =
                new(blockFinder, Substitute.For<ISpecProvider>()) {LastGasPrice = 7};
            List<UInt256> expected = new() {7};

            IEnumerable<UInt256> results = gasPriceOracle.GetGasPricesFromRecentBlocks(0);
            
            results.ToList().Should().BeEquivalentTo(expected); 
        }
        
        [Test]
        public void AddValidTxAndReturnCount_IfNoTxsInABlock_DefaultPriceAddedToListInstead()
        {
            Transaction[] transactions = Array.Empty<Transaction>();
            Block block = Build.A.Block.Genesis.WithTransactions(transactions).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(0).Returns(block);
            GasPriceOracle gasPriceOracle = new(blockFinder, Substitute.For<ISpecProvider>()) {LastGasPrice = 7};
            List<UInt256> expected = new() {7};

            IEnumerable<UInt256> results = gasPriceOracle.GetGasPricesFromRecentBlocks(0);
            
            results.ToList().Should().BeEquivalentTo(expected); 
        }
    }
}
