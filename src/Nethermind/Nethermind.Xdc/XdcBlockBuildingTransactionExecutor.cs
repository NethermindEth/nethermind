// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.TxPool.Comparison;
using System.Collections.Generic;
using System.Threading;
using Nethermind.TxPool;
using static Nethermind.Consensus.Processing.BlockProcessor;

namespace Nethermind.Xdc;

internal class XdcBlockBuildingTransactionExecutor(
        ITransactionProcessorAdapter transactionProcessor,
        IWorldState stateProvider,
        IBlockProductionTransactionPicker txPicker,
        ISpecProvider specProvider,
        ILogManager logManager) : BlockProcessor.BlockProductionTransactionsExecutor(transactionProcessor, stateProvider, txPicker, logManager)
{
    public override TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        // We start with high number as don't want to resize too much
        const int defaultTxCount = 512;

        var spec = specProvider.GetXdcSpec(block.Header as XdcBlockHeader);

        BlockToProduce? blockToProduce = block as BlockToProduce;

        // Don't use blockToProduce.Transactions.Count() as that would fully enumerate which is expensive
        int txCount = blockToProduce is not null ? defaultTxCount : block.Transactions.Length;
        IEnumerable<Transaction> transactions = blockToProduce?.Transactions ?? block.Transactions;

        ArrayPoolListRef<Transaction> includedTx = new(txCount);

        HashSet<Transaction> consideredTx = new(ByHashTxComparer.Instance);
        int i = 0;

        HashSet<Transaction> delayedTx = new(ByHashTxComparer.Instance);
        try
        {

            foreach (Transaction currentTx in transactions)
            {
                // Check if we have gone over time or the payload has been requested
                if (token.IsCancellationRequested) break;

                if (!currentTx.IsSpecialTransaction(spec))
                {
                    delayedTx.Add(currentTx);
                    continue;
                }

                if (!ProcessSingleTransaction(block, processingOptions, receiptsTracer, blockToProduce, ref includedTx, consideredTx, i, currentTx))
                {
                    break;
                }
            }


            foreach (Transaction currentTx in delayedTx)
            {
                // Check if we have gone over time or the payload has been requested
                if (token.IsCancellationRequested) break;

                if(!ProcessSingleTransaction(block, processingOptions, receiptsTracer, blockToProduce, ref includedTx, consideredTx, i, currentTx))
                {
                    break;
                }
            }

            block.Header.TxRoot = TxTrie.CalculateRoot(includedTx.AsSpan());
            if (blockToProduce is not null)
            {
                blockToProduce.Transactions = includedTx.ToArray();
            }
            return receiptsTracer.TxReceipts.ToArray();
        } finally
        {
            includedTx.Dispose();
        }
    }

    private bool ProcessSingleTransaction(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, BlockToProduce blockToProduce, ref ArrayPoolListRef<Transaction> includedTx, HashSet<Transaction> consideredTx, int i, Transaction currentTx)
    {
        TxAction action = ProcessTransaction(block, currentTx, i++, receiptsTracer, processingOptions, consideredTx);
        if (action == TxAction.Stop) return false;

        consideredTx.Add(currentTx);
        if (action == TxAction.Add)
        {
            includedTx.Add(currentTx);
            if (blockToProduce is not null)
            {
                blockToProduce.TxByteLength += currentTx.GetLength(false);
            }
        }

        return true;
    }
}
