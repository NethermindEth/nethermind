// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.TxPool.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class TransactionExtensionsTests
    {
        [Test]
        public void CalculatePayableGasPrice_returns_expected_results([ValueSource(nameof(TransactionPayableGasPriceCases))]
            TransactionPayableGasPrice test)
        {
            Transaction tx = new();
            tx.Type = test.Type;
            tx.GasPrice = test.GasPrice;
            tx.GasLimit = test.GasLimit;
            tx.Value = test.Value;
            tx.DecodedMaxFeePerGas = test.FeeCap;

            UInt256 payableGasPrice = tx.CalculateAffordableGasPrice(test.IsEip1559Enabled, test.BaseFee, test.AccountBalance);
            Assert.That(payableGasPrice, Is.EqualTo(test.ExpectedPayableGasPriceResult));
        }

        private const long DisplacedCost = 7_000;
        private const long KeyedCost = 5_000;

        /// <remarks>Ascending nonce order lets the walk stop at the entry the incoming transaction displaces, but
        /// an EIP-8250 domain past that cut is an independent liability, so the exit only holds before activation.</remarks>
        [TestCase(false, 0L, TestName = "before activation the walk stops at the transaction it displaces")]
        [TestCase(true, KeyedCost, TestName = "after activation the keyed liability past it is still summed")]
        public void IsOverflowWhenSummingSenderBucket_walks_past_the_displaced_transaction_only_after_activation(bool keyedNoncesEnabled, long expected)
        {
            Transaction displaced = OrdinaryTx(nonce: 1, DisplacedCost, TestItem.KeccakA);
            Transaction incoming = OrdinaryTx(nonce: 1, DisplacedCost, TestItem.KeccakC);

            bool overflow = incoming.IsOverflowWhenSummingSenderBucket(
                FrameTxFilterTestPools.Pool(blobs: false, displaced, KeyedTx(nonce: 2, KeyedCost, TestItem.KeccakB)),
                accountNonce: 1, unreservedOnly: false, keyedNoncesEnabled, out UInt256 cumulativeCost);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(overflow, Is.False);
                Assert.That(cumulativeCost, Is.EqualTo((UInt256)expected));
            }
        }

        /// <remarks>The control: with no keyed domain in the bucket the two readings must agree, so the guard
        /// above changes only what it is meant to.</remarks>
        [Test]
        public void IsOverflowWhenSummingSenderBucket_is_unchanged_by_activation_over_an_account_domain_bucket([Values] bool keyedNoncesEnabled)
        {
            const long aheadCost = 3_000;
            Transaction ahead = OrdinaryTx(nonce: 0, aheadCost, TestItem.KeccakA);
            Transaction displaced = OrdinaryTx(nonce: 1, DisplacedCost, TestItem.KeccakB);
            Transaction later = OrdinaryTx(nonce: 2, DisplacedCost, TestItem.KeccakD);
            Transaction incoming = OrdinaryTx(nonce: 1, DisplacedCost, TestItem.KeccakC);

            bool overflow = incoming.IsOverflowWhenSummingSenderBucket(
                FrameTxFilterTestPools.Pool(blobs: false, ahead, displaced, later),
                accountNonce: 0, unreservedOnly: false, keyedNoncesEnabled, out UInt256 cumulativeCost);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(overflow, Is.False);
                Assert.That(cumulativeCost, Is.EqualTo((UInt256)aheadCost));
            }
        }

        private static Transaction OrdinaryTx(ulong nonce, long cost, Hash256 hash) =>
            Build.A.Transaction
                .WithSenderAddress(TestItem.AddressA)
                .WithNonce(nonce)
                .WithGasLimit(1)
                .WithGasPrice((UInt256)cost)
                .WithValue(0)
                .WithHash(hash)
                .TestObject;

        /// <summary>A frame transaction in its own EIP-8250 domain, priced at what admission recorded.</summary>
        private static Transaction KeyedTx(ulong nonce, long cost, Hash256 hash)
        {
            Transaction tx = Build.A.Transaction
                .WithType(TxType.FrameTx)
                .WithSenderAddress(TestItem.AddressA)
                .WithNonce(nonce)
                .WithNonceKeys([(UInt256)1])
                .WithHash(hash)
                .TestObject;
            tx.PayerExposure = (UInt256)cost;
            return tx;
        }

        public class TransactionPayableGasPrice
        {
            public int Lp { get; set; }
            public UInt256 BaseFee { get; set; }
            public ulong FeeCap { get; set; }
            public UInt256 GasPrice { get; set; }
            public TxType Type { get; set; }
            public ulong GasLimit { get; set; }
            public ulong Value { get; set; }
            public bool IsEip1559Enabled { get; set; }

            public UInt256 AccountBalance { get; set; }
            public UInt256 ExpectedPayableGasPriceResult { get; set; }

            public override string ToString() =>
                $"Lp: {Lp}, ExpectedPayableGasPriceResult: {ExpectedPayableGasPriceResult}";
        }

        public static IEnumerable<TransactionPayableGasPrice> TransactionPayableGasPriceCases
        {
            get
            {
                /* Legacy transactions before 1559 fork:*/
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 1,
                    GasPrice = 10,
                    AccountBalance = 100,
                    ExpectedPayableGasPriceResult = 10
                };
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 2,
                    GasPrice = 21,
                    GasLimit = 100,
                    AccountBalance = 2100,
                    ExpectedPayableGasPriceResult = 21
                };
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 3,
                    GasPrice = 21,
                    GasLimit = 100,
                    Value = 3,
                    AccountBalance = 2100,
                    ExpectedPayableGasPriceResult = 21
                };

                /*Legacy after 1559 fork:*/
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 4,
                    IsEip1559Enabled = true,
                    GasPrice = 10,
                    GasLimit = 300,
                    Value = 5,
                    AccountBalance = 3005,
                    ExpectedPayableGasPriceResult = 10
                };
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 5,
                    IsEip1559Enabled = true,
                    GasPrice = 10,
                    GasLimit = 300,
                    Value = 5,
                    BaseFee = 500,
                    AccountBalance = 3005,
                    ExpectedPayableGasPriceResult = 10
                };
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 6,
                    IsEip1559Enabled = true,
                    GasPrice = 10,
                    GasLimit = 300,
                    Value = 5,
                    BaseFee = 5,
                    AccountBalance = 3004,
                    ExpectedPayableGasPriceResult = 10
                };
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 7,
                    IsEip1559Enabled = true,
                    GasPrice = 0,
                    GasLimit = 300,
                    Value = 0,
                    BaseFee = 5,
                    AccountBalance = 100000,
                    ExpectedPayableGasPriceResult = 0
                };

                /* Eip1559 transactions before 1559 fork:*/
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 8,
                    Type = TxType.EIP1559,
                    GasPrice = 10,
                    GasLimit = 300,
                    FeeCap = 500,
                    Value = 5,
                    AccountBalance = 3005,
                    ExpectedPayableGasPriceResult = 10
                };
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 9,
                    Type = TxType.EIP1559,
                    GasPrice = 10,
                    GasLimit = 300,
                    Value = 5,
                    AccountBalance = 3004,
                    ExpectedPayableGasPriceResult = 10
                };

                /* Eip1559 transactions after 1559 fork:*/
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 10,
                    IsEip1559Enabled = true,
                    Type = TxType.EIP1559,
                    GasPrice = 10,
                    GasLimit = 300,
                    FeeCap = 500,
                    Value = 5,
                    BaseFee = 20,
                    AccountBalance = 10000,
                    ExpectedPayableGasPriceResult = 30
                };
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 11,
                    IsEip1559Enabled = true,
                    Type = TxType.EIP1559,
                    GasPrice = 10,
                    GasLimit = 300,
                    FeeCap = 500,
                    Value = 5,
                    BaseFee = 20,
                    AccountBalance = 2000,
                    ExpectedPayableGasPriceResult = 6
                };
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 12,
                    IsEip1559Enabled = true,
                    Type = TxType.EIP1559,
                    GasPrice = 10,
                    GasLimit = 300,
                    FeeCap = 500,
                    Value = 5,
                    BaseFee = 20,
                    AccountBalance = 305,
                    ExpectedPayableGasPriceResult = 1
                };
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 13,
                    IsEip1559Enabled = true,
                    Type = TxType.EIP1559,
                    GasPrice = 10,
                    GasLimit = 300,
                    FeeCap = 500,
                    Value = 5,
                    BaseFee = 20,
                    AccountBalance = 304,
                    ExpectedPayableGasPriceResult = 0
                };
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 14,
                    IsEip1559Enabled = true,
                    Type = TxType.EIP1559,
                    GasPrice = 0,
                    GasLimit = 300,
                    FeeCap = 500,
                    Value = 5,
                    BaseFee = 20,
                    AccountBalance = 10000,
                    ExpectedPayableGasPriceResult = 20
                };
                yield return new TransactionPayableGasPrice()
                {
                    Lp = 15,
                    IsEip1559Enabled = true,
                    Type = TxType.EIP1559,
                    GasPrice = 0,
                    GasLimit = 300,
                    FeeCap = 10,
                    Value = 5,
                    BaseFee = 20,
                    AccountBalance = 10000,
                    ExpectedPayableGasPriceResult = 10
                };
            }
        }
    }
}
