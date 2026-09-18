// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockReceiptGasAccountingKernelTests
{
    [TestCase(0ul, 0ul, 0ul, 0ul, 0ul, 0ul, 0ul, 0ul, 0ul, 0ul)]
    [TestCase(10ul, 20ul, 30ul, 4ul, 5ul, 6ul, 14ul, 25ul, 36ul, 25ul)]
    [TestCase(20ul, 10ul, 30ul, 5ul, 4ul, 6ul, 25ul, 14ul, 36ul, 25ul)]
    [TestCase(10ul, 10ul, 30ul, 5ul, 5ul, 6ul, 15ul, 15ul, 36ul, 15ul)]
    [TestCase(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 1ul, 2ul, 3ul, 0ul, 1ul, 2ul, 1ul)]
    public void Accumulate_uses_independent_fixed_width_counters_and_maximum(
        ulong previousExecutionGas,
        ulong previousStateGas,
        ulong previousReceiptGas,
        ulong transactionExecutionGas,
        ulong transactionStateGas,
        ulong transactionPaidGas,
        ulong expectedExecutionGas,
        ulong expectedStateGas,
        ulong expectedReceiptGas,
        ulong expectedHeaderGas)
    {
        BlockReceiptGasAccountingResult result = BlockReceiptGasAccountingKernel.Accumulate(
            previousExecutionGas,
            previousStateGas,
            previousReceiptGas,
            transactionExecutionGas,
            transactionStateGas,
            transactionPaidGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CumulativeExecutionGas, Is.EqualTo(expectedExecutionGas));
            Assert.That(result.CumulativeStateGas, Is.EqualTo(expectedStateGas));
            Assert.That(result.CumulativeReceiptGas, Is.EqualTo(expectedReceiptGas));
            Assert.That(result.HeaderGasUsed, Is.EqualTo(expectedHeaderGas));
        }
    }

    [TestCase(0ul, 0ul, 0ul, 0ul)]
    [TestCase(9ul, 12ul, 7ul, 12ul)]
    [TestCase(12ul, 9ul, 7ul, 12ul)]
    [TestCase(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue)]
    public void FromTotals_preserves_counters_and_recomputes_header(
        ulong executionGas,
        ulong stateGas,
        ulong receiptGas,
        ulong expectedHeaderGas)
    {
        BlockReceiptGasAccountingResult result = BlockReceiptGasAccountingKernel.FromTotals(
            executionGas,
            stateGas,
            receiptGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CumulativeExecutionGas, Is.EqualTo(executionGas));
            Assert.That(result.CumulativeStateGas, Is.EqualTo(stateGas));
            Assert.That(result.CumulativeReceiptGas, Is.EqualTo(receiptGas));
            Assert.That(result.HeaderGasUsed, Is.EqualTo(expectedHeaderGas));
        }
    }

    [Test]
    public void Block_receipts_tracer_restores_all_three_counters_to_snapshot()
    {
        Transaction first = Build.A.Transaction.TestObject;
        Transaction second = Build.A.Transaction.TestObject;
        Block block = Build.A.Block.WithTransactions(first, second).TestObject;
        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

        tracer.StartNewTxTrace(first);
        tracer.MarkAsSuccess(Address.Zero, new GasConsumed(8, 7, 10, 20), [], []);
        tracer.EndTxTrace();
        int snapshot = tracer.TakeSnapshot();

        tracer.StartNewTxTrace(second);
        tracer.MarkAsSuccess(Address.Zero, new GasConsumed(7, 6, 30, 5), [], []);
        tracer.EndTxTrace();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Header.GasUsed, Is.EqualTo(40));
            Assert.That(tracer.CumulativeExecutionGasUsed, Is.EqualTo(40));
            Assert.That(tracer.BlockStateGasUsed, Is.EqualTo(25));
            Assert.That(tracer.LastReceipt.GasUsedTotal, Is.EqualTo(15));
        }

        tracer.Restore(snapshot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.TxReceipts.Length, Is.EqualTo(1));
            Assert.That(block.Header.GasUsed, Is.EqualTo(20));
            Assert.That(tracer.CumulativeExecutionGasUsed, Is.EqualTo(10));
            Assert.That(tracer.BlockStateGasUsed, Is.EqualTo(20));
            Assert.That(tracer.LastReceipt.GasUsedTotal, Is.EqualTo(8));
        }
    }
}
