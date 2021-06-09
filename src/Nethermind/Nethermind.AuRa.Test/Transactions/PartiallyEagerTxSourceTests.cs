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
        private static object[] GetArgs(params Transaction[] txs) => new object[] {txs};

        private static readonly Transaction _generatedTx = Build.A.GeneratedTransaction.TestObject;
        private static readonly Transaction _tx = Build.A.Transaction.TestObject;
        
        public static IEnumerable TestCases
        {
            get
            {
                yield return new TestCaseData(GetArgs()) {TestName = "empty_transactions"};
                yield return new TestCaseData(GetArgs(_generatedTx)) {TestName = "single_eager_transaction"};
                yield return new TestCaseData(GetArgs(_tx)) {TestName = "single_lazy_transaction"};
                yield return new TestCaseData(GetArgs(_generatedTx, _generatedTx, _generatedTx, _tx, _tx, _tx, _tx)) {TestName = "multiple_transactions"};
            }
        }

        [TestCaseSource(nameof(TestCases))]
        public void Eagerly_prepares_needed_transactions(Transaction[] txs)
        {
            MockTxSource mockTxSource = new(){Transactions = txs};
            PartiallyEagerTxSource txSource = new(mockTxSource, EagerTransaction);
            BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
            txSource.PrepareEagerTransactions(blockHeader, 0);
            mockTxSource.Index.Should().Be(txs.TakeWhile(EagerTransaction).Count());
            txSource.GetTransactions(blockHeader, 0).Should().BeEquivalentTo(txs.ToArray<object>());
        }

        private bool EagerTransaction(Transaction t) => t is GeneratedTransaction;

        [Test]
        public void Multiple_evaluations_are_correct()
        {
            Transaction[] txs = {_generatedTx, _generatedTx, _generatedTx, _tx, _tx, _tx, _tx};
            MockTxSource mockTxSource = new(){Transactions = txs};
            PartiallyEagerTxSource txSource = new(mockTxSource, EagerTransaction);
            BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
            txSource.PrepareEagerTransactions(blockHeader, 0);
            IEnumerable<Transaction> transactions = txSource.GetTransactions(blockHeader, 0);
            transactions.Should().BeEquivalentTo(txs.ToArray<object>());
            txs = new[] {_generatedTx, _tx};
            mockTxSource.Transactions = txs;
            txSource.PrepareEagerTransactions(blockHeader, 0);
            transactions = txSource.GetTransactions(blockHeader, 0); 
            transactions.Should().BeEquivalentTo(txs.ToArray<object>());
        }

        private class MockTxSource : ITxSource
        {
            public Transaction[] Transactions { get; set; }

            public int Index { get; private set; } = -1;
            
            public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
            {
                Index = 0;
                
                for (int i = 0; i < Transactions.Length; i++)
                {
                    yield return Transactions[i];
                    Index++;
                }
            }
        }
    }
}
