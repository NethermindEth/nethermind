// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using System.Threading;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class TaikoBlockValidationTransactionExecutor(
    ITransactionProcessorAdapter transactionProcessor,
    IWorldState stateProvider)
    : BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider)
{
    public override TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        if (block.Transactions.Length > 0 &&
            (block.Transactions[0].SenderAddress?.Equals(TaikoBlockValidator.GoldenTouchAccount) ?? false))
        {
            block.Transactions[0].IsAnchorTx = true;

            // If L1SLOAD precompile is enabled, set the anchor block ID.
            if (L1SloadPrecompile.L1StorageProvider is not null)
                JsonRpcL1StorageProvider.SetAnchorBlockId(block.Transactions[0]);
        }

        return base.ProcessTransactions(block, processingOptions, receiptsTracer, token);
    }
}
