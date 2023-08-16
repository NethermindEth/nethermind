// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.State;

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

        public ITransactionProcessorAdapter WithNewStateProvider(IWorldState worldState)
        {
            return new BuildUpTransactionProcessorAdapter(TransactionProcessor.WithNewStateProvider(worldState));
        }
    }
}
