// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    public class SinglePendingTxSelectorTests
    {
        private BlockHeader _anyParent = Build.A.BlockHeader.TestObject;

        [Test, Timeout(Timeout.MaxTestTime)]
        public void To_string_does_not_throw()
        {
            ITxSource txSource = Substitute.For<ITxSource>();
            SinglePendingTxSelector selector = new(txSource);
            _ = selector.ToString();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Throws_on_null_argument()
        {
            Assert.Throws<ArgumentNullException>(() => new SinglePendingTxSelector(null));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void When_no_transactions_returns_empty_list()
        {
            ITxSource txSource = Substitute.For<ITxSource>();
            SinglePendingTxSelector selector = new(txSource);
            selector.GetTransactions(_anyParent, 1000000).Should().HaveCount(0);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void When_many_transactions_returns_one_with_lowest_nonce_and_highest_timestamp()
        {
            ITxSource txSource = Substitute.For<ITxSource>();
            txSource.GetTransactions(_anyParent, 1000000).ReturnsForAnyArgs(new[]
            {
                Build.A.Transaction.WithNonce(6).TestObject,
                Build.A.Transaction.WithNonce(1).WithTimestamp(7).TestObject,
                Build.A.Transaction.WithNonce(9).TestObject,
                Build.A.Transaction.WithNonce(1).WithTimestamp(8).TestObject,
            });

            SinglePendingTxSelector selector = new(txSource);
            var result = selector.GetTransactions(_anyParent, 1000000).ToArray();
            result.Should().HaveCount(1);
            result[0].Timestamp.Should().Be(8);
            result[0].Nonce.Should().Be(1);
        }
    }
}
