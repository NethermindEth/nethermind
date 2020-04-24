//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class InjectionPendingTxSelectorTests
    {
        [Test]
        public void selectTransactions_injects_transactions_from_ImmediateTransactionSources_in_front_of_block_transactions()
        {
            IImmediateTransactionSource CreateImmediateTransactionSource(BlockHeader header, int limit, Address address, List<Transaction> txs, bool createsTransaction)
            {
                var immediateTransactionSource = Substitute.For<IImmediateTransactionSource>();
                immediateTransactionSource.TryCreateTransaction(header, limit, out Arg.Any<Transaction>()).Returns(x =>
                {
                    if (createsTransaction)
                    {
                        var transaction = Build.A.Transaction.To(address).WithGasPrice(UInt256.Zero).TestObject;
                        txs.Add(transaction);
                        x[2] = transaction;
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                });
                return immediateTransactionSource;
            }
            
            var parentHeader = Build.A.BlockHeader.TestObject;
            var gasLimit = 1000;
            List<Transaction> expected = new List<Transaction>();
            
            var innerPendingTxSelector = Substitute.For<IPendingTxSelector>();
            
            var transactionFiller = Substitute.For<ITransactionFiller>();

            var immediateTransactionSource1 = CreateImmediateTransactionSource(parentHeader, gasLimit, TestItem.AddressB, expected, true);
            var immediateTransactionSource2 = CreateImmediateTransactionSource(parentHeader, gasLimit, TestItem.AddressC, expected, false);
            var immediateTransactionSource3 = CreateImmediateTransactionSource(parentHeader, gasLimit, TestItem.AddressD, expected, true);
            
            var originalTxs = Build.A.Transaction.TestObjectNTimes(5);
            innerPendingTxSelector.SelectTransactions(parentHeader, gasLimit).Returns(originalTxs);

            var injectionPendingTxSelector = new InjectionPendingTxSelector(
                innerPendingTxSelector, transactionFiller, immediateTransactionSource1, immediateTransactionSource2, immediateTransactionSource3);
            
            var transactions = injectionPendingTxSelector.SelectTransactions(parentHeader, gasLimit).ToArray();
            expected.AddRange(originalTxs);
            
            transactionFiller.Received().Fill(parentHeader, transactions[0]);
            transactionFiller.Received().Fill(parentHeader, transactions[1]);
            transactions.Should().BeEquivalentTo(expected);
        }
    }
}