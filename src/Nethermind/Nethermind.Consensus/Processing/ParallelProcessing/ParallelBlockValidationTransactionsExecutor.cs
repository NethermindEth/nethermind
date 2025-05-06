// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class ParallelBlockValidationTransactionsExecutor() : IBlockProcessor.IBlockTransactionsExecutor
{
    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec, CancellationToken token = default)
    {
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction tx = block.Transactions[i];
            TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(i, tx, new TxReceipt()));
        }

        return [];
    }

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;
}
