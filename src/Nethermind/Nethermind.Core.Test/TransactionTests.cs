// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class TransactionTests
    {
        [Test]
        public void When_to_not_empty_then_is_message_call()
        {
            Transaction transaction = new();
            transaction.To = Address.Zero;
            Assert.True(transaction.IsMessageCall, nameof(Transaction.IsMessageCall));
            Assert.False(transaction.IsContractCreation, nameof(Transaction.IsContractCreation));
        }

        [Test]
        public void When_to_empty_then_is_message_call()
        {
            Transaction transaction = new();
            transaction.To = null;
            Assert.False(transaction.IsMessageCall, nameof(Transaction.IsMessageCall));
            Assert.True(transaction.IsContractCreation, nameof(Transaction.IsContractCreation));
        }

        [TestCase(1, true)]
        [TestCase(300, true)]
        public void Supports1559_returns_expected_results(int decodedFeeCap, bool expectedSupports1559)
        {
            Transaction transaction = new();
            transaction.DecodedMaxFeePerGas = (uint)decodedFeeCap;
            transaction.Type = TxType.EIP1559;
            Assert.That(transaction.DecodedMaxFeePerGas, Is.EqualTo(transaction.MaxFeePerGas));
            Assert.That(transaction.Supports1559, Is.EqualTo(expectedSupports1559));
        }
    }
}
