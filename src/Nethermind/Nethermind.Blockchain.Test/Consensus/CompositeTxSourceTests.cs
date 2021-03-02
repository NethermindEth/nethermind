//  Copyright (c) 2021 Demerzel Solutions Limited
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
        [Test]
        public void To_string_does_not_throw()
        {
            ITxSource txSource = Substitute.For<ITxSource>();
            CompositeTxSource selector = new CompositeTxSource(txSource);
            _ = selector.ToString();
        } 
        
        [Test]
        public void Throws_on_null_argument()
        {
            Assert.Throws<ArgumentNullException>(() => new CompositeTxSource(null));
        } 
        
        [Test]
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
                        return new[] {transaction};
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
            List<Transaction> expected = new List<Transaction>();
            
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
