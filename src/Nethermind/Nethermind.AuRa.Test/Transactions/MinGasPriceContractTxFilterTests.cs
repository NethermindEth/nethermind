// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
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
            BlocksConfig blocksConfig = new()
            {
                MinGasPrice = UInt256.One
            };
            IMinGasPriceTxFilter minGasPriceFilter = new MinGasPriceTxFilter(blocksConfig, Substitute.For<ISpecProvider>());
            IDictionaryContractDataStore<TxPriorityContract.Destination> dictionaryContractDataStore = Substitute.For<IDictionaryContractDataStore<TxPriorityContract.Destination>>();
            dictionaryContractDataStore.TryGetValue(
                    Arg.Any<BlockHeader>(),
                    Arg.Is<TxPriorityContract.Destination>(d => d.Target == TestItem.AddressA),
                    out Arg.Any<TxPriorityContract.Destination>())
                .Returns(x =>
                {
                    x[2] = new TxPriorityContract.Destination(TestItem.AddressA, Array.Empty<byte>(), 5);
                    return true;
                });

            MinGasPriceContractTxFilter txFilter = new(minGasPriceFilter, dictionaryContractDataStore);
            Transaction tx = Build.A.Transaction.WithTo(address).WithGasPrice(gasLimit).WithData(null).TestObject;

            return txFilter.IsAllowed(tx, Build.A.BlockHeader.TestObject);
        }
    }
}
