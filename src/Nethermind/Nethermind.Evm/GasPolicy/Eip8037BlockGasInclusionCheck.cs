// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

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
        ulong worstCaseExecution = WorstCaseExecution(txGas);
        return Validate(blockGasLimit, cumulativeBlockExecution, cumulativeBlockState, worstCaseExecution, txGas);
    }

    /// <summary>Validates a frame transaction's exact per-dimension block reservations against the remaining execution and state capacity (EIP-8141).</summary>
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

    /// <summary>Single source for the per-dimension block gas a transaction reserves, shared by block production admission and end-of-block validation.</summary>
    public static bool TryGetBlockGasReservations(Transaction tx, IReleaseSpec spec, out ulong executionReservation, out ulong stateReservation)
    {
        if (tx.SupportsFrames)
        {
            return FrameTxValidation.TryCalculateBlockGasReservations(tx, spec, out executionReservation, out stateReservation);
        }

        executionReservation = spec.IsEip8037Enabled ? WorstCaseExecution(tx.GasLimit) : tx.GasLimit;
        stateReservation = spec.IsEip8037Enabled ? tx.GasLimit : 0;
        return true;
    }

    private static ulong WorstCaseExecution(ulong txGas) => Math.Min(Eip7825Constants.DefaultTxGasLimitCap, txGas);
}
