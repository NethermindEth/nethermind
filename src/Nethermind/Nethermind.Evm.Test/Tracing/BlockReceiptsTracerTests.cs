// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture]
    public class BlockReceiptsTracerTests
    {
        [Test]
        public void Forwards_current_transaction_cancellation([Values] bool nested)
        {
            using CancellationTokenSource cancellation = new();
            Transaction first = Build.A.Transaction.WithNonce(0).TestObject;
            Transaction second = Build.A.Transaction.WithNonce(1).TestObject;
            using CancellationTxTracer transactionTracer = new(NullTxTracer.Instance, cancellation.Token);
            IBlockTracer blockTracer = Substitute.For<IBlockTracer>();
            blockTracer.StartNewTxTrace(first).Returns(transactionTracer);
            blockTracer.StartNewTxTrace(second).Returns(NullTxTracer.Instance);
            using BlockReceiptsTracer? inner = nested ? new() : null;
            if (inner is not null)
            {
                inner.SetOtherTracer(blockTracer);
                blockTracer = inner;
            }
            using BlockReceiptsTracer tracer = new();
            ITxTracer capabilities = tracer;
            tracer.SetOtherTracer(blockTracer);
            tracer.StartNewBlockTrace(Build.A.Block.WithTransactions(first, second).TestObject);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(capabilities.IsCancelable, Is.False);
                Assert.That(capabilities.IsCancelled, Is.False);
            }

            tracer.StartNewTxTrace(first);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(capabilities.IsCancelable, Is.True);
                Assert.That(capabilities.IsCancelled, Is.False);
            }
            cancellation.Cancel();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(capabilities.IsCancelable, Is.True);
                Assert.That(capabilities.IsCancelled, Is.True);
            }

            tracer.EndTxTrace();
            tracer.StartNewTxTrace(second);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(capabilities.IsCancelable, Is.False);
                Assert.That(capabilities.IsCancelled, Is.False);
            }
        }

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
        public void Reads_the_transaction_output_only_for_another_tracer([Values] bool wrapsTracer)
        {
            Transaction tx = Build.A.Transaction.TestObject;
            BlockReceiptsTracer tracer = new();
            if (wrapsTracer)
            {
                IBlockTracer blockTracer = Substitute.For<IBlockTracer>();
                blockTracer.StartNewTxTrace(tx).Returns(Substitute.For<ITxTracer>());
                tracer.SetOtherTracer(blockTracer);
            }
            tracer.StartNewBlockTrace(Build.A.Block.WithTransactions(tx).TestObject);

            tracer.StartNewTxTrace(tx);

            Assert.That(tracer.IsTracingOutput, Is.EqualTo(wrapsTracer));
        }

        [Test]
        public void Reads_the_transaction_output_for_a_composite_of_block_tracers()
        {
            Transaction tx = Build.A.Transaction.TestObject;
            CompositeBlockTracer composite = new();
            for (int i = 0; i < 2; i++)
            {
                IBlockTracer blockTracer = Substitute.For<IBlockTracer>();
                blockTracer.StartNewTxTrace(tx).Returns(Substitute.For<ITxTracer>());
                composite.Add(blockTracer);
            }

            BlockReceiptsTracer tracer = new();
            tracer.SetOtherTracer(composite.GetTracer());
            tracer.StartNewBlockTrace(Build.A.Block.WithTransactions(tx).TestObject);

            tracer.StartNewTxTrace(tx);

            Assert.That(tracer.IsTracingOutput, Is.True, "plugin tracers and produced-block dumps run through a composite");
        }

        [Test]
        public void Without_receipts_keeps_block_gas_and_forwards_the_outcome([Values] bool failedFrame)
        {
            Transaction frameTx = Build.A.Transaction.WithType(TxType.FrameTx).TestObject;
            Transaction failedTx = Build.A.Transaction.TestObject;
            Block block = Build.A.Block.WithTransactions(frameTx, failedTx).TestObject;
            ITxTracer txTracer = Substitute.For<ITxTracer>();
            txTracer.IsTracingReceipt.Returns(true);
            IBlockTracer blockTracer = Substitute.For<IBlockTracer>();
            blockTracer.StartNewTxTrace(Arg.Any<Transaction>()).Returns(txTracer);
            BlockReceiptsTracer tracer = new() { BuildsReceipts = false };
            tracer.SetOtherTracer(blockTracer);
            tracer.StartNewBlockTrace(block);
            TxFrameReceipt[] frames = [new(failedFrame ? StatusCode.Failure : StatusCode.Success, 10, 0, [])];

            tracer.StartNewTxTrace(frameTx);
            tracer.ReportFrameTxReceipt(TestItem.AddressA, frames);
            tracer.MarkAsSuccess(TestItem.AddressB, 100, [], []);
            tracer.EndTxTrace();
            tracer.StartNewTxTrace(failedTx);
            tracer.MarkAsFailed(TestItem.AddressB, 200, [], "error");
            tracer.EndTxTrace();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracer.TxReceipts.Length, Is.EqualTo(0));
                Assert.That(tracer.CumulativeExecutionGasUsed, Is.EqualTo(300ul));
                Assert.That(block.Header.GasUsed, Is.EqualTo(300ul));
            }
            txTracer.Received(failedFrame ? 0 : 1).MarkAsSuccess(TestItem.AddressB, Arg.Any<GasConsumed>(), Arg.Any<byte[]>(), Arg.Any<LogEntry[]>(), Arg.Any<Hash256?>());
            txTracer.Received(failedFrame ? 2 : 1).MarkAsFailed(TestItem.AddressB, Arg.Any<GasConsumed>(), Arg.Any<byte[]>(), Arg.Any<string?>(), Arg.Any<Hash256?>());
        }

        [Test]
        public void Collects_logs_only_for_receipts_or_a_tracer_that_reads_them(
            [Values] bool buildsReceipts, [Values] bool tracerCollectsLogs, [Values] bool nestedReceiptsTracer)
        {
            Transaction tx = Build.A.Transaction.TestObject;
            ITxTracer txTracer = Substitute.For<ITxTracer>();
            txTracer.IsCollectingLogs.Returns(tracerCollectsLogs);
            IBlockTracer blockTracer = Substitute.For<IBlockTracer>();
            blockTracer.StartNewTxTrace(tx).Returns(txTracer);
            if (nestedReceiptsTracer)
            {
                BlockReceiptsTracer nested = new();
                nested.SetOtherTracer(blockTracer);
                blockTracer = nested;
            }
            BlockReceiptsTracer tracer = new() { BuildsReceipts = buildsReceipts };
            tracer.SetOtherTracer(blockTracer);
            tracer.StartNewBlockTrace(Build.A.Block.WithTransactions(tx).TestObject);

            tracer.StartNewTxTrace(tx);

            Assert.That(tracer.IsCollectingLogs, Is.EqualTo(buildsReceipts || tracerCollectsLogs || nestedReceiptsTracer));
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

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracer.TxReceipts.Length, Is.EqualTo(3));
                Assert.That(wrappedTracer.TxReceipts.Length, Is.EqualTo(3));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracer.TxReceipts[2], Is.SameAs(receipt));
                Assert.That(wrappedTracer.TxReceipts[2], Is.SameAs(receipt));
            }
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

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracer.TxReceipts.Length, Is.EqualTo(0));
                Assert.That(tracer.InnerTracer, Is.SameAs(NullTxTracer.Instance));
            }
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

        /// <summary>A producer that leaves an executed transaction out must not keep its receipt, block gas or fees.</summary>
        [Test]
        public void DiscardLastTx_drops_the_receipt_gas_and_fees_of_the_last_transaction()
        {
            Transaction first = Build.A.Transaction.WithNonce(0).TestObject;
            Transaction discarded = Build.A.Transaction.WithNonce(1).TestObject;
            Transaction last = Build.A.Transaction.WithNonce(2).TestObject;
            Block block = Build.A.Block.WithTransactions(first, discarded, last).TestObject;
            FeesTracer fees = new();
            BlockReceiptsTracer tracer = new();
            tracer.SetOtherTracer(fees);
            tracer.StartNewBlockTrace(block);

            Trace(first, 100, 10);
            Trace(discarded, 200, 20);
            tracer.DiscardLastTx();
            Trace(last, 300, 30);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracer.TxReceipts.Length, Is.EqualTo(2));
                Assert.That(tracer.TxReceipts[1].Index, Is.EqualTo(1));
                Assert.That(tracer.TxReceipts[1].GasUsedTotal, Is.EqualTo(400ul));
                Assert.That(tracer.CumulativeExecutionGasUsed, Is.EqualTo(400ul));
                Assert.That(block.Header.GasUsed, Is.EqualTo(400ul));
                Assert.That(fees.Fees, Is.EqualTo((UInt256)40));
            }

            void Trace(Transaction tx, ulong gas, ulong fee)
            {
                tracer.StartNewTxTrace(tx);
                tracer.ReportFees(fee, 0);
                tracer.MarkAsSuccess(TestItem.AddressA, gas, [], []);
                tracer.EndTxTrace();
            }
        }
    }
}
