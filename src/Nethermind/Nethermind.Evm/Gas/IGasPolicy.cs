// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Gas;

/// <summary>
/// Defines a gas policy for EVM execution.
/// Implementations can use single-dimensional or multidimensional gas accounting.
/// </summary>
/// <typeparam name="TSelf">The implementing type (enables static dispatch without virtual calls)</typeparam>
public interface IGasPolicy<TSelf> where TSelf : struct, IGasPolicy<TSelf>
{
    /// <summary>
    /// Initialize gas state for a new transaction with intrinsic gas already deducted.
    /// Called by TransactionProcessor before ExecuteTransaction.
    /// </summary>
    /// <param name="gasLimit">The gas limit provided for the transaction</param>
    /// <param name="intrinsicGas">Intrinsic gas already calculated (not deducted yet from gasLimit)</param>
    /// <returns>Initialized gas state with remaining gas and policy-specific data</returns>
    static abstract GasState InitializeForTransaction(long gasLimit, long intrinsicGas);

    /// <summary>
    /// Get the remaining single-dimensional gas available for execution.
    /// This is what's checked against zero to detect out-of-gas conditions.
    /// </summary>
    /// <param name="gasState">The current gas state</param>
    /// <returns>Remaining gas (negative values indicate out-of-gas)</returns>
    static abstract long GetRemainingGas(in GasState gasState);

    /// <summary>
    /// Consume gas for an EVM operation.
    /// Implementations can categorize gas by instruction type for multidimensional accounting.
    /// </summary>
    /// <param name="gasState">The gas state to update (passed by reference for mutation)</param>
    /// <param name="gasCost">The gas cost to charge for this operation</param>
    /// <param name="instruction">The instruction being executed (for categorization)</param>
    static abstract void ConsumeGas(ref GasState gasState, long gasCost, Instruction instruction);

    /// <summary>
    /// Refund unused gas (e.g., from failed CALL/CREATE).
    /// This is different from ApplyRefund, which applies accumulated refunds at the transaction end.
    /// </summary>
    /// <param name="gasState">The gas state to update</param>
    /// <param name="gasAmount">The amount of gas to return (positive value)</param>
    static abstract void RefundGas(ref GasState gasState, long gasAmount);

    /// <summary>
    /// Mark the gas state as out of gas (sets remaining to zero).
    /// Called when execution exhausts all gas.
    /// </summary>
    /// <param name="gasState">The gas state to update</param>
    static abstract void SetOutOfGas(ref GasState gasState);

    /// <summary>
    /// Initialize the gas state for a child call frame (CALL, DELEGATECALL, STATICCALL, CREATE, CREATE2).
    /// </summary>
    /// <param name="gasProvided">The amount of gas provided to the child frame</param>
    /// <returns>New gas state for the child frame</returns>
    static abstract GasState InitializeChildFrame(long gasProvided);

    /// <summary>
    /// Merge child frame gas state back into parent after call completion.
    /// Handles gas returns, refunds, and policy-specific aggregation (e.g., multigas accumulation).
    /// </summary>
    /// <param name="parentState">The parent gas state to update</param>
    /// <param name="childState">The completed child gas state</param>
    /// <param name="gasProvided">The amount of gas originally provided to the child</param>
    static abstract void MergeChildFrame(
        ref GasState parentState,
        in GasState childState,
        long gasProvided);

    /// <summary>
    /// Finalize the gas state by copying the accumulated refund from EvmState.
    /// Called at transaction end before GetReceiptData.
    /// For simple policies this is a no-op (refund used directly from EvmState).
    /// For multigas policies this copies refund into the multigas structure.
    /// </summary>
    /// <param name="gasState">The gas state to finalize</param>
    /// <param name="refund">The accumulated refund from EvmState</param>
    static abstract void FinalizeRefund(ref GasState gasState, long refund);

    /// <summary>
    /// Get policy-specific data for receipt storage (e.g., multigas breakdown).
    /// Returns null for policies without extra data (e.g., Ethereum).
    /// </summary>
    /// <param name="gasState">The final gas state after transaction execution</param>
    /// <returns>Policy-specific receipt data, or null if none</returns>
    static abstract object? GetReceiptData(in GasState gasState);
}
