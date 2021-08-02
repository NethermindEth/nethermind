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
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute.Extensions;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.TestBlockConstructor;
using static Nethermind.JsonRpc.Test.Modules.Eth.EthRpcModuleTests;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    class GasPriceEstimateTxInsertionManagerTests
    {
        /*
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
        */
    }
}
