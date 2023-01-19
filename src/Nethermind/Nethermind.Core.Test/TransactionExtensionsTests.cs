// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class TransactionExtensionsTests
    {
        [Test]
        public void GetTransactionPotentialCost_returns_expected_results([ValueSource(nameof(TransactionPotentialCostsTestCases))]
            TransactionPotentialCostsAndEffectiveGasPrice test)
        {
            Transaction transaction = new();
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

        [Test]
        public void TryCalculatePremiumPerGas_should_succeeds_for_free_transactions()
        {
            Transaction transaction = new SystemTransaction();
            transaction.DecodedMaxFeePerGas = 10;
            transaction.GasPrice = 30;
            transaction.Type = TxType.EIP1559;
            bool tryResult = transaction.TryCalculatePremiumPerGas(100, out UInt256 premiumPerGas);
            Assert.AreEqual(UInt256.Zero, premiumPerGas);
            Assert.True(tryResult);
        }

        [Test]
        public void CalculateEffectiveGasPrice_can_handle_overflow_scenario_with_max_priority()
        {
            Transaction transaction = new();
            transaction.DecodedMaxFeePerGas = UInt256.MaxValue;
            transaction.GasPrice = UInt256.MaxValue;
            transaction.Type = TxType.EIP1559;
            UInt256 effectiveGasPrice = transaction.CalculateEffectiveGasPrice(true, 100);
            Assert.AreEqual(UInt256.MaxValue, effectiveGasPrice);
        }

        [Test]
        public void CalculateEffectiveGasPrice_can_handle_overflow_scenario_with_max_baseFee()
        {
            UInt256 expectedValue = UInt256.MaxValue - 10;
            Transaction transaction = new();
            transaction.DecodedMaxFeePerGas = expectedValue;
            transaction.GasPrice = 100;
            transaction.Type = TxType.EIP1559;
            UInt256 effectiveGasPrice = transaction.CalculateEffectiveGasPrice(true, UInt256.MaxValue);
            Assert.AreEqual(expectedValue, effectiveGasPrice);
        }

        [Test]
        public void TryCalculatePremiumPerGas_should_fails_when_base_fee_is_greater_than_fee()
        {
            Transaction transaction = new();
            transaction.DecodedMaxFeePerGas = 10;
            transaction.GasPrice = 30;
            transaction.Type = TxType.EIP1559;
            bool tryResult = transaction.TryCalculatePremiumPerGas(100, out UInt256 premiumPerGas);
            Assert.AreEqual(UInt256.Zero, premiumPerGas);
            Assert.False(tryResult);
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
                    Lp = 1,
                    GasPrice = 10,
                    ExpectedPotentialCostResult = 0,
                    ExpectedEffectiveGasPriceResult = 10
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 2,
                    GasPrice = 21,
                    GasLimit = 100,
                    ExpectedPotentialCostResult = 2100,
                    ExpectedEffectiveGasPriceResult = 21
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 3,
                    GasPrice = 21,
                    GasLimit = 100,
                    Value = 3,
                    ExpectedPotentialCostResult = 2103,
                    ExpectedEffectiveGasPriceResult = 21
                };

                /*Legacy after 1559 fork:*/
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 4,
                    IsEip1559Enabled = true,
                    GasPrice = 10,
                    GasLimit = 300,
                    Value = 5,
                    ExpectedPotentialCostResult = 3005,
                    ExpectedEffectiveGasPriceResult = 10
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 5,
                    IsEip1559Enabled = true,
                    GasPrice = 10,
                    GasLimit = 300,
                    Value = 5,
                    BaseFee = 200,
                    ExpectedPotentialCostResult = 3005,
                    ExpectedEffectiveGasPriceResult = 10
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 6,
                    IsEip1559Enabled = true,
                    GasPrice = 10,
                    GasLimit = 300,
                    Value = 5,
                    BaseFee = 5,
                    ExpectedPotentialCostResult = 3005,
                    ExpectedEffectiveGasPriceResult = 10
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 7,
                    IsEip1559Enabled = true,
                    GasPrice = 0,
                    GasLimit = 300,
                    Value = 0,
                    BaseFee = 5,
                    ExpectedPotentialCostResult = 0,
                    ExpectedEffectiveGasPriceResult = 0
                };

                /* Eip1559 transactions before 1559 fork:*/
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 8,
                    Type = TxType.EIP1559,
                    GasPrice = 10,
                    GasLimit = 300,
                    FeeCap = 500,
                    Value = 5,
                    ExpectedPotentialCostResult = 3005,
                    ExpectedEffectiveGasPriceResult = 10
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 9,
                    Type = TxType.EIP1559,
                    GasPrice = 10,
                    FeeCap = 300,
                    ExpectedPotentialCostResult = 0,
                    ExpectedEffectiveGasPriceResult = 10
                };

                /* Eip1559 transactions after 1559 fork:*/
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 10,
                    IsEip1559Enabled = true,
                    Type = TxType.EIP1559,
                    GasPrice = 10,
                    GasLimit = 300,
                    FeeCap = 5,
                    Value = 5,
                    BaseFee = 200,
                    ExpectedPotentialCostResult = 1505,
                    ExpectedEffectiveGasPriceResult = 5
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 11,
                    IsEip1559Enabled = true,
                    Type = TxType.EIP1559,
                    GasPrice = 10,
                    GasLimit = 300,
                    FeeCap = 300,
                    Value = 5,
                    BaseFee = 200,
                    ExpectedPotentialCostResult = 63005,
                    ExpectedEffectiveGasPriceResult = 210
                };
                yield return new TransactionPotentialCostsAndEffectiveGasPrice()
                {
                    Lp = 12,
                    IsEip1559Enabled = true,
                    Type = TxType.EIP1559,
                    GasPrice = 0,
                    GasLimit = 300,
                    FeeCap = 0,
                    Value = 5,
                    BaseFee = 200,
                    ExpectedPotentialCostResult = 5,
                    ExpectedEffectiveGasPriceResult = 0
                };
            }
        }
    }
}
