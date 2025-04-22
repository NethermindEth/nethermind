// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class TaikoBlockValidationTransactionExecutor(
    ITransactionProcessorAdapter transactionProcessor,
    IWorldState stateProvider)
    : BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider)
{

    public TaikoBlockValidationTransactionExecutor(
        ITransactionProcessor transactionProcessor,
        IWorldState stateProvider) : this(new ExecuteTransactionProcessorAdapter(transactionProcessor), stateProvider)
    {
    }

    protected override void ProcessTransaction(in BlockExecutionContext blkCtx, Transaction currentTx, int i, BlockExecutionTracer executionTracer, ProcessingOptions processingOptions)
    {
        if ((currentTx.SenderAddress?.Equals(TaikoBlockValidator.GoldenTouchAccount) ?? false) && i == 0)
            currentTx.IsAnchorTx = true;
        base.ProcessTransaction(in blkCtx, currentTx, i, executionTracer, processingOptions);
    }
}
