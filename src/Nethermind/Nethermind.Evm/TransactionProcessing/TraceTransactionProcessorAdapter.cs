// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    public class TraceTransactionProcessorAdapter(ITransactionProcessor transactionProcessor)
        : ITransactionProcessor
    {
        public TransactionResult Execute(Transaction transaction, in BlockExecutionContext blkCtx, ITxTracer txTracer) =>
            transactionProcessor.Trace(transaction, in blkCtx, txTracer);

        public TransactionResult CallAndRestore(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) => transactionProcessor.CallAndRestore(transaction, in blCtx, txTracer);

        public TransactionResult BuildUp(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) => transactionProcessor.BuildUp(transaction, in blCtx, txTracer);

        public TransactionResult Trace(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) => transactionProcessor.Trace(transaction, in blCtx, txTracer);

        public TransactionResult Warmup(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) => transactionProcessor.Warmup(transaction, in blCtx, txTracer);
    }
}
