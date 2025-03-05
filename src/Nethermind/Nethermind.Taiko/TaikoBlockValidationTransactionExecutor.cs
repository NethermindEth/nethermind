// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Taiko;

public class TaikoBlockValidationTransactionExecutor(
    ITransactionProcessor transactionProcessor,
    IWorldState stateProvider)
    : BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider)
{
    protected override void ProcessTransaction(in BlockExecutionContext blkCtx, Transaction currentTx, int i, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
    {
        if (i == 0)
        {
            currentTx.IsAnchorTx = true;
        }
        base.ProcessTransaction(in blkCtx, currentTx, i, receiptsTracer, processingOptions);
    }
}
