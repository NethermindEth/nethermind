// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool.Comparison;
using NUnit.Framework;

namespace Nethermind.TxPool.Test
{
    public class CompetingTransactionEqualityComparerTests
    {
        public static IEnumerable TestCases
        {
            get
            {
                var transaction = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).TestObject;

                yield return new TestCaseData(null, null) { ExpectedResult = true };

                yield return new TestCaseData(transaction, null)
                {
                    ExpectedResult = false
                };

                yield return new TestCaseData(null, transaction)
                {
                    ExpectedResult = false
                };

                yield return new TestCaseData(transaction, Build.A.Transaction.WithSenderAddress(TestItem.AddressB).WithNonce(2).TestObject)
                {
                    ExpectedResult = false
                };

                yield return new TestCaseData(transaction, Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(4).TestObject)
                {
                    ExpectedResult = false
                };

                yield return new TestCaseData(transaction, transaction)
                {
                    ExpectedResult = true
                };

                yield return new TestCaseData(transaction, Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).TestObject)
                {
                    ExpectedResult = true
                };
            }
        }

        [TestCaseSource(nameof(TestCases))]
        public bool Equals_test(Transaction t1, Transaction t2) => CompetingTransactionEqualityComparer.Instance.Equals(t1, t2);

        [TestCaseSource(nameof(TestCases))]
        public bool HashCode_test(Transaction t1, Transaction t2) =>
            CompetingTransactionEqualityComparer.Instance.GetHashCode(t1) == CompetingTransactionEqualityComparer.Instance.GetHashCode(t2);
    }
}
