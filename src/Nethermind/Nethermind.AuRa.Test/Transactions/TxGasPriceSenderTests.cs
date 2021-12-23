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

using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class TxGasPriceSenderTests
    {
        
        [TestCase(0uL, 0u, 0uL)]
        [TestCase(100uL, 110u, 110uL)]
        [TestCase(200uL, 110u, 220uL)]
        public void SendTransaction_sets_correct_gas_price(ulong gasEstimate, uint percentDelta, ulong expectedGasPrice)
        {
            IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
            gasPriceOracle.GetGasPriceEstimate().Returns(gasEstimate);
            ITxSender txSender = Substitute.For<ITxSender>();
            TxGasPriceSender txGasPriceSender = new(txSender, gasPriceOracle, percentDelta);
            Transaction transaction = Build.A.Transaction.WithGasPrice(0).TestObject;
            txGasPriceSender.SendTransaction(transaction, TxHandlingOptions.None);
            Assert.AreEqual((UInt256)expectedGasPrice, transaction.GasPrice);
        }
    }
}
