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
    // Relax EIP-3607 via skipSenderCodeCheck (set on the context so it reaches both the main tx processor and
    // the EIP-7928 BAL processors, which share it) and apply the simulate blobBaseFee override. Forward the
    // incoming PrevRandao and BlobBaseFee verbatim rather than re-deriving them (a BlockProcessor subclass
    // may have set non-default values).
    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
        baseTransactionExecutor.SetBlockExecutionContext(BlockExecutionContext.WithPrevRandaoAndBlobBaseFee(
            blockExecutionContext.Header,
            blockExecutionContext.Spec,
            blockExecutionContext.PrevRandao,
            simulateState.BlobBaseFeeOverride ?? blockExecutionContext.BlobBaseFee.ToUInt256(),
            skipSenderCodeCheck: true));

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
