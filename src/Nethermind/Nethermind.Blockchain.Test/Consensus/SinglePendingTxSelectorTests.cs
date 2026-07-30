// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [Parallelizable(ParallelScope.All)]
    public class SinglePendingTxSelectorTests
    {
        private readonly BlockHeader _anyParent = Build.A.BlockHeader.TestObject;

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void To_string_does_not_throw()
        {
            ITxSource txSource = Substitute.For<ITxSource>();
            SinglePendingTxSelector selector = new(txSource);
            _ = selector.ToString();
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void When_no_transactions_returns_empty_list()
        {
            ITxSource txSource = Substitute.For<ITxSource>();
            SinglePendingTxSelector selector = new(txSource);
            Assert.That(System.Linq.Enumerable.Count(selector.GetTransactions(_anyParent, 1000000)), Is.EqualTo(0));
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
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
            Transaction[] result = selector.GetTransactions(_anyParent, 1000000).ToArray();
            Assert.That(result.Length, Is.EqualTo(1));
            Assert.That(result[0].Timestamp, Is.EqualTo((UInt256)8));
            Assert.That(result[0].Nonce, Is.EqualTo(1ul));
        }
    }
}
