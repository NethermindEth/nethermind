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

        // EIP-8037: the inclusion check reserves the transaction's full gas limit in each dimension
        // (no intrinsic subtraction). The regular dimension is additionally bounded by EIP-7825's
        // per-tx gas limit cap, since regular execution can never exceed it; the state dimension has
        // no such cap, as state-heavy work may be funded by the state reservoir above it.
        ulong worstCaseRegular = Math.Min(Eip7825Constants.DefaultTxGasLimitCap, txGas);
        if (worstCaseRegular > regularAvailable)
            return Outcome.RegularDimensionExceeded;

        if (txGas > stateAvailable)
            return Outcome.StateDimensionExceeded;

        return Outcome.Ok;
    }

    // EIP-8037 (EELS amsterdam/fork.py): tx_regular_gas = tx_gas_used_before_refund - max(0, tx_state_gas).
    // The block's regular-gas dimension is the pre-refund gas charged minus the state-gas component,
    // which is accounted independently in the state dimension; block gasUsed = max(ΣregularPreRefund,
    // Σstate). Deriving it from gas_left/reservoir/spill deltas instead diverges once spilled state gas
    // is refunded back to gas_left (a clearing-frame SSTORE refund that funds a later CREATE) and can
    // even go negative when the reservoir survives, so subtract the state component directly. The
    // EIP-7623/7976 calldata floor is a sender-only minimum (tx_gas_used / receipts) and must NOT
    // inflate the block's regular-gas dimension.
    public static ulong CalculateBlockRegularGas(ulong preRefundGas, ulong blockStateGas)
        => preRefundGas.SaturatingSub(blockStateGas);
}
