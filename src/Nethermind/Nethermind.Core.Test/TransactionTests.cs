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

using System;
using FluentAssertions;
using System.Collections.Generic;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class TransactionTests
    {
        [Test]
        public void When_to_not_empty_then_is_message_call()
        {
            Transaction transaction = new Transaction();
            transaction.To = Address.Zero;
            Assert.True(transaction.IsMessageCall, nameof(Transaction.IsMessageCall));
            Assert.False(transaction.IsContractCreation, nameof(Transaction.IsContractCreation));
        }

        [Test]
        public void When_to_empty_then_is_message_call()
        {
            Transaction transaction = new Transaction();
            transaction.To = null;
            Assert.False(transaction.IsMessageCall, nameof(Transaction.IsMessageCall));
            Assert.True(transaction.IsContractCreation, nameof(Transaction.IsContractCreation));
        }
        
        [TestCase(1, true)]
        [TestCase(300, true)]
        public void IsEip1559_returns_expected_results(int decodedFeeCap, bool expectedIsEip1559)
        {
            Transaction transaction = new Transaction();
            transaction.DecodedMaxFeePerGas = (uint)decodedFeeCap;
            transaction.Type = TxType.EIP1559;
            Assert.AreEqual(transaction.MaxFeePerGas, transaction.DecodedMaxFeePerGas);
            Assert.AreEqual(expectedIsEip1559, transaction.IsEip1559);
        }
        
        [Test]
        public void GetTransactionPotentialCost_returns_expected_results([ValueSource(nameof(TransactionPotentialCostsTestCases))]
            TransactionPotentialCostsAndEffectiveGasPrice test)
        {
            Transaction transaction = new Transaction();
            transaction.GasPrice = test.GasPrice;
            transaction.GasLimit = test.GasLimit;
            transaction.Value = test.Value;
            transaction.DecodedMaxFeePerGas = test.FeeCap;
            transaction.Type = test.Type;
            UInt256 actualResult = transaction.CalculateTransactionPotentialCost(test.IsEip1559Enabled, test.BaseFee);
            UInt256 effectiveGasPrice = transaction.CalculateEffectiveGasPrice(test.IsEip1559Enabled, test.BaseFee);
            Assert.AreEqual(test.ExpectedPotentialCostResult, actualResult);
            Assert.AreEqual(test.ExpectedEffectiveGasPriceResult, effectiveGasPrice);
            Assert.AreEqual(test.ExpectedEffectiveGasPriceResult * (UInt256)test.GasLimit + test.Value, test.ExpectedPotentialCostResult);
        }
        
        public class TransactionPotentialCostsAndEffectiveGasPrice
        {
            public int Lp { get; set; }
            public UInt256 BaseFee { get; set; }
            public UInt256 FeeCap { get; set; }
            public UInt256 GasPrice { get; set; }
            public TxType Type { get; set; }
            public long GasLimit { get; set; }
            public UInt256 Value { get; set; }
            public bool IsEip1559Enabled { get; set; }
            public UInt256 ExpectedPotentialCostResult { get; set; }
            
            public UInt256 ExpectedEffectiveGasPriceResult { get; set; }
            
            public override string ToString() =>
                $"Lp: {Lp}, ExpectedPotentialCostResult: {ExpectedPotentialCostResult}, ExpectedEffectiveGasPriceResult: {ExpectedEffectiveGasPriceResult}";
        }
        
        public static IEnumerable<TransactionPotentialCostsAndEffectiveGasPrice> TransactionPotentialCostsTestCases
        {
            get
            {
                /* Legacy transactions before 1559 fork:*/
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 1, GasPrice = 10, ExpectedPotentialCostResult = 0, ExpectedEffectiveGasPriceResult = 10
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 2, GasPrice = 21, GasLimit = 100, ExpectedPotentialCostResult = 2100, ExpectedEffectiveGasPriceResult = 21
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 3, GasPrice = 21, GasLimit = 100, Value = 3, ExpectedPotentialCostResult = 2103, ExpectedEffectiveGasPriceResult = 21
                };
                
                /*Legacy after 1559 fork:*/
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 4, IsEip1559Enabled = true, GasPrice = 10, GasLimit = 300, Value = 5, ExpectedPotentialCostResult = 3005, ExpectedEffectiveGasPriceResult = 10
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 5, IsEip1559Enabled = true, GasPrice = 10, GasLimit = 300, Value = 5, BaseFee = 200, ExpectedPotentialCostResult = 3005, ExpectedEffectiveGasPriceResult = 10
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 6, IsEip1559Enabled = true, GasPrice = 10, GasLimit = 300, Value = 5, BaseFee = 5, ExpectedPotentialCostResult = 3005, ExpectedEffectiveGasPriceResult = 10
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 7, IsEip1559Enabled = true, GasPrice = 0, GasLimit = 300, Value = 0, BaseFee = 5, ExpectedPotentialCostResult = 0, ExpectedEffectiveGasPriceResult = 0
                };
                
                /* Eip1559 transactions before 1559 fork:*/
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 8, Type = TxType.EIP1559, GasPrice = 10, GasLimit = 300, FeeCap = 500, Value = 5, ExpectedPotentialCostResult = 3005, ExpectedEffectiveGasPriceResult = 10
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 9, Type = TxType.EIP1559, GasPrice = 10, FeeCap = 300, ExpectedPotentialCostResult = 0, ExpectedEffectiveGasPriceResult = 10
                };
                
                /* Eip1559 transactions after 1559 fork:*/
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 10, IsEip1559Enabled = true, Type = TxType.EIP1559, GasPrice = 10, GasLimit = 300, FeeCap = 5, Value = 5, BaseFee = 200, ExpectedPotentialCostResult = 1505, ExpectedEffectiveGasPriceResult = 5
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 11, IsEip1559Enabled = true, Type = TxType.EIP1559, GasPrice = 10, GasLimit = 300, FeeCap = 300, Value = 5, BaseFee = 200, ExpectedPotentialCostResult = 63005, ExpectedEffectiveGasPriceResult = 210
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 12, IsEip1559Enabled = true, Type = TxType.EIP1559, GasPrice = 0, GasLimit = 300, FeeCap = 0, Value = 5, BaseFee = 200, ExpectedPotentialCostResult = 5, ExpectedEffectiveGasPriceResult = 0
                };
                
            }
        }
    }
}
