// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
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

            Assert.That(tracer.TxReceipts[0].PostTransactionState, Is.EqualTo(TestItem.KeccakF));
        }

        [Test]
        public void Sets_tx_type()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.WithChainId(TestBlockchainIds.ChainId).WithType(TxType.AccessList).TestObject).TestObject;

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

            Assert.That(tracer.TxReceipts[0].PostTransactionState, Is.EqualTo(TestItem.KeccakF));
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
            LogEntry[] logEntries = new LogEntry[0];
            tracer.MarkAsSuccess(TestItem.AddressA, 100, Array.Empty<byte>(), logEntries, TestItem.KeccakF);

            (otherTracer as ITxTracer).Received().MarkAsSuccess(TestItem.AddressA, 100, Array.Empty<byte>(), logEntries, TestItem.KeccakF);
        }
    }
}
