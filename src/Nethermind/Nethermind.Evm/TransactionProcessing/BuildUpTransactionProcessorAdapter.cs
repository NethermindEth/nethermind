// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    public class BuildUpTransactionProcessorAdapter(ITransactionProcessor transactionProcessor) : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, in BlockExecutionContext blkCtx, ITxTracer txTracer,
            Dictionary<Address, AccountOverride>? stateOverride = null) =>
            transactionProcessor.BuildUp(transaction, in blkCtx, txTracer, stateOverride);
    }
}
