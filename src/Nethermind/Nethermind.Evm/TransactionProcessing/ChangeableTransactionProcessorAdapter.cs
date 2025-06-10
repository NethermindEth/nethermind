// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    public class ChangeableTransactionProcessorAdapter : ITransactionProcessor
    {
        public ITransactionProcessor CurrentAdapter { get; set; }
        public ITransactionProcessor TransactionProcessor { get; }

        public ChangeableTransactionProcessorAdapter(ITransactionProcessor adapter)
        {
            CurrentAdapter = adapter;
            TransactionProcessor = adapter;
        }

        public TransactionResult Execute(Transaction transaction, in BlockExecutionContext blkCtx, ITxTracer txTracer) => CurrentAdapter.Execute(transaction, in blkCtx, txTracer);

        public TransactionResult CallAndRestore(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) => CurrentAdapter.CallAndRestore(transaction, in blCtx, txTracer);

        public TransactionResult BuildUp(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) => CurrentAdapter.BuildUp(transaction, in blCtx, txTracer);

        public TransactionResult Trace(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) => CurrentAdapter.Trace(transaction, in blCtx, txTracer);

        public TransactionResult Warmup(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) => CurrentAdapter.Warmup(transaction, in blCtx, txTracer);
    }
}
