// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.GasPolicy;

internal enum StateGasChargeOutcome : byte
{
    Success,
    OutOfGas,
}

internal readonly struct StateGasChargeResult(
    StateGasChargeOutcome outcome,
    ulong value,
    long stateReservoir,
    long stateGasUsed,
    long stateGasSpill,
    long stateGasSpillRefunded)
{
    public readonly StateGasChargeOutcome Outcome = outcome;
    public readonly ulong Value = value;
    public readonly long StateReservoir = stateReservoir;
    public readonly long StateGasUsed = stateGasUsed;
    public readonly long StateGasSpill = stateGasSpill;
    public readonly long StateGasSpillRefunded = stateGasSpillRefunded;
}

internal static class StateGasChargeKernel
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasChargeResult TryCharge(
        ulong value,
        long stateReservoir,
        long stateGasUsed,
        long stateGasSpill,
        long stateGasSpillRefunded,
        long stateGasCost)
    {
        if (stateReservoir >= stateGasCost)
        {
            return new StateGasChargeResult(
                StateGasChargeOutcome.Success,
                value,
                unchecked(stateReservoir - stateGasCost),
                unchecked(stateGasUsed + stateGasCost),
                stateGasSpill,
                stateGasSpillRefunded);
        }

        ulong spillAmount = CalculateSpill(stateReservoir, stateGasCost);
        if (value < spillAmount)
        {
            return new StateGasChargeResult(
                StateGasChargeOutcome.OutOfGas,
                value,
                stateReservoir,
                stateGasUsed,
                stateGasSpill,
                stateGasSpillRefunded);
        }

        return new StateGasChargeResult(
            StateGasChargeOutcome.Success,
            value - spillAmount,
            Math.Min(0, stateReservoir),
            unchecked(stateGasUsed + stateGasCost),
            unchecked(stateGasSpill + (long)spillAmount),
            stateGasSpillRefunded);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CalculateSpill(long stateReservoir, long stateGasCost)
    {
        if (stateGasCost <= 0)
        {
            return 0;
        }

        if (stateReservoir <= 0)
        {
            return (ulong)stateGasCost;
        }

        return stateGasCost > stateReservoir ? (ulong)(stateGasCost - stateReservoir) : 0;
    }
}
