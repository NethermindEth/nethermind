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

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture]
    public class BlockReceiptsTracerTests
    {
        [Test]
        public void Sets_state_root_if_provided_on_success()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
            
            BlockReceiptsTracer tracer = new BlockReceiptsTracer();
            tracer.SetOtherTracer(NullBlockTracer.Instance);
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(block.Transactions[0].Hash);
            tracer.MarkAsSuccess(TestItem.AddressA, 100, new byte[0], new LogEntry[0], TestItem.KeccakF);
            
            Assert.AreEqual(TestItem.KeccakF, tracer.TxReceipts[0].PostTransactionState);
        }
        
        [Test]
        public void Sets_state_root_if_provided_on_failure()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
            
            BlockReceiptsTracer tracer = new BlockReceiptsTracer();
            tracer.SetOtherTracer(NullBlockTracer.Instance);
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(block.Transactions[0].Hash);
            tracer.MarkAsFailed(TestItem.AddressA, 100, new byte[0], "error", TestItem.KeccakF);
            
            Assert.AreEqual(TestItem.KeccakF, tracer.TxReceipts[0].PostTransactionState);
        }
    }
}