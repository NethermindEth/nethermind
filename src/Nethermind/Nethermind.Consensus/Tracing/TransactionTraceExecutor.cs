// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Metrics = Nethermind.Evm.Metrics;

namespace Nethermind.Consensus.Tracing;

/// <summary>Executes read-only transaction prefixes; delegates all other processing unchanged.</summary>
public sealed class TransactionTraceExecutor(
    IBlockProcessor.IBlockTransactionsExecutor inner,
    ITransactionProcessorAdapter transactionProcessor,
    IWorldState state,
    IBlockAccessListManager balManager,
    BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessed = null)
    : IBlockProcessor.IBlockTransactionsExecutor
{
    public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(in context);

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions options, BlockReceiptsTracer tracer, CancellationToken token)
    {
        TransactionTraceBoundary? boundary = TransactionTraceBoundary.Get(tracer.OtherTracer, options);
        if (boundary is null || balManager.ForceConstructGeneratedBlockAccessList || boundary.IsTracingRewards)
            return inner.ProcessTransactions(block, options, tracer, token);

        Metrics.ResetBlockStats();
        inner.SetupTxTimingMetrics(block);
        if (balManager.Enabled) balManager.NextTransaction();

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            Transaction tx = block.Transactions[i];
            ITransactionProcessorAdapter processor = balManager.Enabled ? balManager.GetTxProcessor((uint)i + 1) : transactionProcessor;
            long start = inner.StartTxTimer();
            TransactionResult result;
            try
            {
                result = processor.ProcessTransaction(tx, tracer, options, state);
            }
            finally
            {
                inner.StopTxTimer(i, start);
            }

            if (!result) BlockProcessor.BlockValidationTransactionsExecutor.ThrowInvalidTransactionException(result, block.Header, tx, i);
            transactionProcessed?.OnTransactionProcessed(new TxProcessedEventArgs(i, tx, block.Header, tracer.TxReceipts[i]));
            if (balManager.Enabled)
            {
                balManager.NextTransaction();
                balManager.SpendGas(tx.BlockGasUsed);
            }
            if (boundary.IsComplete) break;
        }

        Metrics.SeedBlockGasPriceIfEmpty(block.Header.BaseFeePerGas);
        Metrics.PublishBlockGasPriceGauges();
        return [.. tracer.TxReceipts];
    }
}
