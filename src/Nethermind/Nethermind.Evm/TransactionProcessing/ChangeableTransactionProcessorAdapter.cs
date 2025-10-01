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

        private ChangeableTransactionProcessorAdapter(ITransactionProcessorAdapter adapter)
        {
            CurrentAdapter = adapter;
        }

        public ChangeableTransactionProcessorAdapter(ITransactionProcessor transactionProcessor)
            : this(new ExecuteTransactionProcessorAdapter(transactionProcessor))
        {
            TransactionProcessor = transactionProcessor;
        }

        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer, bool? isFromTraceEndpoint = null) =>
            CurrentAdapter.Execute(transaction, txTracer);
        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            => CurrentAdapter.SetBlockExecutionContext(in blockExecutionContext);
    }
}
