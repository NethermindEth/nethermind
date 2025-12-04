// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm.Gas;

/// <summary>
/// Standard Ethereum single-dimensional gas policy.
/// Empty struct - sizeof(SimpleGasPolicy) == 1 (CLR minimum), but JIT treats as 0 overhead.
/// </summary>
public readonly struct SimpleGasPolicy : IGasPolicy<SimpleGasPolicy>
{
    /// <summary>
    /// Initialize gas state for a new transaction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GasState<SimpleGasPolicy> InitializeForTransaction(long gasLimit, long intrinsicGas)
    {
        return new GasState<SimpleGasPolicy>(gasLimit);
    }

    /// <summary>
    /// Get remaining gas for OOG checks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetRemainingGas(in GasState<SimpleGasPolicy> gasState)
    {
        return gasState.RemainingGas;
    }

    /// <summary>
    /// Consume gas for an operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ConsumeGas(ref GasState<SimpleGasPolicy> gasState, long gasCost, Instruction instruction)
    {
        gasState.RemainingGas -= gasCost;
    }

    /// <summary>
    /// Refund unused gas (e.g., from failed CALL/CREATE).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RefundGas(ref GasState<SimpleGasPolicy> gasState, long gasAmount)
    {
        gasState.RemainingGas += gasAmount;
    }

    /// <summary>
    /// Mark the gas state as out of gas.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOutOfGas(ref GasState<SimpleGasPolicy> gasState)
    {
        gasState.RemainingGas = 0;
    }
}
