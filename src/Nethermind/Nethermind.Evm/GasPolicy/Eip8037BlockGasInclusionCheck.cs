// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;

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
        // A cumulative dimension that already exceeded the block limit must reject — silent saturation
        // would otherwise let the worst-case check pass and admit a tx that block-end validation rejects.
        if (cumulativeBlockRegular > blockGasLimit) return Outcome.RegularDimensionExceeded;
        if (cumulativeBlockState > blockGasLimit) return Outcome.StateDimensionExceeded;

        ulong regularAvailable = blockGasLimit - cumulativeBlockRegular;
        ulong stateAvailable = blockGasLimit - cumulativeBlockState;

        // Keep below-intrinsic txs from producing a negative worst-case regular dimension.
        ulong worstCaseRegular = txGas.SaturatingSub(intrinsicState);
        if (worstCaseRegular > Eip7825Constants.DefaultTxGasLimitCap)
            worstCaseRegular = Eip7825Constants.DefaultTxGasLimitCap;
        if (worstCaseRegular > regularAvailable)
            return Outcome.RegularDimensionExceeded;

        // The state dimension has no per-tx equivalent of EIP-7825's DefaultTxGasLimitCap;
        // state-heavy work may be funded by the state reservoir above that regular-dimension cap.
        ulong worstCaseState = txGas.SaturatingSub(intrinsicRegular);
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
        // SaturatingSub preserves the master-side defense-in-depth: any upstream
        // accounting flaw clamps to 0 and Math.Max below falls back to floorGas,
        // rather than wrapping to a giant value and corrupting header.GasUsed.
        ulong executionRegularGasUsed = initialRegularGas
            .SaturatingSub(remainingRegularGas)
            .SaturatingSub(stateGasSpill)
            + stateGasSpillReclassified;
        ulong blockRegularGas = intrinsicRegularGas + executionRegularGasUsed;
        return Math.Max(blockRegularGas, floorGas);
    }
}
