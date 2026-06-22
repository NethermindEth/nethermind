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
        long blockGasLimit,
        long cumulativeBlockRegular,
        long cumulativeBlockState,
        long txGas,
        long intrinsicRegular,
        long intrinsicState)
    {
        long regularAvailable = blockGasLimit - cumulativeBlockRegular;
        long stateAvailable = blockGasLimit - cumulativeBlockState;

        // Keep below-intrinsic txs from producing a negative worst-case regular dimension.
        long worstCaseRegular = Math.Max(0, txGas - intrinsicState);
        if (worstCaseRegular > Eip7825Constants.DefaultTxGasLimitCap)
            worstCaseRegular = Eip7825Constants.DefaultTxGasLimitCap;
        if (worstCaseRegular > regularAvailable)
            return Outcome.RegularDimensionExceeded;

        // The state dimension has no per-tx equivalent of EIP-7825's DefaultTxGasLimitCap;
        // state-heavy work may be funded by the state reservoir above that regular-dimension cap.
        long worstCaseState = Math.Max(0, txGas - intrinsicRegular);
        if (worstCaseState > stateAvailable)
            return Outcome.StateDimensionExceeded;

        return Outcome.Ok;
    }

    public static long CalculateBlockRegularGas(
        long intrinsicRegularGas,
        long initialRegularGas,
        long remainingRegularGas,
        long stateGasSpill,
        long stateGasSpillReclassified)
    {
        // EIP-7778/EIP-8037: the block's regular-gas dimension is the pre-refund regular gas actually
        // consumed. The EIP-7623/7976 calldata floor is a minimum charge on the sender (tx_gas_used /
        // receipts) only and must NOT inflate the block gasUsed.
        long executionRegularGasUsed = initialRegularGas - remainingRegularGas - stateGasSpill + stateGasSpillReclassified;
        return intrinsicRegularGas + executionRegularGasUsed;
    }
}
