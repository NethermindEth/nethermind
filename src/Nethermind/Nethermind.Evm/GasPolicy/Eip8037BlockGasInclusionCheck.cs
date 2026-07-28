// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm.GasPolicy;

// EIP-8037 per-tx 2D block-gas inclusion check.
// Each dimension's worst-case contribution must fit in the remaining per-dim block budget at
// inclusion time, subtracting the counterpart dimension's intrinsic gas. Block-end validation
// still enforces max(R, S) <= gas_limit.
public static class Eip8037BlockGasInclusionCheck
{
    public enum Outcome { Ok, RegularDimensionExceeded, StateDimensionExceeded }

    /// <summary>
    /// Validates that a transaction's worst-case per-dimension gas contribution fits in the
    /// remaining block budget at inclusion time.
    /// </summary>
    /// <param name="blockGasLimit">Block gas limit bounding each dimension.</param>
    /// <param name="cumulativeBlockRegular">Cumulative regular gas of all previously included txs.</param>
    /// <param name="cumulativeBlockState">Cumulative state gas of all previously included txs.</param>
    /// <param name="txGas">The candidate transaction's gas limit.</param>
    /// <param name="intrinsicRegular">Intrinsic regular gas of the tx; subtracted from the state-dimension reservation.</param>
    /// <param name="intrinsicState">Intrinsic state gas of the tx; subtracted from the regular-dimension reservation (cross-wired by design).</param>
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

        // EIP-8037: reserve tx.gas minus the counterpart dimension's intrinsic gas. The min() clamps
        // floor the subtraction at zero when intrinsic exceeds tx.gas — such a tx is underfunded and
        // rejected on intrinsic grounds, so the 2D check must not spuriously reject on saturation.
        // Only the regular dimension is bounded by the EIP-7825 per-tx cap; state work can exceed it
        // via the reservoir.
        ulong worstCaseRegular = Math.Min(Eip7825Constants.DefaultTxGasLimitCap, txGas - Math.Min(txGas, intrinsicState));
        if (worstCaseRegular > regularAvailable)
            return Outcome.RegularDimensionExceeded;

        ulong worstCaseState = txGas - Math.Min(txGas, intrinsicRegular);
        if (worstCaseState > stateAvailable)
            return Outcome.StateDimensionExceeded;

        return Outcome.Ok;
    }

    /// <summary>
    /// Calculates EIP-8037 regular block gas after removing state gas and applying the EIP-7976 calldata floor.
    /// </summary>
    public static ulong CalculateBlockRegularGas(ulong preRefundGas, ulong blockStateGas, ulong calldataFloor)
        => Math.Max(preRefundGas.SaturatingSub(blockStateGas), calldataFloor);
}
