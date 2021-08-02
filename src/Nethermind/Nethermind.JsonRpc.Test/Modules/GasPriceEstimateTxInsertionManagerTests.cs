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
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
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
        public void AddValidTxAndReturnCount_TxsWithPriceLessThanIgnoreUnder_ShouldNotHaveGasPriceInTxGasPriceList()
        {
            Block testBlock = GetTestBlockA();
            (List<UInt256> results, GasPriceEstimateTxInsertionManager txInsertionManager) = GetTestableTxInsertionManager(ignoreUnder: 3);
            txInsertionManager.Configure().GetTxGasPriceList(Arg.Any<IGasPriceOracle>()).Returns(results);
            List<UInt256> expected = new() {3, 4};
            
            txInsertionManager.GetTxPrices(testBlock);

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

            txInsertionManager.GetTxPrices(block);
            
            results.Should().BeEquivalentTo(expected); 
        }
        
        private (List<UInt256>, GasPriceEstimateTxInsertionManager) GetTestableTxInsertionManager(
            IGasPriceOracle gasPriceOracle = null,
            UInt256? ignoreUnder = null,
            ISpecProvider specProvider = null)
        {
            GasPriceEstimateTxInsertionManager txInsertionManager = 
                Substitute.ForPartsOf<GasPriceEstimateTxInsertionManager>(
                    gasPriceOracle ?? Substitute.For<IGasPriceOracle>(),
                    ignoreUnder ?? UInt256.Zero,
                    specProvider ?? Substitute.For<ISpecProvider>());
            List<UInt256> results = new();
            txInsertionManager.Configure().GetTxGasPriceList(Arg.Any<IGasPriceOracle>()).Returns(results);
            
            return (results, txInsertionManager);
        }
    }
}
