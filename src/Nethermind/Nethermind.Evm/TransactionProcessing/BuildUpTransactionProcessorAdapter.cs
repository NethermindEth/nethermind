// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public class BuildUpTransactionProcessorAdapter : ITransactionProcessorAdapter
    {
        private readonly ITransactionProcessor _transactionProcessor;

        public BuildUpTransactionProcessorAdapter(ITransactionProcessor transactionProcessor)
        {
            _transactionProcessor = transactionProcessor;
        }

        public void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer) =>
            _transactionProcessor.BuildUp(transaction, block, txTracer);

        public ITransactionProcessorAdapter WithNewStateProvider(IWorldState worldState)
        {
            return new BuildUpTransactionProcessorAdapter(_transactionProcessor.WithNewStateProvider(worldState));
        }
    }
}
