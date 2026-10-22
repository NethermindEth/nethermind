// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    public class BuildUpTransactionProcessorAdapter(ITransactionProcessor transactionProcessor) : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer) =>
            transactionProcessor.BuildUp(transaction, txTracer);
        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            => transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
    }
}
