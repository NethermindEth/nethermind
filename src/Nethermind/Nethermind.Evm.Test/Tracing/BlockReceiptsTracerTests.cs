// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
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
        public void Nested_receipts_tracers_retain_frame_data([Values] bool failedFrame, [Values] bool blockHandlesFrames)
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.WithType(TxType.FrameTx).TestObject).TestObject;
            ITxTracer leaf = Substitute.For<ITxTracer, IFrameTxReceiptTracer>();
            IBlockTracer leafBlock = blockHandlesFrames
                ? Substitute.For<IBlockTracer, IFrameTxReceiptTracer>()
                : Substitute.For<IBlockTracer>();
            leafBlock.StartNewTxTrace(Arg.Any<Transaction>()).Returns(leaf);
            BlockReceiptsTracer inner = new();
            inner.SetOtherTracer(leafBlock);
            BlockReceiptsTracer middle = new();
            BlockReceiptsTracer outer = new();
            middle.SetOtherTracer(inner);
            outer.SetOtherTracer(middle);
            outer.StartNewBlockTrace(block);
            outer.StartNewTxTrace(block.Transactions[0]);
            TxFrameReceipt[] frames = [new(StatusCode.Success, 10, 0, []), new(failedFrame ? StatusCode.Failure : StatusCode.Success, 20, 0, [])];

            IFrameTxReceiptTracer leafFrames = (IFrameTxReceiptTracer)leaf;
            if (leafBlock is IFrameTxReceiptTracer blockFrames)
            {
                blockFrames.When(t => t.ReportFrameTxReceipt(TestItem.AddressA, frames))
                    .Do(_ => leafFrames.ReportFrameTxReceipt(TestItem.AddressA, frames));
            }

            outer.ReportFrameEnd(1, null);
            outer.ReportFramesRolledBack(0, 1);
            outer.ReportFrameTxReceipt(TestItem.AddressA, frames);
            outer.MarkAsSuccess(TestItem.AddressB, 100, [], []);

            leafFrames.Received(1).ReportFrameTxReceipt(TestItem.AddressA, frames);
            leafFrames.Received(1).ReportFrameEnd(1, null);
            leafFrames.Received(1).ReportFramesRolledBack(0, 1);
            if (leafBlock is IFrameTxReceiptTracer receivingBlock)
            {
                receivingBlock.Received(1).ReportFrameTxReceipt(TestItem.AddressA, frames);
                receivingBlock.DidNotReceive().ReportFrameEnd(Arg.Any<int>(), Arg.Any<EvmExceptionType?>());
                receivingBlock.DidNotReceive().ReportFramesRolledBack(Arg.Any<int>(), Arg.Any<int>());
            }
            BlockReceiptsTracer[] tracers = [outer, middle, inner];
            using (Assert.EnterMultipleScope())
            {
                foreach (BlockReceiptsTracer tracer in tracers)
                {
                    Assert.That(tracer.TxReceipts[0].StatusCode, Is.EqualTo(failedFrame ? StatusCode.Failure : StatusCode.Success));
                    Assert.That(tracer.TxReceipts[0].Payer, Is.EqualTo(TestItem.AddressA));
                    Assert.That(tracer.TxReceipts[0].FrameReceipts, Is.SameAs(frames));
                }
            }
        }

        [Test]
        public void Sets_state_root_if_provided_on_success()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;

            BlockReceiptsTracer tracer = new();
            tracer.SetOtherTracer(NullBlockTracer.Instance);
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(block.Transactions[0]);
            tracer.MarkAsSuccess(TestItem.AddressA, 100, [], [], TestItem.KeccakF);

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
            tracer.MarkAsSuccess(TestItem.AddressA, 100, [], []);

            Assert.That(tracer.TxReceipts[0].TxType, Is.EqualTo(TxType.AccessList));
        }

        [Test]
        public void Sets_state_root_if_provided_on_failure()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;

            BlockReceiptsTracer tracer = new();
            tracer.SetOtherTracer(NullBlockTracer.Instance);
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(block.Transactions[0]);
            tracer.MarkAsFailed(TestItem.AddressA, 100, [], "error", TestItem.KeccakF);

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
            tracer.MarkAsFailed(TestItem.AddressA, 100, [], "error", TestItem.KeccakF);

            (otherTracer as ITxTracer).Received().MarkAsFailed(TestItem.AddressA, 100, [], "error", TestItem.KeccakF);
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
            LogEntry[] logEntries = [];
            tracer.MarkAsSuccess(TestItem.AddressA, 100, [], logEntries, TestItem.KeccakF);

            (otherTracer as ITxTracer).Received().MarkAsSuccess(TestItem.AddressA, 100, [], logEntries, TestItem.KeccakF);
        }

        [Test]
        public void SetReceipt_forwards_to_wrapped_receipts_tracer()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;

            BlockReceiptsTracer wrappedTracer = new();
            BlockReceiptsTracer tracer = new();
            tracer.SetOtherTracer(wrappedTracer);
            tracer.StartNewBlockTrace(block);
            TxReceipt receipt = new() { TxHash = TestItem.KeccakA };

            tracer.SetReceipt(2, receipt);

            Assert.That(tracer.TxReceipts.Length, Is.EqualTo(3));
            Assert.That(wrappedTracer.TxReceipts.Length, Is.EqualTo(3));
            Assert.That(tracer.TxReceipts[2], Is.SameAs(receipt));
            Assert.That(wrappedTracer.TxReceipts[2], Is.SameAs(receipt));
        }

        [Test]
        public void EndBlockTrace_tolerates_harvested_receipt_gaps()
        {
            Block block = Build.A.Block.WithTransactions(
                Build.A.Transaction.TestObject,
                Build.A.Transaction.TestObject,
                Build.A.Transaction.TestObject).TestObject;

            BlockReceiptsTracer tracer = new();
            tracer.SetOtherTracer(NullBlockTracer.Instance);
            tracer.StartNewBlockTrace(block);
            tracer.SetReceipt(2, new TxReceipt { Logs = [] });

            Assert.DoesNotThrow(() => tracer.EndBlockTrace());
            Assert.That(block.Header.Bloom, Is.Not.Null);
        }

        [Test]
        public void ResetForParallelTx_clears_receipts_and_detaches_previous_other_tracer()
        {
            Block previousBlock = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
            Block nextBlock = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
            IBlockTracer previousOtherTracer = Substitute.For<IBlockTracer>();
            IBlockTracer nextOtherTracer = Substitute.For<IBlockTracer>();
            nextOtherTracer.StartNewTxTrace(Arg.Any<Transaction?>()).Returns(NullTxTracer.Instance);

            BlockReceiptsTracer tracer = new(true);
            tracer.SetOtherTracer(previousOtherTracer);
            tracer.StartNewBlockTrace(previousBlock);
            tracer.StartNewTxTrace(previousBlock.Transactions[0]);
            tracer.MarkAsSuccess(TestItem.AddressA, 100, [], []);

            tracer.ResetForParallelTx(nextBlock, nextOtherTracer);

            Assert.That(tracer.TxReceipts.Length, Is.EqualTo(0));
            Assert.That(tracer.InnerTracer, Is.SameAs(NullTxTracer.Instance));
            previousOtherTracer.Received(1).StartNewBlockTrace(previousBlock);
            previousOtherTracer.DidNotReceive().StartNewBlockTrace(nextBlock);
            nextOtherTracer.DidNotReceive().StartNewBlockTrace(nextBlock);

            tracer.StartNewTxTrace(nextBlock.Transactions[0]);

            nextOtherTracer.Received(1).StartNewTxTrace(nextBlock.Transactions[0]);
        }

        [Test]
        public void ResetForParallelTx_does_not_reserve_capacity_for_the_whole_block([Values(1_000, 100_000)] int txCount)
        {
            Transaction tx = Build.A.Transaction.TestObject;
            Transaction[] txs = new Transaction[txCount];
            Array.Fill(txs, tx);
            Block block = Build.A.Block.WithTransactions(txs).TestObject;
            BlockReceiptsTracer tracer = new(true);

            long before = GC.GetAllocatedBytesForCurrentThread();
            tracer.ResetForParallelTx(block, NullBlockTracer.Instance);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.LessThan(1024),
                "a parallel tracer must reserve receipt capacity for its single transaction, not for the block");
        }
    }
}
