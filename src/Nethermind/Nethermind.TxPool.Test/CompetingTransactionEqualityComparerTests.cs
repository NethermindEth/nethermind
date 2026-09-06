// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool.Comparison;
using NUnit.Framework;

namespace Nethermind.TxPool.Test
{
    [Parallelizable(ParallelScope.All)]
    public class CompetingTransactionEqualityComparerTests
    {
        public static IEnumerable TestCases
        {
            get
            {
                Transaction transaction = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).TestObject;

                yield return new TestCaseData(null, null).Returns(true).SetArgDisplayNames("Both_null");
                yield return new TestCaseData(transaction, null).Returns(false).SetArgDisplayNames("Left_transaction_right_null");
                yield return new TestCaseData(null, transaction).Returns(false).SetArgDisplayNames("Left_null_right_transaction");
                yield return new TestCaseData(transaction, Build.A.Transaction.WithSenderAddress(TestItem.AddressB).WithNonce(2).TestObject).Returns(false).SetArgDisplayNames("Different_sender");
                yield return new TestCaseData(transaction, Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(4).TestObject).Returns(false).SetArgDisplayNames("Different_nonce");
                yield return new TestCaseData(transaction, transaction).Returns(true).SetArgDisplayNames("Same_instance");
                yield return new TestCaseData(transaction, Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).TestObject).Returns(true).SetArgDisplayNames("Same_sender_and_nonce");
            }
        }

        [TestCaseSource(nameof(TestCases))]
        public bool Equals_test(Transaction t1, Transaction t2) => CompetingTransactionEqualityComparer.Instance.Equals(t1, t2);

        [TestCaseSource(nameof(TestCases))]
        public bool HashCode_test(Transaction t1, Transaction t2) =>
            CompetingTransactionEqualityComparer.Instance.GetHashCode(t1) == CompetingTransactionEqualityComparer.Instance.GetHashCode(t2);
    }
}
