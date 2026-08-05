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
    // Apply only the simulate blobBaseFee override here; EIP-3607 relaxation for state-overridden
    // contract senders is handled by the SkipSenderCodeCheck execution-policy flag on the tx processors
    // (both the main adapter and the EIP-7928 BAL path), so the spec keeps its concrete runtime type and
    // chain-specific interfaces (ITaikoReleaseSpec/IXdcReleaseSpec) survive. Forward the incoming
    // PrevRandao and BlobBaseFee verbatim rather than re-deriving them from the header — a BlockProcessor
    // subclass (e.g. XdcBlockProcessor) may have supplied non-default values. The override still wins.
    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
        baseTransactionExecutor.SetBlockExecutionContext(BlockExecutionContext.WithPrevRandaoAndBlobBaseFee(
            blockExecutionContext.Header,
            blockExecutionContext.Spec,
            blockExecutionContext.PrevRandao,
            simulateState.BlobBaseFeeOverride ?? blockExecutionContext.BlobBaseFee.ToUInt256()));

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
