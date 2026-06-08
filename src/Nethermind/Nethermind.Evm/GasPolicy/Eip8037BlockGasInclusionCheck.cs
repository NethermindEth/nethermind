// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Evm.GasPolicy;

// EIP-8037 per-tx 2D block-gas inclusion check.
// Both regular and state dimensions must independently fit in the remaining per-dim block
// budget at inclusion time. Block-end validation still enforces max(R, S) <= gas_limit.
public static class Eip8037BlockGasInclusionCheck
{
    public enum Outcome { Ok, RegularDimensionExceeded, StateDimensionExceeded }

    public static Outcome Validate(
        ulong blockGasLimit,
        ulong cumulativeBlockRegular,
        ulong cumulativeBlockState,
        ulong txGas,
        ulong intrinsicRegular,
        ulong intrinsicState)
    {
        ulong regularAvailable = blockGasLimit - cumulativeBlockRegular;
        ulong stateAvailable = blockGasLimit - cumulativeBlockState;

        // Keep below-intrinsic txs from producing a negative worst-case regular dimension.
        ulong worstCaseRegular = txGas > intrinsicState ? txGas - intrinsicState : 0UL;
        if (worstCaseRegular > Eip7825Constants.DefaultTxGasLimitCap)
            worstCaseRegular = Eip7825Constants.DefaultTxGasLimitCap;
        if (worstCaseRegular > regularAvailable)
            return Outcome.RegularDimensionExceeded;

        // The state dimension has no per-tx equivalent of EIP-7825's DefaultTxGasLimitCap;
        // state-heavy work may be funded by the state reservoir above that regular-dimension cap.
        ulong worstCaseState = txGas > intrinsicRegular ? txGas - intrinsicRegular : 0UL;
        if (worstCaseState > stateAvailable)
            return Outcome.StateDimensionExceeded;

        return Outcome.Ok;
    }

    public static ulong CalculateBlockRegularGas(
        ulong intrinsicRegularGas,
        ulong initialRegularGas,
        ulong remainingRegularGas,
        ulong stateGasSpill,
        ulong stateGasSpillReclassified,
        ulong floorGas)
    {
        ulong executionRegularGasUsed = initialRegularGas - remainingRegularGas - stateGasSpill + stateGasSpillReclassified;
        ulong blockRegularGas = intrinsicRegularGas + executionRegularGasUsed;
        return Math.Max(blockRegularGas, floorGas);
    }
}
