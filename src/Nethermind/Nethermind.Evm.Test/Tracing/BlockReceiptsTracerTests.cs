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

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Specs.Forks;
using NSubstitute;
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
            
            BlockReceiptsTracer tracer = new();
            tracer.SetOtherTracer(NullBlockTracer.Instance);
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(block.Transactions[0]);
            tracer.MarkAsSuccess(TestItem.AddressA, 100, new byte[0], new LogEntry[0], TestItem.KeccakF);
            
            Assert.AreEqual(TestItem.KeccakF, tracer.TxReceipts[0].PostTransactionState);
        }
        
        [Test]
        public void Sets_tx_type()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.WithChainId(1).WithType(TxType.AccessList).TestObject).TestObject;
            
            BlockReceiptsTracer tracer = new();
            tracer.SetOtherTracer(NullBlockTracer.Instance);
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(block.Transactions[0]);
            tracer.MarkAsSuccess(TestItem.AddressA, 100, new byte[0], new LogEntry[0]);

            tracer.TxReceipts[0].TxType.Should().Be(TxType.AccessList);
        }
        
        [Test]
        public void Sets_state_root_if_provided_on_failure()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
            
            BlockReceiptsTracer tracer = new();
            tracer.SetOtherTracer(NullBlockTracer.Instance);
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(block.Transactions[0]);
            tracer.MarkAsFailed(TestItem.AddressA, 100, new byte[0], "error", TestItem.KeccakF);
            
            Assert.AreEqual(TestItem.KeccakF, tracer.TxReceipts[0].PostTransactionState);
        }

        [Test]
        public void Invokes_other_tracer_mark_as_failed_if_other_block_tracer_is_tx_tracer_too()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
            
            IBlockTracer otherTracer = Substitute.For<IBlockTracer, ITxTracer>();
            BlockReceiptsTracer tracer = new();
            tracer.SetOtherTracer(otherTracer);
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(block.Transactions[0]);
            tracer.MarkAsFailed(TestItem.AddressA, 100, Array.Empty<byte>(), "error", TestItem.KeccakF);
            
            (otherTracer as ITxTracer).Received().MarkAsFailed(TestItem.AddressA, 100, Array.Empty<byte>(), "error", TestItem.KeccakF);
        }
        
        [Test]
        public void Invokes_other_tracer_mark_as_success_if_other_block_tracer_is_tx_tracer_too()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
            
            IBlockTracer otherTracer = Substitute.For<IBlockTracer, ITxTracer>();
            BlockReceiptsTracer tracer = new();
            tracer.SetOtherTracer(otherTracer);
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(block.Transactions[0]);
            var logEntries = new LogEntry[0];
            tracer.MarkAsSuccess(TestItem.AddressA, 100,Array.Empty<byte>(), logEntries, TestItem.KeccakF);
            
            (otherTracer as ITxTracer).Received().MarkAsSuccess(TestItem.AddressA, 100, Array.Empty<byte>(), logEntries, TestItem.KeccakF);
        }
    }
}
