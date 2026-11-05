// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Trace;

/// <summary>
/// Traces as <see cref="TraceTransactionProcessorAdapter"/> does, running each transaction priced at zero with a zero
/// base fee and each blob transaction with a zero blob fee cap with a zero blob base fee, as eth_call runs them.
/// </summary>
/// <remarks>
/// trace_callMany traces its calls as the transactions of one block, which has a single base fee and blob base fee, so
/// they are cleared per transaction and restored before the next one and before the rest of block processing.
/// Zero-priced is the condition under which the processor skips gas validation and charges no fee;
/// <see cref="UnpricedBlobFeeCalculator"/> charges no blob fee under the same condition as the blob base fee here.
/// </remarks>
internal sealed class UnpricedCallTraceAdapter(ITransactionProcessor transactionProcessor) : ITransactionProcessorAdapter
{
    private BlockExecutionContext _blockExecutionContext;

    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        bool unpricedGas = transaction.MaxFeePerGas.IsZero && transaction.MaxPriorityFeePerGas.IsZero;
        bool unpricedBlobs = HasUnpricedBlobs(transaction);
        if (!unpricedGas && !unpricedBlobs) return transactionProcessor.Trace(transaction, txTracer);

        BlockHeader header = _blockExecutionContext.Header;
        UInt256 baseFee = header.BaseFeePerGas;
        if (unpricedGas) header.BaseFeePerGas = UInt256.Zero;
        if (unpricedBlobs)
        {
            transactionProcessor.SetBlockExecutionContext(BlockExecutionContext.WithPrevRandaoAndBlobBaseFee(
                header, _blockExecutionContext.Spec, _blockExecutionContext.PrevRandao, UInt256.Zero));
        }

        try
        {
            return transactionProcessor.Trace(transaction, txTracer);
        }
        finally
        {
            header.BaseFeePerGas = baseFee;
            if (unpricedBlobs) transactionProcessor.SetBlockExecutionContext(in _blockExecutionContext);
        }
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _blockExecutionContext = blockExecutionContext;
        transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
    }

    /// <summary>Whether <paramref name="transaction"/> carries blobs with a zero blob fee cap.</summary>
    internal static bool HasUnpricedBlobs(Transaction transaction) =>
        transaction.CarriesBlobs && transaction.MaxFeePerBlobGas is { IsZero: true };
}
