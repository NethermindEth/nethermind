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

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class TxTraceFilterTests
    {
        [Test]
        public void Trace_filter_should_skip_expected_number_of_transactions()
        {
            TxTraceFilter traceFilter = new(null, null, 2, null, LimboLogs.Instance);
            Transaction tx1 = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithNonce(0).TestObject;
            Transaction tx2 = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithNonce(1).TestObject;
            Transaction tx3 = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithNonce(2).TestObject;
            Assert.AreEqual(false, traceFilter.ShouldTraceTx(tx1));
            Assert.AreEqual(false, traceFilter.ShouldTraceTx(tx2));
            Assert.AreEqual(true, traceFilter.ShouldTraceTx(tx3));
        }
        
        [Test]
        public void Trace_filter_should_skip_expected_number_of_blocks()
        {
            TxTraceFilter traceFilter = new(null, null, 2, null, LimboLogs.Instance);

            Transaction[] firstBlockTxs = {
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithNonce(0).TestObject,
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithNonce(1).TestObject
            };
            
            Transaction[] secondBlockTxs = {
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithNonce(2).TestObject
            };
            
            Block firstBlock = Build.A.Block.WithNumber(1).WithTransactions(firstBlockTxs).TestObject;
            Block secondBlock = Build.A.Block.WithNumber(1).WithTransactions(secondBlockTxs).TestObject;
            Assert.AreEqual(false, traceFilter.ShouldTraceBlock(firstBlock));
            Assert.AreEqual(true, traceFilter.ShouldTraceBlock(secondBlock));
        }
        
        [Test]
        public void Trace_filter_should_trace_block_with_given_from_address()
        {
            TxTraceFilter traceFilter = new(new []{ TestItem.AddressA }, null, 0, null, LimboLogs.Instance);

            Transaction[] firstBlockTxs = {
                Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithNonce(0).TestObject,
            };
            
            Block firstBlock = Build.A.Block.WithNumber(1).WithTransactions(firstBlockTxs).TestObject;
            Assert.AreEqual(true, traceFilter.ShouldTraceBlock(firstBlock));
        }
    }
}
