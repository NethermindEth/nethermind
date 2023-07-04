// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            Assert.That(transaction.GasPrice, Is.EqualTo((UInt256)expectedGasPrice));
        }
    }
}
