// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    public class CompositeTxSourceTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void To_string_does_not_throw()
        {
            ITxSource txSource = Substitute.For<ITxSource>();
            CompositeTxSource selector = new(txSource);
            _ = selector.ToString();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Throws_on_null_argument()
        {
            Assert.Throws<ArgumentNullException>(() => new CompositeTxSource(null!));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void selectTransactions_injects_transactions_from_ImmediateTransactionSources_in_front_of_block_transactions()
        {
            ITxSource CreateImmediateTransactionSource(BlockHeader header, Address address, List<Transaction> txs, bool createsTransaction)
            {
                var immediateTransactionSource = Substitute.For<ITxSource>();
                immediateTransactionSource.GetTransactions(header, Arg.Any<long>()).Returns(x =>
                {
                    if (createsTransaction)
                    {
                        var transaction = Build.A.GeneratedTransaction.To(address).WithGasPrice(UInt256.Zero).TestObject;
                        txs.Add(transaction);
                        return new[] { transaction };
                    }
                    else
                    {
                        return Array.Empty<Transaction>();
                    }
                });
                return immediateTransactionSource;
            }

            var parentHeader = Build.A.BlockHeader.TestObject;
            var gasLimit = 1000;
            List<Transaction> expected = new();

            var innerPendingTxSelector = Substitute.For<ITxSource>();

            var immediateTransactionSource1 = CreateImmediateTransactionSource(parentHeader, TestItem.AddressB, expected, true);
            var immediateTransactionSource2 = CreateImmediateTransactionSource(parentHeader, TestItem.AddressC, expected, false);
            var immediateTransactionSource3 = CreateImmediateTransactionSource(parentHeader, TestItem.AddressD, expected, true);

            var originalTxs = Build.A.Transaction.TestObjectNTimes(5);
            innerPendingTxSelector.GetTransactions(parentHeader, Arg.Any<long>()).Returns(originalTxs);

            var compositeTxSource = new CompositeTxSource(
                immediateTransactionSource1, immediateTransactionSource2, immediateTransactionSource3, innerPendingTxSelector);

            var transactions = compositeTxSource.GetTransactions(parentHeader, gasLimit).ToArray();
            expected.AddRange(originalTxs);

            transactions.Should().BeEquivalentTo(expected);
        }
    }
}
