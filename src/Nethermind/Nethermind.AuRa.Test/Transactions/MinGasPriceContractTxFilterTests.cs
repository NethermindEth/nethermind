// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class MinGasPriceContractTxFilterTests
    {
        public static IEnumerable IsAllowedTestCases
        {
            get
            {
                yield return new TestCaseData(TestItem.AddressA, 2ul).Returns(false).SetName("Filtered by contract.");
                yield return new TestCaseData(TestItem.AddressB, 0ul).Returns(false).SetName("Filtered by base limit.");
                yield return new TestCaseData(TestItem.AddressA, 5ul).Returns(true).SetName("Allowed by contract.");
                yield return new TestCaseData(TestItem.AddressB, 1ul).Returns(true).SetName("Allowed by base limit.");
            }
        }

        [TestCaseSource(nameof(IsAllowedTestCases))]
        public bool is_allowed_returns_correct(Address address, ulong gasLimit)
        {
            MinGasPriceContractTxFilter txFilter = CreateFilter();
            Transaction tx = Build.A.Transaction.WithTo(address).WithGasPrice(gasLimit).WithData(null).TestObject;

            return txFilter.IsAllowed(tx, Build.A.BlockHeader.TestObject, ReleaseSpecSubstitute.Create());
        }

        [TestCaseSource(nameof(IsAllowedTestCases))]
        public bool is_allowed_with_a_context_returns_correct(Address address, ulong gasLimit)
        {
            MinGasPriceContractTxFilter txFilter = CreateFilter();
            Transaction tx = Build.A.Transaction.WithTo(address).WithGasPrice(gasLimit).WithData(null).TestObject;
            TxFilterContext context = new(Build.A.BlockHeader.TestObject, ReleaseSpecSubstitute.Create());

            return txFilter.IsAllowed(tx, in context);
        }

        [Test]
        public void Contract_floor_is_checked_with_the_base_fee_of_the_context()
        {
            MinGasPriceContractTxFilter txFilter = CreateFilter();
            ReleaseSpec london = new() { IsEip1559Enabled = true };
            // The parent used its gas target, so its next base fee is 1 wei and the premium 9.
            BlockHeader parent = Build.A.BlockHeader.WithNumber(10).WithBaseFee(1).WithGasLimit(30_000_000).WithGasUsed(15_000_000).TestObject;
            Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressA).WithType(TxType.EIP1559)
                .WithMaxFeePerGas(10).WithMaxPriorityFeePerGas(10).WithData(null).TestObject;
            // With a base fee of 6 wei the premium is 4, below the contract's floor of 5.
            TxFilterContext context = new(parent, london, 6);

            using (Assert.EnterMultipleScope())
            {
                Assert.That((bool)txFilter.IsAllowed(tx, parent, london), Is.True);
                Assert.That(txFilter.IsAllowed(tx, in context).ToString(), Is.EqualTo(
                    $"{AcceptTxResult.FeeTooLow}, EffectivePriorityFeePerGas too low 4 < 5, BaseFee: 6"));
            }
        }

        private static MinGasPriceContractTxFilter CreateFilter()
        {
            BlocksConfig blocksConfig = new()
            {
                MinGasPrice = UInt256.One
            };
            IMinGasPriceTxFilter minGasPriceFilter = new MinGasPriceTxFilter(blocksConfig);
            IDictionaryContractDataStore<TxPriorityContract.Destination> dictionaryContractDataStore = Substitute.For<IDictionaryContractDataStore<TxPriorityContract.Destination>>();
            dictionaryContractDataStore.TryGetValue(
                    Arg.Any<BlockHeader>(),
                    Arg.Is<TxPriorityContract.Destination>(static d => d.Target == TestItem.AddressA),
                    out Arg.Any<TxPriorityContract.Destination>())
                .Returns(static x =>
                {
                    x[2] = new TxPriorityContract.Destination(TestItem.AddressA, [], 5);
                    return true;
                });

            return new MinGasPriceContractTxFilter(minGasPriceFilter, dictionaryContractDataStore);
        }
    }
}
