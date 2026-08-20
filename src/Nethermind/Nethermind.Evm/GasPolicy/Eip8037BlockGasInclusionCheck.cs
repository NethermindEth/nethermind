// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm.GasPolicy;

// EIP-8037 per-tx 2D block-gas inclusion check.
// Both execution and state dimensions must independently fit in the remaining per-dim block
// budget at inclusion time. Block-end validation still enforces max(R, S) <= gas_limit.
public static class Eip8037BlockGasInclusionCheck
{
    public enum Outcome { Ok, ExecutionDimensionExceeded, StateDimensionExceeded }

    public static Outcome Validate(
        ulong blockGasLimit,
        ulong cumulativeBlockExecution,
        ulong cumulativeBlockState,
        ulong txGas)
    {
        // EIP-8037: reserve the full gas limit in each dimension (no intrinsic subtraction). Only the
        // execution dimension is bounded by the EIP-7825 per-tx cap; state work can exceed it via the reservoir.
        ulong worstCaseExecution = Math.Min(Eip7825Constants.DefaultTxGasLimitCap, txGas);
        return Validate(blockGasLimit, cumulativeBlockExecution, cumulativeBlockState, worstCaseExecution, txGas);
    }

    /// <summary>
    /// EIP-8141 frame transactions declare exact per-dimension budgets, so their block reservations are
    /// known rather than worst-cased: the execution reservation is the intrinsic execution cost plus the
    /// frames' execution budgets (bounded below by the calldata floor), and the state reservation is the
    /// frames' state budgets.
    /// </summary>
    public static Outcome Validate(
        ulong blockGasLimit,
        ulong cumulativeBlockExecution,
        ulong cumulativeBlockState,
        ulong executionReservation,
        ulong stateReservation)
    {
        // A cumulative dimension that already exceeded the block limit must reject — silent saturation
        // would otherwise let the worst-case check pass and admit a tx that block-end validation rejects.
        if (cumulativeBlockExecution > blockGasLimit) return Outcome.ExecutionDimensionExceeded;
        if (cumulativeBlockState > blockGasLimit) return Outcome.StateDimensionExceeded;

        ulong executionAvailable = blockGasLimit - cumulativeBlockExecution;
        ulong stateAvailable = blockGasLimit - cumulativeBlockState;

        if (executionReservation > executionAvailable)
            return Outcome.ExecutionDimensionExceeded;

        if (stateReservation > stateAvailable)
            return Outcome.StateDimensionExceeded;

        return Outcome.Ok;
    }

    /// <summary>
    /// Calculates EIP-8037 execution block gas after removing state gas and applying the EIP-7976 calldata floor.
    /// </summary>
    public static ulong CalculateBlockExecutionGas(ulong preRefundGas, ulong blockStateGas, ulong calldataFloor)
        => Math.Max(preRefundGas.SaturatingSub(blockStateGas), calldataFloor);
}
