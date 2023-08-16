// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public class TraceTransactionProcessorAdapter : ITransactionProcessorAdapter
    {
        private readonly ITransactionProcessor _transactionProcessor;

        public TraceTransactionProcessorAdapter(ITransactionProcessor transactionProcessor)
        {
            _transactionProcessor = transactionProcessor;
        }

        public void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer) =>
            _transactionProcessor.Trace(transaction, block, txTracer);

        public ITransactionProcessorAdapter WithNewStateProvider(IWorldState worldState) =>
            new TraceTransactionProcessorAdapter(_transactionProcessor.WithNewStateProvider(worldState));
    }
}
