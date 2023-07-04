// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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

        public void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            CurrentAdapter.Execute(transaction, block, txTracer);
        }
    }
}
