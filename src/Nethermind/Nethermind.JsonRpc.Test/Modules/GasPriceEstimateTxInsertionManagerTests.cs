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
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NSubstitute.Extensions;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.TestBlockConstructor;
using static Nethermind.JsonRpc.Test.Modules.Eth.EthRpcModuleTests;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    class GasPriceEstimateTxInsertionManagerTests
    {
        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasMoreThanThreeValidTx_AddOnlyThreeNew()
        {
            (List<UInt256> results, GasPriceEstimateTxInsertionManager txInsertionManager) = GetTestableTxInsertionManager(ignoreUnder: 3);
            txInsertionManager.Configure().GetTxGasPriceList(Arg.Any<IGasPriceOracle>()).Returns(results);
            Block testBlock = GetTestBlockB();
            
            txInsertionManager.AddValidTxFromBlockAndReturnCount(testBlock);

            results.Count.Should().Be(3);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_IfBlockHasMoreThanThreeValidTxs_OnlyAddTxsWithLowestGasPrices()
        {
            (List<UInt256> results, GasPriceEstimateTxInsertionManager txInsertionManager) = GetTestableTxInsertionManager(ignoreUnder: 3);
            txInsertionManager.Configure().GetTxGasPriceList(Arg.Any<IGasPriceOracle>()).Returns(results);
            Block testBlock = GetTestBlockB();
            List<UInt256> expected = new() {5,6,7};
            
            txInsertionManager.AddValidTxFromBlockAndReturnCount(testBlock);

            results.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void AddValidTxAndReturnCount_TxsWithPriceLessThanIgnoreUnder_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            Block testBlock = GetTestBlockA();
            (List<UInt256> results, GasPriceEstimateTxInsertionManager txInsertionManager) = GetTestableTxInsertionManager(ignoreUnder: 3);
            txInsertionManager.Configure().GetTxGasPriceList(Arg.Any<IGasPriceOracle>()).Returns(results);
            List<UInt256> expected = new() {3, 4};
            
            txInsertionManager.AddValidTxFromBlockAndReturnCount(testBlock);

            results.Should().BeEquivalentTo(expected);
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
            (List<UInt256> results, GasPriceEstimateTxInsertionManager txInsertionManager) = GetTestableTxInsertionManager();
            List<UInt256> expected = new() {8,9};
            
            txInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            results.Should().BeEquivalentTo(expected);
        }
        
        [TestCase(true, new ulong[]{26,27,27})]
        [TestCase(false, new ulong[]{1})]
        public void AddValidTxAndReturnCount_WhenEip1559Txs_EffectiveGasPriceProperlyCalculated(bool eip1559Enabled, ulong[] expected)
        {  
            Transaction[] eip1559TxGroup = {
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(25).WithType(TxType.EIP1559).TestObject,
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(26).WithType(TxType.EIP1559).TestObject, 
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(27).WithType(TxType.EIP1559).TestObject  
            };
            Block eip1559Block = Build.A.Block.Genesis.WithTransactions(eip1559TxGroup).WithBaseFeePerGas(1).TestObject;
            ISpecProvider specProvider = SpecProviderWithEip1559EnabledAs(eip1559Enabled);
            IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
            gasPriceOracle.FallbackGasPrice.Returns(UInt256.One);
            (List<UInt256> results, GasPriceEstimateTxInsertionManager txInsertionManager) =
                GetTestableTxInsertionManager(gasPriceOracle: gasPriceOracle, specProvider: specProvider);

            txInsertionManager.AddValidTxFromBlockAndReturnCount(eip1559Block);

            List<UInt256> expectedList = expected.Select(n => (UInt256) n).ToList();
            results.Should().BeEquivalentTo(expectedList);
        }
         
        [TestCase(true, new ulong[]{25,26,27})]
        [TestCase(false, new ulong[]{25,26,27})]
        public void AddValidTxAndReturnCount_WhenNotEip1559Txs_EffectiveGasPriceProperlyCalculated(bool eip1559Enabled, ulong[] expected)
        {  
            Transaction[] nonEip1559TxGroup = {
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(25).TestObject,
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(26).TestObject,
                Build.A.Transaction.WithMaxFeePerGas(27).WithMaxPriorityFeePerGas(27).TestObject 
            };
            
            Block eip1559Block = Build.A.Block.Genesis.WithTransactions(nonEip1559TxGroup).WithBaseFeePerGas(1).TestObject;
            ISpecProvider specProvider = SpecProviderWithEip1559EnabledAs(eip1559Enabled);
            (List<UInt256> results, GasPriceEstimateTxInsertionManager txInsertionManager) =
                GetTestableTxInsertionManager(specProvider: specProvider);

            txInsertionManager.AddValidTxFromBlockAndReturnCount(eip1559Block);

            List<UInt256> expectedList = expected.Select(n => (UInt256) n).ToList();
            results.Should().BeEquivalentTo(expectedList);
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
            (List<UInt256> results, GasPriceEstimateTxInsertionManager txInsertionManager) = GetTestableTxInsertionManager(gasPriceOracle:gasPriceOracle, ignoreUnder: 4);
            List<UInt256> expected = new() {7};

            txInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            results.Should().BeEquivalentTo(expected); 
        }

        [Test]
        public void AddValidTxAndReturnCount_IfNoTxsInABlock_DefaultPriceAddedToListInstead()
        {
            Transaction[] transactions = Array.Empty<Transaction>();
            Block block = Build.A.Block.Genesis.WithTransactions(transactions).TestObject;
            IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
            gasPriceOracle.FallbackGasPrice.Returns((UInt256?) 3);
            (List<UInt256> results, GasPriceEstimateTxInsertionManager txInsertionManager) = GetTestableTxInsertionManager(gasPriceOracle:gasPriceOracle, ignoreUnder: 4);
            List<UInt256> expected = new() {3};

            txInsertionManager.AddValidTxFromBlockAndReturnCount(block);
            
            results.Should().BeEquivalentTo(expected); 
        }
        
        private (List<UInt256>, GasPriceEstimateTxInsertionManager) GetTestableTxInsertionManager(
            IGasPriceOracle gasPriceOracle = null,
            UInt256? ignoreUnder = null,
            ISpecProvider specProvider = null)
        {
            GasPriceEstimateTxInsertionManager txInsertionManager = Substitute.ForPartsOf<GasPriceEstimateTxInsertionManager>(
                    gasPriceOracle ?? Substitute.For<IGasPriceOracle>(),
                    ignoreUnder ?? UInt256.Zero,
                    specProvider ?? Substitute.For<ISpecProvider>());
            List<UInt256> results = new();
            txInsertionManager.Configure().GetTxGasPriceList(Arg.Any<IGasPriceOracle>()).Returns(results);

            return (results, txInsertionManager);
        }
    }
}
