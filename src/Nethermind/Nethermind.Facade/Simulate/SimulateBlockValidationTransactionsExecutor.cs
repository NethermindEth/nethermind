// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
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
        // Relax EIP-3607 on the block execution context so a state-overridden contract can be the tx
        // sender. Done here (not at the spec-provider level) so it reaches both the main tx processor and
        // the EIP-7928 BAL manager — ParallelBlockValidationTransactionsExecutor sets this context on both —
        // while BlockProcessor still receives the unwrapped spec, preserving chain-specific spec interfaces.
        IReleaseSpec spec = blockExecutionContext.Spec.WithoutEip3607();

        // This is now the single funnel for every processor in the simulate scope, so preserve the incoming
        // PrevRandao rather than re-deriving it — a BlockProcessor subclass (e.g. XdcBlockProcessor) may have
        // supplied a non-default value that the default derivation would otherwise discard.
        baseTransactionExecutor.SetBlockExecutionContext(simulateState.BlobBaseFeeOverride is null
            ? BlockExecutionContext.WithPrevRandao(blockExecutionContext.Header, spec, blockExecutionContext.PrevRandao)
            : BlockExecutionContext.WithPrevRandaoAndBlobBaseFee(blockExecutionContext.Header, spec, blockExecutionContext.PrevRandao, simulateState.BlobBaseFeeOverride.Value));
    }

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer,
        CancellationToken token = default)
    {
        ulong startingGasLeft = simulateState.TotalGasLeft;
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

        block.Header.GasUsed = startingGasLeft - simulateState.TotalGasLeft;

        // SimulateTransactionProcessorAdapter change gas limit as block is processed. So need to recalculate.
        block.Header.TxRoot = TxTrie.CalculateRoot(block.Transactions);

        return result;
    }
}
