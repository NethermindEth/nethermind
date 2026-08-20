// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismBlockReceiptTracerTests
{
    private static OptimismBlockReceiptTracer BuildTracer() =>
        new(Substitute.For<IOptimismSpecHelper>(), Substitute.For<IWorldState>());

    [Test]
    public void FrameTxReceipt_CarriesPayerAndFrameReceipts()
    {
        Transaction frameTx = Build.A.Transaction.WithType(TxType.FrameTx).WithSenderAddress(TestItem.AddressA).TestObject;
        Block block = Build.A.Block.WithTransactions(frameTx).TestObject;
        LogEntry frameLog = new(TestItem.AddressC, [1], [TestItem.KeccakA]);

        OptimismBlockReceiptTracer tracer = BuildTracer();
        tracer.StartNewBlockTrace(block);
        tracer.StartNewTxTrace(frameTx);
        tracer.ReportFrameTxReceipt(TestItem.AddressD, [new TxFrameReceipt(TxFrameReceipt.StatusFailure, 21_000, [frameLog])]);
        tracer.MarkAsSuccess(TestItem.AddressB, new GasConsumed(21_000, 21_000), [], [frameLog]);
        tracer.EndTxTrace();

        TxReceipt receipt = tracer.LastReceipt;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt, Is.InstanceOf<OptimismTxReceipt>());
            Assert.That(receipt.Payer, Is.EqualTo(TestItem.AddressD));
            Assert.That(receipt.FrameReceipts, Has.Length.EqualTo(1));
            Assert.That(receipt.StatusCode, Is.EqualTo(TxFrameReceipt.StatusFailure), "status is aggregated from the frames");
        }
    }

    [Test]
    public void FrameTxReceipt_DoesNotLeakIntoTheNextFailingTransaction()
    {
        Transaction frameTx = Build.A.Transaction.WithType(TxType.FrameTx).WithSenderAddress(TestItem.AddressA).TestObject;
        Transaction plainTx = Build.A.Transaction.WithSenderAddress(TestItem.AddressB).TestObject;
        Block block = Build.A.Block.WithTransactions(frameTx, plainTx).TestObject;
        LogEntry frameLog = new(TestItem.AddressC, [1], [TestItem.KeccakA]);

        OptimismBlockReceiptTracer tracer = BuildTracer();
        tracer.StartNewBlockTrace(block);

        tracer.StartNewTxTrace(frameTx);
        tracer.ReportFrameTxReceipt(TestItem.AddressD, [new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, [frameLog])]);
        tracer.MarkAsSuccess(TestItem.AddressB, new GasConsumed(21_000, 21_000), [], [frameLog]);
        tracer.EndTxTrace();

        tracer.StartNewTxTrace(plainTx);
        tracer.MarkAsFailed(TestItem.AddressE, new GasConsumed(21_000, 21_000), [], "reverted");
        tracer.EndTxTrace();

        TxReceipt failedReceipt = tracer.LastReceipt;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failedReceipt.Logs, Is.Empty);
            Assert.That(failedReceipt.Payer, Is.Null);
            Assert.That(failedReceipt.FrameReceipts, Is.Null);
        }
    }
}
