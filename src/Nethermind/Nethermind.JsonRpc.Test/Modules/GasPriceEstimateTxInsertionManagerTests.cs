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

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.TestBlockConstructor;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    partial class GasPriceEstimateTxInsertionManagerTests
    {
        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasMoreThanThreeValidTx_AddOnlyThree()
        {
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            Block testBlock = GetTestBlockB();
            
            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(testBlock);

            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Count.Should().Be(3);
        }

        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasMoreThanThreeValidTxs_OnlyAddTxsWithLowestGasPrices()
        {
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            Block testBlock = GetTestBlockB();
            List<UInt256> expected = new() {5,6,7};
            
            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(testBlock);

            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_TxsWithPriceLessThanIgnoreUnder_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            Block testBlock = GetTestBlockA();
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(ignoreUnder: 3);
            List<UInt256> expected = new() {3, 4};
            
            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(testBlock);

            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_TxsSentByMiner_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            Address minerAddress = TestItem.PrivateKeyA.Address;
            Transaction[] transactions =
            {
                Build.A.Transaction.WithGasPrice(7).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(0).TestObject,
                Build.A.Transaction.WithGasPrice(8).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(0).TestObject,
                Build.A.Transaction.WithGasPrice(9).SignedAndResolved(TestItem.PrivateKeyC).WithNonce(0).TestObject,
            };
            Block block = Build.A.Block.Genesis.WithBeneficiary(minerAddress).WithTransactions(transactions).TestObject;
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager();
            List<UInt256> expected = new() {8,9};
            
            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void AddValidTxAndReturnCount_WhenEip1559Txs_EffectiveGasPriceProperlyCalculated()
        {  
            Transaction[] eip1559TxGroup = {
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(25).WithType(TxType.EIP1559).TestObject, //Min(27, 25 + 1) => 26
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(26).WithType(TxType.EIP1559).TestObject, //Min(27, 26 + 1) => 27
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(27).WithType(TxType.EIP1559).TestObject  //Min(27, 27 + 1) => 27
            };
            Block eip1559Block = Build.A.Block.Genesis.WithTransactions(eip1559TxGroup).WithBaseFeePerGas(1).TestObject;
            ISpecProvider specProvider = SpecProviderWithEip1559EnabledAs(true);
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager =
                GasPriceEstimateTxInsertionManagerWith(specProvider);
            List<UInt256> expected = new() {26, 27, 27};

            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(eip1559Block);

            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void AddValidTxAndReturnCount_WhenNotEip1559Txs_EffectiveGasPriceProperlyCalculated()
        {  
            Transaction[] nonEip1559TxGroup = {
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(25).TestObject, //Min(25, 25 + 1) => 25
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(26).TestObject, //Min(26, 26 + 1) => 26
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(27).TestObject  //Min(27, 27 + 1) => 27
            };
            
            Block eip1559Block = Build.A.Block.Genesis.WithTransactions(nonEip1559TxGroup).WithBaseFeePerGas(1).TestObject;
            ISpecProvider specProvider = SpecProviderWithEip1559EnabledAs(false);
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager =
                GasPriceEstimateTxInsertionManagerWith(specProvider);
            List<UInt256> expected = new() {25, 26, 27};

            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(eip1559Block);

            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().BeEquivalentTo(expected);
        }
        
        TestableGasPriceEstimateTxInsertionManager GasPriceEstimateTxInsertionManagerWith(ISpecProvider specProviderParam)
        {
            IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
            gasPriceOracle.FallbackGasPrice.Returns(UInt256.One);
           
            TestableGasPriceEstimateTxInsertionManager testableManager =
                GetTestableTxInsertionManager(specProvider: specProviderParam, gasPriceOracle: gasPriceOracle);
            
            return testableManager;
        }

        private ISpecProvider SpecProviderWithEip1559EnabledAs(bool isEip1559)
        {
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            IReleaseSpec specEip1559 = Substitute.For<IReleaseSpec>();
            specEip1559.IsEip1559Enabled.Returns(isEip1559);
            
            specProvider.GetSpec(Arg.Any<long>()).Returns(specEip1559);
            
            return specProvider;
        }

        [Test]
        public void AddValidTxAndReturnCount_IfNoValidTxsInABlock_DefaultPriceAddedToListInstead()
        {
            Transaction[] transactions =
            {
                Build.A.Transaction.WithGasPrice(1).TestObject, 
                Build.A.Transaction.WithGasPrice(2).TestObject,
                Build.A.Transaction.WithGasPrice(3).TestObject
            };
            Block block = Build.A.Block.Genesis.WithTransactions(transactions).TestObject;
            IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
            gasPriceOracle.FallbackGasPrice.Returns((UInt256?) 7);
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(gasPriceOracle:gasPriceOracle, ignoreUnder: 4);
            List<UInt256> expected = new() {7};

            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().Equal(expected); 
        }

        [Test]
        public void AddValidTxAndReturnCount_IfNoTxsInABlock_DefaultPriceAddedToListInstead()
        {
            Transaction[] transactions = Array.Empty<Transaction>();
            Block block = Build.A.Block.Genesis.WithTransactions(transactions).TestObject;
            IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
            gasPriceOracle.FallbackGasPrice.Returns((UInt256?) 3);
            TestableGasPriceEstimateTxInsertionManager testableGasPriceEstimateTxInsertionManager = GetTestableTxInsertionManager(gasPriceOracle:gasPriceOracle);
            List<UInt256> expected = new() {3};

            testableGasPriceEstimateTxInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            testableGasPriceEstimateTxInsertionManager._txGasPriceList.Should().Equal(expected); 
        }

        private TestableGasPriceEstimateTxInsertionManager GetTestableTxInsertionManager(
            IGasPriceOracle gasPriceOracle = null,
            UInt256? ignoreUnder = null,
            ISpecProvider specProvider = null,
            List<UInt256> txGasPriceList = null)
        {
            return new(
                gasPriceOracle ?? Substitute.For<IGasPriceOracle>(),
                ignoreUnder ?? UInt256.Zero,
                specProvider ?? Substitute.For<ISpecProvider>(),
                txGasPriceList);
        }
    }
}
