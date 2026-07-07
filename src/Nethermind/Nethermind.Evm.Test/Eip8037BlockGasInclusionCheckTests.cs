// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

// Tests target Eip8037BlockGasInclusionCheck.Validate directly.
[TestFixture]
public class Eip8037BlockGasInclusionCheckTests
{
    private const ulong CostPerStateByte = 1530;
    private const ulong GasNewAccount = 120; // EIP-8037 GAS_NEW_ACCOUNT
    private const ulong IntrinsicNewAccountState = GasNewAccount * CostPerStateByte;
    private const ulong BaseIntrinsicRegular = 21_000;
    private const ulong CreateIntrinsicRegular = 53_000;
    private const ulong SStoreStateGas = 64 * CostPerStateByte; // GasCostOf.SSetState

    // Worst-case state dimension is (tx.gas - intrinsicRegular). Reject when that exceeds state_available.
    [TestCase(BaseIntrinsicRegular, Eip8037BlockGasInclusionCheck.Outcome.Ok, TestName = "Boundary_state_exact_fit_accepts")]
    [TestCase(BaseIntrinsicRegular + 1, Eip8037BlockGasInclusionCheck.Outcome.StateDimensionExceeded, TestName = "Boundary_state_exceeded_by_one_rejects_on_state_dimension")]
    public void Boundary_state(ulong delta, Eip8037BlockGasInclusionCheck.Outcome expected)
    {
        const int numSstores = 50;
        ulong tx1State = numSstores * SStoreStateGas;
        ulong blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + tx1State + 100_000;

        ulong cumR_afterTx1 = BaseIntrinsicRegular + 5_000;
        ulong cumS_afterTx1 = tx1State;

        ulong stateAvailable = blockGasLimit - cumS_afterTx1;
        ulong tx2Gas = stateAvailable + delta;

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, cumR_afterTx1, cumS_afterTx1, tx2Gas, BaseIntrinsicRegular, intrinsicState: 0);

        Assert.That(outcome, Is.EqualTo(expected));
    }

    // Single tx state contribution > block_gas_limit -> reject.
    [Test]
    public void Single_tx_state_check_exceeds_block_limit_rejects()
    {
        ulong intrinsicRegular = BaseIntrinsicRegular;
        ulong intrinsicState = 0; // plain CALL, not creation

        ulong blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + 100;
        // tx.gas - intrinsic.regular > block_gas_limit
        ulong txGas = blockGasLimit + intrinsicRegular + 1;

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, 0, 0, txGas, intrinsicRegular, intrinsicState);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.StateDimensionExceeded));
    }

    // Creation tx state > remaining state while regular fits -> reject on state.
    [Test]
    public void Creation_tx_state_check_exceeded_rejects_on_state_dimension()
    {
        ulong createIntrinsicState = IntrinsicNewAccountState;
        ulong createIntrinsicRegular = CreateIntrinsicRegular;

        const int numSstores = 50;
        ulong tx1State = numSstores * SStoreStateGas;
        ulong blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + tx1State + 100_000;

        ulong cumR_afterTx1 = BaseIntrinsicRegular + 5_000;
        ulong cumS_afterTx1 = tx1State;
        ulong stateAvailable = blockGasLimit - cumS_afterTx1;

        // tx2 (creation): state contribution = state_available + 1 -> reject
        ulong createTxGas = createIntrinsicRegular + stateAvailable + 1;

        // Regular dimension check must pass so rejection is pinned to state.
        ulong regularAvailable = blockGasLimit - cumR_afterTx1;
        ulong worstCaseRegular = Math.Min(Eip7825Constants.DefaultTxGasLimitCap, createTxGas - createIntrinsicState);
        Assert.That(worstCaseRegular, Is.LessThanOrEqualTo(regularAvailable),
            "regular check must pass so rejection is pinned to state dimension");

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, cumR_afterTx1, cumS_afterTx1, createTxGas, createIntrinsicRegular, createIntrinsicState);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.StateDimensionExceeded));
    }

    // EIP-7825 cap: regular worst-case clamped at TX_MAX_GAS_LIMIT regardless of (tx.gas - intrinsic.state).
    [Test]
    public void Regular_check_caps_worst_case_at_tx_max_gas_limit()
    {
        ulong blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + 100; // tiny headroom
        ulong intrinsicRegular = BaseIntrinsicRegular;
        ulong intrinsicState = 0;

        // Pick tx.gas so that (tx.gas - intrinsic.state) >> TX_MAX_GAS_LIMIT but the cap
        // brings worst-case regular back down to TX_MAX_GAS_LIMIT, which fits exactly.
        ulong txGas = Eip7825Constants.DefaultTxGasLimitCap * 10;
        ulong cumR = 0;
        ulong cumS = 0;

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, cumR, cumS, txGas, intrinsicRegular, intrinsicState);

        // Regular passes due to cap. State worst-case = txGas - intrinsicRegular which
        // is enormous and exceeds blockGasLimit -> state dimension rejects.
        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.StateDimensionExceeded));
    }

    [Test]
    public void Empty_block_simple_call_accepts()
    {
        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit: 30_000_000,
            cumulativeBlockRegular: 0,
            cumulativeBlockState: 0,
            txGas: 21_000,
            intrinsicRegular: 21_000,
            intrinsicState: 0);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.Ok));
    }

    [Test]
    public void Regular_worst_case_is_clamped_when_intrinsic_state_exceeds_tx_gas()
    {
        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit: 30_000_000,
            cumulativeBlockRegular: 0,
            cumulativeBlockState: 0,
            txGas: 10,
            intrinsicRegular: 5,
            intrinsicState: 20);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.Ok));
    }

    [Test]
    public void Calculate_block_regular_gas_keeps_valid_transcripts_non_negative()
    {
        Random random = new(8037);
        for (int i = 0; i < 2_000; i++)
        {
            ulong intrinsicRegular = random.NextUInt64(21_000, 500_000);
            ulong initialRegular = random.NextUInt64(0, 5_000_000);
            ulong spentRegular = random.NextUInt64(0, Math.Min(initialRegular, int.MaxValue)) + (initialRegular > int.MaxValue ? random.NextUInt64(0, 2) : 0ul);
            if (spentRegular > initialRegular)
            {
                spentRegular = initialRegular;
            }

            ulong stateGasSpill = random.NextUInt64(0, Math.Min(spentRegular, int.MaxValue));
            ulong remainingRegular = initialRegular - spentRegular;
            ulong floorGas = random.NextUInt64(21_000, 200_000);

            ulong executionRegularGasUsed = initialRegular - remainingRegular - stateGasSpill;
            ulong blockRegularGas = Eip8037BlockGasInclusionCheck.CalculateBlockRegularGas(
                intrinsicRegular,
                initialRegular,
                remainingRegular,
                stateGasSpill,
                floorGas);

            Assert.That(executionRegularGasUsed, Is.GreaterThanOrEqualTo(0ul));
            Assert.That(blockRegularGas, Is.EqualTo(Math.Max(intrinsicRegular + executionRegularGasUsed, floorGas)));
        }
    }

    [TestCase(300UL, 100UL, TestName = "Calculate_block_regular_gas_floor_clamps_low_regular_gas")]
    [TestCase(0UL, 0UL, TestName = "Calculate_block_regular_gas_allows_negative_execution_intermediate")]
    public void Calculate_block_regular_gas_clamps_to_floor(ulong initialRegular, ulong remainingRegular)
    {
        ulong blockRegularGas = Eip8037BlockGasInclusionCheck.CalculateBlockRegularGas(
            intrinsicRegularGas: 21_000,
            initialRegularGas: initialRegular,
            remainingRegularGas: remainingRegular,
            stateGasSpill: 200,
            floorGas: 53_000);

        Assert.That(blockRegularGas, Is.EqualTo(53_000ul));
    }
}
