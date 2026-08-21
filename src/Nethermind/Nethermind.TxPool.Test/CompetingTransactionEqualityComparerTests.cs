// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
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

                // Under EIP-8250 a keyed transaction's slot is (sender, nonce_keys, nonce_seq); the set [0]
                // still aliases the account nonce.
                yield return new TestCaseData(KeyedTx([1]), KeyedTx([2])).Returns(false).SetArgDisplayNames("Different_nonce_keys");
                yield return new TestCaseData(KeyedTx([1, 2]), KeyedTx([1, 2])).Returns(true).SetArgDisplayNames("Same_nonce_keys");
                yield return new TestCaseData(KeyedTx([1]), Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).TestObject).Returns(false).SetArgDisplayNames("Keyed_and_account_nonce_domains");
                yield return new TestCaseData(KeyedTx([0]), Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).TestObject).Returns(true).SetArgDisplayNames("Nonce_key_zero_aliases_the_account_nonce");
            }
        }

        private static Transaction KeyedTx(UInt256[] nonceKeys) =>
            Build.A.Transaction
                .WithType(TxType.FrameTx)
                .WithSenderAddress(TestItem.AddressA)
                .WithNonce(2)
                .WithNonceKeys(nonceKeys)
                .TestObject;

        [TestCaseSource(nameof(TestCases))]
        public bool Equals_test(Transaction t1, Transaction t2) => CompetingTransactionEqualityComparer.Instance.Equals(t1, t2);

        [TestCaseSource(nameof(TestCases))]
        public bool HashCode_test(Transaction t1, Transaction t2) =>
            CompetingTransactionEqualityComparer.Instance.GetHashCode(t1) == CompetingTransactionEqualityComparer.Instance.GetHashCode(t2);
    }
}
