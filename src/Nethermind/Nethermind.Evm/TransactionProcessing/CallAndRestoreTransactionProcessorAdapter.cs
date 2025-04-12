// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
//
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    public class CallAndRestoreTransactionProcessorAdapter(ITransactionProcessor transactionProcessor)
        : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, in BlockExecutionContext blkCtx, ITxTracer txTracer) =>
            transactionProcessor.CallAndRestore(transaction, in blkCtx, txTracer);
    }
}
