// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    /// <inheritdoc/>
    public void PublishTransactionProcessedEvents() => baseTransactionExecutor.PublishTransactionProcessedEvents();

    /// <inheritdoc/>
    public void ClearTransactionProcessedEvents() => baseTransactionExecutor.ClearTransactionProcessedEvents();

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer,
        CancellationToken token = default)
    {
        if (!simulateState.Validate)
        {
            processingOptions |= ProcessingOptions.ForceProcessing | ProcessingOptions.NoValidation;
        }

        TxReceipt[] result = baseTransactionExecutor.ProcessTransactions(block, processingOptions, receiptsTracer, token);

        // Many gas calculation not done with skip validation, but needed for response
        ulong currentGasUsedTotal = 0;
        foreach (TxReceipt txReceipt in result)
        {
            currentGasUsedTotal += txReceipt.GasUsed;
            txReceipt.GasUsedTotal = currentGasUsedTotal;
        }

        // SimulateTransactionProcessorAdapter change gas limit as block is processed. So need to recalculate.
        block.Header.TxRoot = TxTrie.CalculateRoot(block.Transactions);

        return result;
    }
}
