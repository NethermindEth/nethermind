// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    public class ChangeableTransactionProcessorAdapter : ITransactionProcessorAdapter
    {
        public ITransactionProcessorAdapter CurrentAdapter { get; set; }
        public ITransactionProcessor TransactionProcessor { get; }

        private ChangeableTransactionProcessorAdapter(ITransactionProcessorAdapter adapter) => CurrentAdapter = adapter;

        public ChangeableTransactionProcessorAdapter(ITransactionProcessor transactionProcessor)
            : this(new ExecuteTransactionProcessorAdapter(transactionProcessor)) => TransactionProcessor = transactionProcessor;

        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer) =>
            CurrentAdapter.Execute(transaction, txTracer);
        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            => CurrentAdapter.SetBlockExecutionContext(in blockExecutionContext);

        /// <summary>
        /// Builds an adapter that runs <paramref name="transactionProcessor"/> in this adapter's current
        /// runtime mode (Execute vs Trace), re-read on every call.
        /// </summary>
        /// <remarks>
        /// Lets the EIP-7928 block-access-list pool's per-worker processors honour the debug tracer's runtime
        /// Execute↔Trace swap: the debug scope registers this as its <see cref="TransactionProcessorAdapterFactory"/>,
        /// so each worker tracks this shared adapter's mode while executing on its own processor.
        /// </remarks>
        public ITransactionProcessorAdapter ForProcessor(ITransactionProcessor transactionProcessor)
            => new PerProcessorAdapter(this, transactionProcessor);

        // GethStyleTracer swaps CurrentAdapter between the Execute and Trace adapters only.
        private bool IsTracing => CurrentAdapter is TraceTransactionProcessorAdapter;

        private sealed class PerProcessorAdapter(ChangeableTransactionProcessorAdapter mode, ITransactionProcessor transactionProcessor)
            : ITransactionProcessorAdapter
        {
            public TransactionResult Execute(Transaction transaction, ITxTracer txTracer) =>
                mode.IsTracing
                    ? transactionProcessor.Trace(transaction, txTracer)
                    : transactionProcessor.Execute(transaction, txTracer);
            public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
                => transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
        }
    }
}
