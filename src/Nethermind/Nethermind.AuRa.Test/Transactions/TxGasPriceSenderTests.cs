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

using System.Collections;
using System.Linq;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class TxGasPriceSenderTests
    {
        public static IEnumerable TestCases
        {
            get
            {
                UInt256 scaledDefault = TxGasPriceSenderConstants.DefaultGasPrice * 110 / 100;
                UInt256 u100 = new(100);
                UInt256 u200 = new(200);
                UInt256 u300 = new(300);
                UInt256 u3_000_000_000 = new(3_000_000_000);
                uint scale = 110u;

                yield return new TestCaseData(UInt256.Zero, new UInt256[0], 100u)
                    .Returns(TxGasPriceSenderConstants.DefaultGasPrice)
                    .SetName("Default");
                
                yield return new TestCaseData(UInt256.Zero, new UInt256[0], scale)
                    .Returns(scaledDefault)
                    .SetName("Default scaled");
                
                yield return new TestCaseData(u100, new UInt256[0], scale)
                    .Returns(scaledDefault)
                    .SetName("Min mined gas price < Default");
                
                yield return new TestCaseData(u3_000_000_000, new UInt256[0], scale)
                    .Returns(u3_000_000_000)
                    .SetName("Min mined gas price > Default");
                
                yield return new TestCaseData(UInt256.Zero, new[] {u100, u200}, 100u)
                    .Returns(u100)
                    .SetName("Lowest pool");
                
                yield return new TestCaseData(u100, new[] {u200}, 100u)
                    .Returns(u200)
                    .SetName("Lowest pool > Min mined gas price");
                
                yield return new TestCaseData(u300, new[] {u100, u200}, 100u)
                    .Returns(u300)
                    .SetName("Min mined gas price > Highest pool");
                
                yield return new TestCaseData(UInt256.Zero, new[] {u100, u200}, scale)
                    .Returns(u100 * scale / 100u)
                    .SetName("Lowest pool scaled");
            }
        }
        
        [TestCaseSource(nameof(TestCases))]
        public UInt256 SendTransaction_sets_correct_gas_price(UInt256 minMiningGasPrice, UInt256[] txPoolGasPrices, uint percentDelta)
        {
            ITxSender txSender = Substitute.For<ITxSender>();
            ITxPool txPool = Substitute.For<ITxPool>();
            txPool.GetPendingTransactions().Returns(
                txPoolGasPrices.Select(g => Build.A.Transaction.WithGasPrice(g).TestObject).ToArray());
            MiningConfig miningConfig = new() {MinGasPrice = minMiningGasPrice};
            TxGasPriceSender txGasPriceSender = new(txSender, txPool, miningConfig, percentDelta);

            Transaction transaction = Build.A.Transaction.WithGasPrice(0).TestObject;

            txGasPriceSender.SendTransaction(transaction, TxHandlingOptions.None);

            return transaction.GasPrice;
        }
    }
}
