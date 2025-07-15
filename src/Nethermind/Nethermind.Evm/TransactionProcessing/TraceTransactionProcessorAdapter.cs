// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    public class TraceTransactionProcessorAdapter(ITransactionProcessor transactionProcessor)
        : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer) =>
            transactionProcessor.Trace(transaction, txTracer);
        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            => transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
    }
}
