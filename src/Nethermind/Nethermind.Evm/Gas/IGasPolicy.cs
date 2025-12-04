// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Gas;

/// <summary>
/// Defines a gas policy for EVM execution.
/// </summary>
/// <typeparam name="TSelf">The implementing type</typeparam>
public interface IGasPolicy<TSelf> where TSelf : struct, IGasPolicy<TSelf>
{
    /// <summary>
    /// Initialize gas state for a new transaction with intrinsic gas already deducted.
    /// Called by TransactionProcessor before ExecuteTransaction.
    /// </summary>
    /// <param name="gasLimit">The gas limit provided for the transaction</param>
    /// <param name="intrinsicGas">Intrinsic gas already calculated</param>
    /// <returns>Initialized gas state with remaining gas</returns>
    static abstract GasState<TSelf> InitializeForTransaction(long gasLimit, long intrinsicGas);

    /// <summary>
    /// Get the remaining single-dimensional gas available for execution.
    /// This is what's checked against zero to detect out-of-gas conditions.
    /// </summary>
    /// <param name="gasState">The current gas state</param>
    /// <returns>Remaining gas (negative values indicate out-of-gas)</returns>
    static abstract long GetRemainingGas(in GasState<TSelf> gasState);

    /// <summary>
    /// Consume gas for an EVM operation with tracking.
    /// </summary>
    /// <param name="gasState">The gas state to update</param>
    /// <param name="gasCost">The gas cost to charge for this operation</param>
    /// <param name="instruction">The instruction being executed</param>
    static abstract void ConsumeGas(ref GasState<TSelf> gasState, long gasCost, Instruction instruction);

    /// <summary>
    /// Refund unused gas (e.g., from failed CALL/CREATE).
    /// </summary>
    /// <param name="gasState">The gas state to update</param>
    /// <param name="gasAmount">The amount of gas to return</param>
    static abstract void RefundGas(ref GasState<TSelf> gasState, long gasAmount);

    /// <summary>
    /// Mark the gas state as out of gas.
    /// Called when execution exhausts all gas.
    /// </summary>
    /// <param name="gasState">The gas state to update</param>
    static abstract void SetOutOfGas(ref GasState<TSelf> gasState);
}
