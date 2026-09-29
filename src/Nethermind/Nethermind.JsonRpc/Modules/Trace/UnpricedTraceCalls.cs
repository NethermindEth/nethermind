// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Trace;

/// <summary>
/// The calls of a trace_callMany request that are priced at zero. Each runs with a zero base fee, as eth_call runs it,
/// while the other calls of the request see the block's base fee.
/// </summary>
/// <remarks>
/// trace_callMany traces its calls as the transactions of one block, which has a single base fee, so the adapters
/// created here clear it for each marked call and restore it before the next. A module instance is rented for a
/// whole request, a streamed result included, so its marks are never shared between requests.
/// </remarks>
public sealed class UnpricedTraceCalls
{
    private IReadOnlySet<Transaction>? _calls;

    /// <summary>Marks <paramref name="calls"/> as unpriced until the returned scope is disposed.</summary>
    public Marks Mark(IReadOnlySet<Transaction> calls)
    {
        _calls = calls;
        return new Marks(this);
    }

    /// <summary>Traces as <see cref="TraceTransactionProcessorAdapter"/> does, with a zero base fee for each marked call.</summary>
    public ITransactionProcessorAdapter CreateAdapter(ITransactionProcessor transactionProcessor) => new Adapter(transactionProcessor, this);

    public readonly struct Marks(UnpricedTraceCalls owner) : IDisposable
    {
        public void Dispose() => owner._calls = null;
    }

    private sealed class Adapter(ITransactionProcessor transactionProcessor, UnpricedTraceCalls unpricedCalls) : ITransactionProcessorAdapter
    {
        private BlockHeader? _header;

        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            if (unpricedCalls._calls?.Contains(transaction) != true) return transactionProcessor.Trace(transaction, txTracer);

            // The header is the block's processing copy: its gas used keeps accumulating, and its base fee is back for
            // the next call and for what the block processes after its transactions.
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
}
