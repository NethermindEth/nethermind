// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class TaikoBlockValidationTransactionExecutor(
    ITransactionProcessorAdapter transactionProcessor,
    IWorldState stateProvider)
    : BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider)
{
    public override TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        Transaction[] transactions = block.Transactions;
        if (transactions.Length > 0)
        {
            Transaction first = transactions[0];
            if ((first.SenderAddress?.Equals(TaikoBlockValidator.GoldenTouchAccount) ?? false))
                first.IsAnchorTx = true;
        }

        return base.ProcessTransactions(block, processingOptions, receiptsTracer, token);
    }
}
