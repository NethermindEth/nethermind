// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class TransactionEipsSupportTests
    {
        [TestCase(TxType.AccessList, true, false, false)]
        [TestCase(TxType.EIP1559, true, true, false)]
        [TestCase(TxType.Blob, true, true, true)]
        public void When_to_not_empty_then_is_message_call(TxType txType, bool isEip2930Supported,
            bool isEip1559Supported, bool isEip4844Supported)
        {
            Transaction transaction = new() { Type = txType };
            Assert.That(transaction.IsEip2930, Is.EqualTo(isEip2930Supported));
            Assert.That(transaction.IsEip1559, Is.EqualTo(isEip1559Supported));
            Assert.That(transaction.IsEip4844, Is.EqualTo(isEip4844Supported));
        }
    }
}
