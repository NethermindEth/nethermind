// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Taiko.Precompiles;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class BlockInvalidTxExecutor(
    ITransactionProcessorAdapter txProcessor,
    IWorldState worldState,
    IL1OriginStore l1OriginStore,
    ILogManager logManager)
    : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly ILogger _logger = logManager.GetClassLogger<BlockInvalidTxExecutor>();

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;
    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        => txProcessor.SetBlockExecutionContext(in blockExecutionContext);

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        if (block.Transactions.Length == 0)
        {
            if (block.IsGenesis)
                return [];

            throw new ArgumentException("Block must contain at least the anchor transaction");
        }

        block.Transactions[0].IsAnchorTx = true;

        using ArrayPoolListRef<Transaction> correctTransactions = new(block.Transactions.Length);

        try
        {
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Snapshot snap = worldState.TakeSnapshot();
                Transaction tx = block.Transactions[i];

                if (tx.Type == TxType.Blob)
                {
                    // Skip blob transactions
                    continue;
                }

                // Parse anchor context before Execute so subsequent L1 precompile calls in the
                // block still see the 256-block window even if the anchor tx fails. The parse
                // reads calldata only and does not depend on execution success; the method
                // early-returns for i!=0 or non-AnchorV4 selectors.
                if (i == 0 && !L1PrecompileContextInitializer.TrySetFromAnchorTransaction(i, tx, block.Header.Number, l1OriginStore)
                    && _logger.IsWarn)
                {
                    _logger.Warn($"BlockInvalidTxExecutor: anchor tx context not set at block {block.Header.Number} — subsequent L1 precompile calls will skip range validation");
                }

                using ITxTracer _ = receiptsTracer.StartNewTxTrace(tx);

                try
                {
                    if (!txProcessor.Execute(tx, receiptsTracer))
                    {
                        // if the transaction was invalid, we ignore it and continue
                        worldState.Restore(snap);
                        continue;
                    }
                }
                catch
                {
                    // sometimes invalid transactions can throw exceptions because
                    // they are detected later in the processing pipeline
                    worldState.Restore(snap);
                    continue;
                }

                // only end the trace if the transaction was successful
                // so that we don't increment the receipt index for failed transactions
                receiptsTracer.EndTxTrace();
                TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(i, tx, block.Header, receiptsTracer.LastReceipt));
                correctTransactions.Add(tx);
            }
        }
        finally
        {
            L1PrecompileExecutionContext.Clear();
        }

        block.TrySetTransactions([.. correctTransactions]);
        return [.. receiptsTracer.TxReceipts];
    }
}
