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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class PartiallyEagerTxSourceTests
    {
        public static IEnumerable TestCases
        {
            get
            {
                object[] GetArgs(params Transaction[] txs) => new object[] {txs};

                Transaction generatedTx = Build.A.GeneratedTransaction.TestObject;
                Transaction tx = Build.A.Transaction.TestObject;
                
                yield return new TestCaseData(GetArgs()) {TestName = "empty_transactions"};
                yield return new TestCaseData(GetArgs(generatedTx)) {TestName = "single_eager_transaction"};
                yield return new TestCaseData(GetArgs(tx)) {TestName = "single_lazy_transaction"};
                yield return new TestCaseData(GetArgs(generatedTx, generatedTx, generatedTx, tx, tx, tx, tx)) {TestName = "multiple_transactions"};
            }
        }

        [TestCaseSource(nameof(TestCases))]
        public void Eagerly_prepares_needed_transactions(Transaction[] txs)
        {
            bool EagerTransaction(Transaction t) => t is GeneratedTransaction;
            
            MockTxSource mockTxSource = new(txs);
            PartiallyEagerTxSource txSource = new(mockTxSource, EagerTransaction);

            BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
            txSource.PrepareEagerTransactions(blockHeader, 0);
            mockTxSource.Index.Should().Be(txs.TakeWhile(EagerTransaction).Count());
            txSource.GetTransactions(blockHeader, 0).Should().BeEquivalentTo(txs.ToArray<object>());
        }

        private class MockTxSource : ITxSource
        {
            private readonly Transaction[] _transactions;

            public MockTxSource(params Transaction[] transactions)
            {
                _transactions = transactions;
            }

            public int Index { get; private set; } = -1;
            
            public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
            {
                Index = 0;
                
                for (int i = 0; i < _transactions.Length; i++)
                {
                    yield return _transactions[i];
                    Index++;
                }
            }
        }
    }
}
