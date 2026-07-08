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
        ulong txGas)
    {
        // A cumulative dimension that already exceeded the block limit must reject — silent saturation
        // would otherwise let the worst-case check pass and admit a tx that block-end validation rejects.
        if (cumulativeBlockRegular > blockGasLimit) return Outcome.RegularDimensionExceeded;
        if (cumulativeBlockState > blockGasLimit) return Outcome.StateDimensionExceeded;

        ulong regularAvailable = blockGasLimit - cumulativeBlockRegular;
        ulong stateAvailable = blockGasLimit - cumulativeBlockState;

        // EIP-8037: reserve the full gas limit in each dimension (no intrinsic subtraction). Only the
        // regular dimension is bounded by the EIP-7825 per-tx cap; state work can exceed it via the reservoir.
        ulong worstCaseRegular = Math.Min(Eip7825Constants.DefaultTxGasLimitCap, txGas);
        if (worstCaseRegular > regularAvailable)
            return Outcome.RegularDimensionExceeded;

        if (txGas > stateAvailable)
            return Outcome.StateDimensionExceeded;

        return Outcome.Ok;
    }

    // EIP-8037: tx_regular_gas = tx_gas_used_before_refund - max(0, tx_state_gas); block gasUsed =
    // max(ΣregularPreRefund, Σstate). Before-refund gas is EIP-8037's "Integration with EIP-7778"
    // form and assumes both are active together (base EIP-8037 uses after-refund gas). The
    // EIP-7623/7976 calldata floor is sender-only and must not inflate this dimension.
    public static ulong CalculateBlockRegularGas(ulong preRefundGas, ulong blockStateGas)
        => preRefundGas.SaturatingSub(blockStateGas);
}
