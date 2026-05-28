// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    }
}
