// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Callback interface for collecting per-transaction execution timing.
/// Implemented by benchmark classes that need tx-level timing breakdowns.
/// </summary>
internal interface ITxExecutionTimingCollector
{
    void AddTxExecutionTiming(TxType txType, long elapsedTicks);
}

/// <summary>
/// Decorator that wraps an <see cref="ITransactionProcessor"/> and measures
/// the elapsed ticks for each <see cref="Execute"/> call, reporting them via
/// <see cref="ITxExecutionTimingCollector"/>.
/// Shared between NewPayload and NewPayloadMeasured benchmarks.
/// </summary>
internal sealed class TimedTransactionProcessor(ITransactionProcessor inner, ITxExecutionTimingCollector owner) : ITransactionProcessor
{
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        long start = Stopwatch.GetTimestamp();
        TransactionResult result = inner.Execute(transaction, txTracer);
        long elapsedTicks = Stopwatch.GetTimestamp() - start;
        owner.AddTxExecutionTiming(transaction.Type, elapsedTicks);
        return result;
    }

    public TransactionResult CallAndRestore(Transaction transaction, ITxTracer txTracer) =>
        inner.CallAndRestore(transaction, txTracer);

    public TransactionResult BuildUp(Transaction transaction, ITxTracer txTracer) =>
        inner.BuildUp(transaction, txTracer);

    public TransactionResult Trace(Transaction transaction, ITxTracer txTracer) =>
        inner.Trace(transaction, txTracer);

    public TransactionResult Warmup(Transaction transaction, ITxTracer txTracer) =>
        inner.Warmup(transaction, txTracer);

    public void SetBlockExecutionContext(BlockHeader blockHeader) =>
        inner.SetBlockExecutionContext(blockHeader);

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
        inner.SetBlockExecutionContext(in blockExecutionContext);
}
