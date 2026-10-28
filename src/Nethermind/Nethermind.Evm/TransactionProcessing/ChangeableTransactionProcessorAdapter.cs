// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    public class ChangeableTransactionProcessorAdapter(ITransactionProcessor transactionProcessor) : ITransactionProcessorAdapter
    {
        /// <summary>The current runtime mode: given a processor, builds the adapter that runs it. Swapped by the debug tracer between Execute and Trace.</summary>
        public TransactionProcessorAdapterFactory CurrentAdapterFactory { get; set; } = static processor => new ExecuteTransactionProcessorAdapter(processor);
        public ITransactionProcessor TransactionProcessor { get; } = transactionProcessor;

        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer) =>
            CurrentAdapterFactory(TransactionProcessor).Execute(transaction, txTracer);
        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            => TransactionProcessor.SetBlockExecutionContext(in blockExecutionContext);

        /// <summary>
        /// Builds an adapter that runs <paramref name="processor"/> in this adapter's current runtime mode,
        /// re-read on every call.
        /// </summary>
        /// <remarks>
        /// Lets the EIP-7928 block-access-list pool's per-worker processors honour the debug tracer's runtime
        /// Execute↔Trace swap: the debug scope registers this as its <see cref="TransactionProcessorAdapterFactory"/>,
        /// so each worker applies this shared adapter's current mode to its own processor.
        /// </remarks>
        public ITransactionProcessorAdapter ForProcessor(ITransactionProcessor processor)
            => new PerProcessorAdapter(this, processor);

        private sealed class PerProcessorAdapter(ChangeableTransactionProcessorAdapter mode, ITransactionProcessor processor)
            : ITransactionProcessorAdapter
        {
            public TransactionResult Execute(Transaction transaction, ITxTracer txTracer) =>
                mode.CurrentAdapterFactory(processor).Execute(transaction, txTracer);
            public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
                => processor.SetBlockExecutionContext(in blockExecutionContext);
        }
    }
}
