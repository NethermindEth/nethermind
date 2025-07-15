// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.State.Proofs;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockValidationTransactionsExecutor(
    IBlockProcessor.IBlockTransactionsExecutor baseTransactionExecutor,
    SimulateRequestState simulateState)
    : IBlockProcessor.IBlockTransactionsExecutor
{
    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        if (simulateState.BlobBaseFeeOverride is null)
        {
            baseTransactionExecutor.SetBlockExecutionContext(in blockExecutionContext);
            return;
        }

        baseTransactionExecutor.SetBlockExecutionContext(
            new BlockExecutionContext(blockExecutionContext.Header,
                blockExecutionContext.Spec,
                simulateState.BlobBaseFeeOverride.Value)
        );
    }

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer,
        CancellationToken token = default)
    {
        long startingGasLeft = simulateState.TotalGasLeft;
        if (!simulateState.Validate)
        {
            processingOptions |= ProcessingOptions.ForceProcessing | ProcessingOptions.DoNotVerifyNonce | ProcessingOptions.NoValidation;
        }

        var result = baseTransactionExecutor.ProcessTransactions(block, processingOptions, receiptsTracer, token);

        // Many gas calculation not done with skip validation, but needed for response
        long currentGasUsedTotal = 0;
        foreach (TxReceipt txReceipt in result)
        {
            currentGasUsedTotal += txReceipt.GasUsed;
            txReceipt.GasUsedTotal = currentGasUsedTotal;

            // For some reason, the logs from geth when processing the block is missing but not in the output from tracer.
            // this cause the receipt root to be different than us. So we simulate it here.
            txReceipt.Logs = [];
        }

        block.Header.GasUsed = startingGasLeft - simulateState.TotalGasLeft;

        // SimulateTransactionProcessorAdapter change gas limit as block is processed. So need to recalculate.
        block.Header.TxRoot = TxTrie.CalculateRoot(block.Transactions);

        return result;
    }

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed
    {
        add => baseTransactionExecutor.TransactionProcessed += value;
        remove => baseTransactionExecutor.TransactionProcessed -= value;
    }
}
