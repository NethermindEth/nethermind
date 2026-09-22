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

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer,
        CancellationToken token = default)
    {
        if (!simulateState.Validate)
        {
            processingOptions |= ProcessingOptions.ForceProcessing | ProcessingOptions.NoValidation;
        }

        TxReceipt[] result = baseTransactionExecutor.ProcessTransactions(block, processingOptions, receiptsTracer, token);

        // Header.GasUsed and the receipts' GasUsedTotal are left as BlockReceiptsTracer wrote them -
        // the EIP-7778/EIP-8037 two-dimensional max(sum execution, sum state) and the post-refund
        // cumulative - not the gas-limit budget this executor's adapter tracks.

        // SimulateTransactionProcessorAdapter change gas limit as block is processed. So need to recalculate.
        block.Header.TxRoot = TxTrie.CalculateRoot(block.Transactions);

        return result;
    }
}
