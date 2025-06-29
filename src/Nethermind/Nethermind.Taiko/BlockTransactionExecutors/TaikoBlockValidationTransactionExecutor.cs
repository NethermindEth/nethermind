// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class TaikoBlockValidationTransactionExecutor(
    ITransactionProcessorAdapter transactionProcessor,
    IWorldState stateProvider)
    : BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider)
{
    protected override void ProcessTransaction(Block block, Transaction currentTx, int i, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
    {
        if ((currentTx.SenderAddress?.Equals(TaikoBlockValidator.GoldenTouchAccount) ?? false) && i == 0)
            currentTx.IsAnchorTx = true;
        base.ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
    }
}
