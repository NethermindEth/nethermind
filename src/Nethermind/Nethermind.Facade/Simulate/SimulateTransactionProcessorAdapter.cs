// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Facade.Simulate;

/// <remarks>
/// Stateful and single-threaded: it advances a per-block <c>_currentTxIndex</c> and mutates the shared
/// <see cref="SimulateRequestState"/> gas counters with unsynchronised <c>-=</c>. It must therefore only ever
/// drive one transaction stream at a time, in order — i.e. the sequential block-access-list path. Simulate
/// guarantees this by never attaching a <c>BlockAccessList</c> to its synthesised blocks and by setting
/// <see cref="ProcessingOptions.ForceSequentialBlockAccessList"/>, both of which keep
/// <c>BlockAccessListManager.ParallelExecutionEnabled</c> false. Do not register it on a parallel pool.
/// </remarks>
public class SimulateTransactionProcessorAdapter(ITransactionProcessor transactionProcessor, SimulateRequestState simulateRequestState) : ITransactionProcessorAdapter
{
    private int _currentTxIndex = 0;
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        // Re-resolve against the state budget the executor already stored (a no-op re-store). On the
        // non-BAL / validation:false paths the executor never calls the hook, so this seeds the gas here;
        // on the BAL validation path it repeats the executor's call with the same budget, so it can't raise
        // the limit the inclusion check already accepted.
        PrepareForInclusionCheck(transaction, simulateRequestState.BlockStateGasLeft);
        transaction.Hash = transaction.CalculateHash();

        TransactionResult result = simulateRequestState.Validate ? transactionProcessor.Execute(transaction, txTracer) : transactionProcessor.Trace(transaction, txTracer);

        // Keep track of gas left
        ulong blockGasUsed = transaction.BlockGasUsed;
        simulateRequestState.TotalGasLeft -= blockGasUsed;
        simulateRequestState.BlockGasLeft -= blockGasUsed;

        _currentTxIndex++;
        return result;
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _currentTxIndex = 0;
        transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Resolves a gas-less call to the running block budget so it reserves only what is left, not the
    /// parse-time GasCap default the EIP-8037 inclusion check would reject. Clamps to both the execution
    /// (<see cref="SimulateRequestState.BlockGasLeft"/>) and state (<paramref name="stateGasAvailable"/>)
    /// dimensions so it fits both sides of the check. <see cref="Execute"/> calls this too, so the value the
    /// check sees is exactly the value execution uses.
    /// </remarks>
    public void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable)
    {
        simulateRequestState.BlockStateGasLeft = stateGasAvailable;

        // The per-dimension budgets shrink as the block is processed.
        if (!simulateRequestState.TxsWithExplicitGas[_currentTxIndex])
        {
            transaction.GasLimit = Math.Min(
                Math.Min(simulateRequestState.BlockGasLeft, stateGasAvailable),
                simulateRequestState.TotalGasLeft);
        }

        if (simulateRequestState.TotalGasLeft < transaction.GasLimit)
        {
            transaction.GasLimit = simulateRequestState.TotalGasLeft;
        }
    }
}
