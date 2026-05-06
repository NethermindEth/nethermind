// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Evm.GasPolicy;

/// <summary>
/// EIP-8037 (bal-devnet-6) per-tx 2D block-gas inclusion check.
/// Mirrors execution-specs PR 2703 (https://github.com/ethereum/execution-specs/pull/2703):
/// at tx inclusion time, both regular and state dimensions must independently fit in the
/// remaining per-dimension block budget. Block-end validation continues to enforce
/// max(block_regular_gas_used, block_state_gas_used) &lt;= gas_limit.
/// </summary>
public static class Eip8037BlockGasInclusionCheck
{
    public enum Outcome
    {
        Ok,
        RegularDimensionExceeded,
        StateDimensionExceeded,
    }

    /// <summary>
    /// Worst-case 2D inclusion check from execution-specs PR 2703.
    /// </summary>
    /// <param name="blockGasLimit">Block header gas_limit.</param>
    /// <param name="cumulativeBlockRegular">Sum of per-tx BlockGas (regular) for txs already included.</param>
    /// <param name="cumulativeBlockState">Sum of per-tx BlockStateGas for txs already included.</param>
    /// <param name="txGas">tx.gas (per-tx gas limit).</param>
    /// <param name="intrinsicRegular">Per-tx intrinsic regular component.</param>
    /// <param name="intrinsicState">Per-tx intrinsic state component (e.g. GAS_NEW_ACCOUNT * cpsb for creation).</param>
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

        // Per execution-specs PR 2703, the state dimension has no per-tx equivalent of
        // EIP-7825's DefaultTxGasLimitCap; state-heavy work may be funded by the state
        // reservoir above that regular-dimension cap.
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
        long stateGasSpillReclassified,
        long floorGas)
    {
        long executionRegularGasUsed = initialRegularGas - remainingRegularGas - stateGasSpill + stateGasSpillReclassified;
        long blockRegularGas = intrinsicRegularGas + executionRegularGasUsed;
        return Math.Max(blockRegularGas, floorGas);
    }
}
