// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class BlockInvalidTxExecutor(ITransactionProcessorAdapter txProcessor, IWorldState worldState) : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly IWorldState _worldState = worldState;
    private readonly ITransactionProcessorAdapter _txProcessor = txProcessor;

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;

    public TxReceipt[] ProcessTransactions(Block block, in BlockExecutionContext blkCtx, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec, CancellationToken token)
    {
        if (block.Transactions.Length == 0)
        {
            if (block.IsGenesis)
                return [];

            throw new ArgumentException("Block must contain at least the anchor transaction");
        }

        block.Transactions[0].IsAnchorTx = true;

        using ArrayPoolList<Transaction> correctTransactions = new(block.Transactions.Length);

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Snapshot snap = _worldState.TakeSnapshot();
            Transaction tx = block.Transactions[i];

            if (tx.Type == TxType.Blob)
            {
                // Skip blob transactions
                continue;
            }

            using ITxTracer _ = receiptsTracer.StartNewTxTrace(tx);

            try
            {
                if (!_txProcessor.Execute(tx, in blkCtx, receiptsTracer))
                {
                    // if the transaction was invalid, we ignore it and continue
                    _worldState.Restore(snap);
                    continue;
                }
            }
            catch
            {
                // sometimes invalid transactions can throw exceptions because
                // they are detected later in the processing pipeline
                _worldState.Restore(snap);
                continue;
            }
            // only end the trace if the transaction was successful
            // so that we don't increment the receipt index for failed transactions
            receiptsTracer.EndTxTrace();
            TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(i, tx, receiptsTracer.LastReceipt));
            correctTransactions.Add(tx);
        }

        block.TrySetTransactions([.. correctTransactions]);
        return [.. receiptsTracer.TxReceipts];
    }
}
