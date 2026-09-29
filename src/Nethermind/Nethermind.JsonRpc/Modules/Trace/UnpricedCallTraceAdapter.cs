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
/// base fee, as eth_call runs it.
/// </summary>
/// <remarks>
/// trace_callMany traces its calls as the transactions of one block, which has a single base fee, so the base fee is
/// cleared per transaction and restored before the next one and before the rest of block processing.
/// Zero-priced is the condition under which the processor skips gas validation and charges no fee.
/// </remarks>
internal sealed class UnpricedCallTraceAdapter(ITransactionProcessor transactionProcessor) : ITransactionProcessorAdapter
{
    private BlockHeader? _header;

    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        if (!transaction.MaxFeePerGas.IsZero || !transaction.MaxPriorityFeePerGas.IsZero) return transactionProcessor.Trace(transaction, txTracer);

        BlockHeader header = _header!;
        UInt256 baseFee = header.BaseFeePerGas;
        header.BaseFeePerGas = UInt256.Zero;
        try
        {
            return transactionProcessor.Trace(transaction, txTracer);
        }
        finally
        {
            header.BaseFeePerGas = baseFee;
        }
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _header = blockExecutionContext.Header;
        transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
    }
}
