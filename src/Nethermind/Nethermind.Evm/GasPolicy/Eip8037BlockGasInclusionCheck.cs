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

    /// <summary>
    /// Validates if a transaction can be included in a block under EIP-8037 pre-inclusion check.
    /// </summary>
    /// <remarks>
    /// Checks that the transaction's maximum possible contribution to both regular and state gas dimensions
    /// does not exceed the remaining available space for those dimensions in the block.
    /// Refer to EIP-8037 section "Transaction validation":
    /// min(TX_MAX_GAS_LIMIT, tx.gas) &lt;= regular_gas_available and tx.gas &lt;= state_gas_available.
    /// </remarks>
    /// <param name="blockGasLimit">The block's gas limit.</param>
    /// <param name="cumulativeBlockRegular">The block's cumulative regular gas used.</param>
    /// <param name="cumulativeBlockState">The block's cumulative state gas used.</param>
    /// <param name="txGas">The transaction's gas limit.</param>
    /// <param name="intrinsicRegular">Unused. Kept for backward compatibility.</param>
    /// <param name="intrinsicState">Unused. Kept for backward compatibility.</param>
    /// <returns>An Outcome representing the validation result.</returns>
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

        ulong worstCaseRegular = Math.Min(Eip7825Constants.DefaultTxGasLimitCap, txGas);
        if (worstCaseRegular > regularAvailable)
            return Outcome.RegularDimensionExceeded;

        ulong worstCaseState = txGas;
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
        // Saturating: a stale invariant would otherwise wrap and silently blow the block gas limit.
        ulong consumedAfterRefund = initialRegularGas.SaturatingSub(remainingRegularGas);
        ulong executionRegularGasUsed = consumedAfterRefund.SaturatingSub(stateGasSpill) + stateGasSpillReclassified;
        ulong blockRegularGas = intrinsicRegularGas + executionRegularGasUsed;
        return Math.Max(blockRegularGas, floorGas);
    }
}
