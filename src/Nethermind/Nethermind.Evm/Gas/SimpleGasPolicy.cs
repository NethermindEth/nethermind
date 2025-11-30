// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm.Gas;

/// <summary>
/// Standard Ethereum single-dimensional gas policy.
/// </summary>
public readonly struct SimpleGasPolicy : IGasPolicy<SimpleGasPolicy>
{
    /// <summary>
    /// Initialize gas state for a new transaction.
    /// Simple policy: set remaining gas (intrinsic already deducted by TransactionProcessor).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GasState InitializeForTransaction(long gasLimit, long intrinsicGas)
    {
        // gasLimit already has intrinsic deducted by TransactionProcessor
        // PolicyData is null for simple gas (no extra tracking needed)
        return new GasState(gasLimit);
    }

    /// <summary>
    /// Get remaining gas for OOG checks.
    /// Simple policy: direct field access.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetRemainingGas(in GasState gasState)
    {
        return gasState.RemainingGas;
    }

    /// <summary>
    /// Consume gas for an operation.
    /// Simple policy: subtract from remaining gas.
    /// Instruction parameter ignored (no categorization needed).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ConsumeGas(ref GasState gasState, long gasCost, Instruction instruction)
    {
        gasState.RemainingGas -= gasCost;
        // Instruction parameter unused for simple policy
    }

    /// <summary>
    /// Apply gas refund (SSTORE clearing, SELFDESTRUCT).
    /// Simple policy: refunds tracked in EvmState.Refund (unchanged from current).
    /// No action needed here - TransactionProcessor handles refund application.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyRefund(ref GasState gasState, long refundAmount)
    {
        // Refunds are accumulated in EvmState.Refund and applied at transaction end
        // Simple policy doesn't need to track refunds in GasState
        // This method exists for API symmetry with complex policies
    }

    /// <summary>
    /// Refund unused gas (e.g., from failed CALL/CREATE).
    /// Simple policy: add back to remaining gas.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RefundGas(ref GasState gasState, long gasAmount)
    {
        gasState.RemainingGas += gasAmount;
    }

    /// <summary>
    /// Mark the gas state as out of gas.
    /// Simple policy: set remaining gas to zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOutOfGas(ref GasState gasState)
    {
        gasState.RemainingGas = 0;
    }

    /// <summary>
    /// Initialize child call frame gas state.
    /// Simple policy: child gets its own gas state with gasProvided.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GasState InitializeChildFrame(long gasProvided)
    {
        return new GasState(gasProvided);
    }

    /// <summary>
    /// Merge child frame gas back into parent after call completion.
    /// Simple policy: no-op since RemainingGas is handled by VirtualMachine.ExecuteTransaction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MergeChildFrame(
        ref GasState parentState,
        in GasState childState,
        long gasProvided)
    {
        // NOTE: RemainingGas is handled by VirtualMachine.ExecuteTransaction separately
        // This method is for policy-specific data merging (e.g., multigas)
        // Simple policy has no extra data to merge
    }

    /// <summary>
    /// Get final gas used for transaction receipt.
    /// Simple policy: gasLimit minus remaining gas.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetFinalGasUsed(in GasState gasState, long gasLimit)
    {
        return gasLimit - gasState.RemainingGas;
    }

    /// <summary>
    /// Finalize gas state with accumulated refund.
    /// Simple policy: no-op (refund is used directly from EvmState).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FinalizeRefund(ref GasState gasState, long refund)
    {
        // No-op: refund is already in EvmState.Refund, used directly by VirtualMachine
    }

    /// <summary>
    /// Get policy-specific receipt data.
    /// Simple policy: no extra data needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object? GetReceiptData(in GasState gasState)
    {
        return null; // No multigas or other policy data for Ethereum
    }
}
