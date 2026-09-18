// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.GasPolicy;

internal enum TransactionGasInitializationOutcome : byte
{
    Success,
    IntrinsicGasExceedsLimit,
}

internal readonly struct TransactionGasInitializationResult(
    TransactionGasInitializationOutcome outcome,
    ulong value,
    long stateReservoir,
    long stateGasUsed,
    long stateGasSpill,
    long stateGasSpillRefunded)
{
    public readonly TransactionGasInitializationOutcome Outcome = outcome;
    public readonly ulong Value = value;
    public readonly long StateReservoir = stateReservoir;
    public readonly long StateGasUsed = stateGasUsed;
    public readonly long StateGasSpill = stateGasSpill;
    public readonly long StateGasSpillRefunded = stateGasSpillRefunded;
}

/// <summary>Pure fixed-width arithmetic for transaction and block gas boundaries.</summary>
internal static class TransactionGasInitializationKernel
{
    /// <summary>
    /// Splits gas remaining after intrinsic charges into execution gas and, when EIP-8037 is
    /// active, the state reservoir. The cap is applied to the post-intrinsic execution budget.
    /// </summary>
    /// <remarks>
    /// The production policy stores the reservoir and state-used counters as <see cref="long"/>.
    /// The casts and additions are therefore explicit unchecked fixed-width operations; normal
    /// transaction validation supplies non-negative, representable intrinsic state gas.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TransactionGasInitializationResult TryCreate(
        ulong gasLimit,
        ulong intrinsicExecutionGas,
        long intrinsicStateGas,
        bool eip8037Enabled,
        ulong executionGasLimitCap)
    {
        ulong intrinsicTotal = unchecked(intrinsicExecutionGas + unchecked((ulong)intrinsicStateGas));
        if (gasLimit < intrinsicTotal)
        {
            return new TransactionGasInitializationResult(
                TransactionGasInitializationOutcome.IntrinsicGasExceedsLimit,
                0,
                0,
                0,
                0,
                0);
        }

        ulong availableGas = gasLimit - intrinsicTotal;
        ulong gasLeft = availableGas;
        if (eip8037Enabled)
        {
            ulong executionGasAfterIntrinsicCap = intrinsicExecutionGas >= executionGasLimitCap
                ? 0
                : executionGasLimitCap - intrinsicExecutionGas;
            gasLeft = Math.Min(availableGas, executionGasAfterIntrinsicCap);
        }

        ulong stateReservoir = availableGas - gasLeft;
        return new TransactionGasInitializationResult(
            TransactionGasInitializationOutcome.Success,
            gasLeft,
            unchecked((long)stateReservoir),
            intrinsicStateGas,
            0,
            0);
    }
}

internal static class BlockGasAccountingKernel
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Combine(ulong blockExecutionGas, ulong blockStateGas) =>
        Math.Max(blockExecutionGas, blockStateGas);
}
