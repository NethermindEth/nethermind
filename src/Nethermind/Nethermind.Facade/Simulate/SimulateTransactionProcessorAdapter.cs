// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Facade.Simulate;

/// <remarks>
/// Stateful and single-threaded: it advances a per-block <c>_currentTxIndex</c> and mutates the shared
/// <see cref="SimulateRequestState"/> gas counters without synchronization. It must therefore only ever
/// drive one transaction stream at a time, in order — i.e. the sequential block-access-list path. Simulate
/// guarantees this by never attaching a <c>BlockAccessList</c> to its synthesised blocks, which keeps
/// <c>BlockAccessListManager.ParallelExecutionEnabled</c> false. Do not register it on a parallel pool.
/// </remarks>
public class SimulateTransactionProcessorAdapter(ITransactionProcessor transactionProcessor, SimulateRequestState simulateRequestState) : ITransactionProcessorAdapter
{
    private int _currentTxIndex = 0;
    private ulong _maxTotalGasLimit = ulong.MaxValue;
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        // The non-BAL / validation:false paths never run the block executor's pre-check, so resolve the gas
        // here; on the BAL path this repeats it with the same budget, leaving the accepted limit unchanged.
        PrepareForInclusionCheck(transaction, simulateRequestState.BlockStateGasLeft);
        transaction.Hash = transaction.CalculateHash();

        // The state budget only depletes while a BlockReceiptsTracer publishes the cumulative state gas;
        // without one the fix for #12692 silently reverts, so pin the premise rather than only tolerate it.
        BlockReceiptsTracer? receiptsTracer = txTracer.GetTracer<BlockReceiptsTracer>();
        Debug.Assert(receiptsTracer is not null, $"Simulate must be traced through a {nameof(BlockReceiptsTracer)}.");
        ulong cumulativeStateGasBefore = receiptsTracer?.BlockStateGasUsed ?? 0;
        TransactionResult result = simulateRequestState.Validate ? transactionProcessor.Execute(transaction, txTracer) : transactionProcessor.Trace(transaction, txTracer);

        ulong blockGasUsed = transaction.BlockGasUsed;
        ulong blockStateGasUsed = receiptsTracer?.BlockStateGasUsed.SaturatingSub(cumulativeStateGasBefore) ?? 0;
        // A transaction's gas limit funds both EIP-8037 dimensions, so the request-wide cap — which is what
        // clamps that limit — depletes by their sum, while the block budgets below track one dimension each.
        simulateRequestState.TotalGasLeft = simulateRequestState.TotalGasLeft.SaturatingSub(blockGasUsed.SaturatingAdd(blockStateGasUsed));
        simulateRequestState.BlockGasLeft = simulateRequestState.BlockGasLeft.SaturatingSub(blockGasUsed);
        // The BAL inclusion check refreshes this from receipt totals when it runs; otherwise — non-BAL specs,
        // ForceSequentialBlockAccessList, block production, Validation:false — this running value is authoritative.
        simulateRequestState.BlockStateGasLeft = simulateRequestState.BlockStateGasLeft.SaturatingSub(blockStateGasUsed);

        _currentTxIndex++;
        return result;
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _currentTxIndex = 0;
        _maxTotalGasLimit = blockExecutionContext.Spec.GetProcessorEnforcedTxGasLimitCap();
        transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
    }

    /// <inheritdoc/>
    public void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable)
    {
        simulateRequestState.BlockStateGasLeft = stateGasAvailable;

        // The per-dimension budgets shrink as the block is processed.
        if (!simulateRequestState.TxsWithExplicitGas[_currentTxIndex])
        {
            // State gas is known only after execution, so this conservative cap can reject a state-free
            // transaction when the remaining state budget is below its intrinsic execution gas.
            transaction.GasLimit = Math.Min(
                Math.Min(simulateRequestState.BlockGasLeft, stateGasAvailable),
                Math.Min(simulateRequestState.TotalGasLeft, _maxTotalGasLimit));
        }

        if (simulateRequestState.TotalGasLeft < transaction.GasLimit)
        {
            transaction.GasLimit = simulateRequestState.TotalGasLeft;
        }
    }
}
