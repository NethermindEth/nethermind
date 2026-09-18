// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm.GasPolicy;

internal enum PrecompileGasPricingOutcome : byte
{
    Success,
    BaseDataOverflow,
    OutOfGas,
}

internal readonly struct PrecompileGasPricingResult(
    PrecompileGasPricingOutcome outcome,
    ulong remainingGas,
    ulong chargedGas)
{
    public readonly PrecompileGasPricingOutcome Outcome = outcome;
    public readonly ulong RemainingGas = remainingGas;
    public readonly ulong ChargedGas = chargedGas;
}

internal static class PrecompileGasPricingKernel
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PrecompileGasPricingResult TryConsume(ulong gas, ulong baseGasCost, ulong dataGasCost)
    {
        if (baseGasCost > ulong.MaxValue - dataGasCost)
            return new PrecompileGasPricingResult(PrecompileGasPricingOutcome.BaseDataOverflow, gas, 0);

        ulong totalGasCost = baseGasCost + dataGasCost;
        if (gas < totalGasCost)
            return new PrecompileGasPricingResult(PrecompileGasPricingOutcome.OutOfGas, 0, 0);

        return new PrecompileGasPricingResult(
            PrecompileGasPricingOutcome.Success,
            gas - totalGasCost,
            totalGasCost);
    }
}
