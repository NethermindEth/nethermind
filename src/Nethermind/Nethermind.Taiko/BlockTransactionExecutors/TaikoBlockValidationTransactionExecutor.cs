// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class TaikoBlockValidationTransactionExecutor(
    ITransactionProcessorAdapter transactionProcessor,
    IWorldState stateProvider,
    ISpecProvider specProvider,
    IBlockAccessListManager balManager)
    : BlockProcessor.ParallelBlockValidationTransactionsExecutor(transactionProcessor, stateProvider, specProvider, balManager)
{
    protected override void ProcessTransaction(
        ITransactionProcessorAdapter transactionProcessor,
        IWorldState stateProvider,
        ITransactionProcessedEventHandler? transactionProcessedEventHandler,
        Block block,
        Transaction currentTx,
        int index,
        BlockReceiptsTracer receiptsTracer,
        ProcessingOptions processingOptions)
    {
        if ((currentTx.SenderAddress?.Equals(TaikoBlockValidator.GoldenTouchAccount) ?? false) && index == 0)
            currentTx.IsAnchorTx = true;
        base.ProcessTransaction(transactionProcessor, stateProvider, transactionProcessedEventHandler, block, currentTx, index, receiptsTracer, processingOptions);
    }
}
