// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Taiko.Precompiles;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class TaikoBlockValidationTransactionExecutor(
    ITransactionProcessorAdapter transactionProcessor,
    IWorldState stateProvider,
    IL1OriginStore l1OriginStore)
    : BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider)
{
    public override TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        try
        {
            return base.ProcessTransactions(block, processingOptions, receiptsTracer, token);
        }
        finally
        {
            L1PrecompileExecutionContext.Clear();
        }
    }

    protected override void ProcessTransaction(Block block, Transaction currentTx, int i, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
    {
        if ((currentTx.SenderAddress?.Equals(TaikoBlockValidator.GoldenTouchAccount) ?? false) && i == 0)
            currentTx.IsAnchorTx = true;
        base.ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
        L1PrecompileContextInitializer.TrySetFromAnchorTransaction(i, currentTx, block.Header.Number, l1OriginStore);
    }
}
