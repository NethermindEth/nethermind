// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public class BuildUpTransactionProcessorAdapter(ITransactionProcessor transactionProcessor) : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(IWorldState worldState, Transaction transaction, in BlockExecutionContext blkCtx, ITxTracer txTracer) =>
            transactionProcessor.BuildUp(worldState, transaction, in blkCtx, txTracer);
    }
}
