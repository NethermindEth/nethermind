// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

// Tests target Eip8037BlockGasInclusionCheck.Validate directly.
[TestFixture]
public class Eip8037BlockGasInclusionCheckTests
{
    private const long CostPerStateByte = 1530;
    private const long GasNewAccount = 120; // EIP-8037 GAS_NEW_ACCOUNT
    private const long IntrinsicNewAccountState = GasNewAccount * CostPerStateByte;
    private const long BaseIntrinsicRegular = 21_000;
    private const long CreateIntrinsicRegular = 53_000;
    private const long SStoreStateGas = 64 * CostPerStateByte; // GasCostOf.SSetState

    // Boundary case: state contribution == state_available -> accepted (strict >).
    [Test]
    public void Boundary_state_exact_fit_accepts()
    {
        // tx1: 50 cold SSTOREs in regular cap budget. Reproduces the spec test
        // setup: tx1_state = num_sstores * sstore_state_gas; tx1_gas = cap + tx1_state.
        const int numSstores = 50;
        long tx1State = numSstores * SStoreStateGas;
        long blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + tx1State + 100_000;

        // After tx1 lands: cumR = (some regular), cumS = tx1_state.
        long cumR_afterTx1 = BaseIntrinsicRegular + 5_000; // arbitrary regular work
        long cumS_afterTx1 = tx1State;

        // tx2 sized so its worst-case state contribution = state_available exactly.
        long stateAvailable = blockGasLimit - cumS_afterTx1;
        long tx2Gas = BaseIntrinsicRegular + stateAvailable + 0; // delta = 0
        long tx2IntrinsicRegular = BaseIntrinsicRegular;
        long tx2IntrinsicState = 0;

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, cumR_afterTx1, cumS_afterTx1, tx2Gas, tx2IntrinsicRegular, tx2IntrinsicState);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.Ok),
            "tx2 worst-case state contribution exactly fills state_available; spec uses strict > so this must be accepted");
    }

    // Boundary case: state contribution > state_available by 1 -> reject on state.
    [Test]
    public void Boundary_state_exceeded_by_one_rejects_on_state_dimension()
    {
        const int numSstores = 50;
        long tx1State = numSstores * SStoreStateGas;
        long blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + tx1State + 100_000;

        long cumR_afterTx1 = BaseIntrinsicRegular + 5_000;
        long cumS_afterTx1 = tx1State;

        long stateAvailable = blockGasLimit - cumS_afterTx1;
        long tx2Gas = BaseIntrinsicRegular + stateAvailable + 1; // delta = 1
        long tx2IntrinsicRegular = BaseIntrinsicRegular;
        long tx2IntrinsicState = 0;

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, cumR_afterTx1, cumS_afterTx1, tx2Gas, tx2IntrinsicRegular, tx2IntrinsicState);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.StateDimensionExceeded),
            "spec must reject on state dimension, not regular");
    }

    // Creation tx: tx.gas > regular_available but (tx.gas - intrinsic.state) fits.
    [Test]
    public void Creation_tx_regular_check_subtracts_intrinsic_state_accepts()
    {
        long intrinsicState = IntrinsicNewAccountState;
        long intrinsicRegular = CreateIntrinsicRegular;
        long intrinsicTotal = intrinsicRegular + intrinsicState;

        // Filler consumed full cap. Remaining regular = intrinsic_regular + 1.
        long remainingRegular = intrinsicRegular + 1;
        long blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + remainingRegular;
        long cumR_afterFiller = blockGasLimit - remainingRegular;
        long cumS_afterFiller = 0;

        // Creation tx: tx.gas = intrinsic_total. Raw tx.gas > remaining_regular
        // but tx.gas - intrinsic_state = intrinsic_regular <= remaining_regular.
        long createTxGas = intrinsicTotal;

        Assert.That(createTxGas, Is.GreaterThan(remainingRegular),
            "old formula must reject -> proves new formula behaves differently");
        Assert.That(createTxGas - intrinsicState, Is.LessThanOrEqualTo(remainingRegular),
            "new formula must accept");

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, cumR_afterFiller, cumS_afterFiller, createTxGas, intrinsicRegular, intrinsicState);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.Ok));
    }

    // Single tx state contribution > block_gas_limit -> reject.
    [Test]
    public void Single_tx_state_check_exceeds_block_limit_rejects()
    {
        long intrinsicRegular = BaseIntrinsicRegular;
        long intrinsicState = 0; // plain CALL, not creation

        long blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + 100;
        // tx.gas - intrinsic.regular > block_gas_limit
        long txGas = blockGasLimit + intrinsicRegular + 1;

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, 0, 0, txGas, intrinsicRegular, intrinsicState);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.StateDimensionExceeded));
    }

    // Creation tx state > remaining state while regular fits -> reject on state.
    [Test]
    public void Creation_tx_state_check_exceeded_rejects_on_state_dimension()
    {
        long createIntrinsicState = IntrinsicNewAccountState;
        long createIntrinsicRegular = CreateIntrinsicRegular;

        const int numSstores = 50;
        long tx1State = numSstores * SStoreStateGas;
        long blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + tx1State + 100_000;

        long cumR_afterTx1 = BaseIntrinsicRegular + 5_000;
        long cumS_afterTx1 = tx1State;
        long stateAvailable = blockGasLimit - cumS_afterTx1;

        // tx2 (creation): state contribution = state_available + 1 -> reject
        long createTxGas = createIntrinsicRegular + stateAvailable + 1;

        // Regular dimension check must pass so rejection is pinned to state.
        long regularAvailable = blockGasLimit - cumR_afterTx1;
        long worstCaseRegular = System.Math.Min(Eip7825Constants.DefaultTxGasLimitCap, createTxGas - createIntrinsicState);
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
        long blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + 100; // tiny headroom
        long intrinsicRegular = BaseIntrinsicRegular;
        long intrinsicState = 0;

        // Pick tx.gas so that (tx.gas - intrinsic.state) >> TX_MAX_GAS_LIMIT but the cap
        // brings worst-case regular back down to TX_MAX_GAS_LIMIT, which fits exactly.
        long txGas = Eip7825Constants.DefaultTxGasLimitCap * 10;
        long cumR = 0;
        long cumS = 0;

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
            long intrinsicRegular = random.Next(21_000, 500_000);
            long initialRegular = random.Next(0, 5_000_000);
            long spentRegular = random.Next(0, (int)Math.Min(initialRegular, int.MaxValue)) + (initialRegular > int.MaxValue ? random.Next(0, 2) : 0);
            if (spentRegular > initialRegular)
            {
                spentRegular = initialRegular;
            }

            long stateGasSpill = random.Next(0, (int)Math.Min(spentRegular, int.MaxValue));
            long stateGasSpillReclassified = random.Next(0, (int)Math.Min(stateGasSpill, int.MaxValue));
            long remainingRegular = initialRegular - spentRegular;
            long floorGas = random.Next(21_000, 200_000);

            long executionRegularGasUsed = initialRegular - remainingRegular - stateGasSpill + stateGasSpillReclassified;
            long blockRegularGas = Eip8037BlockGasInclusionCheck.CalculateBlockRegularGas(
                intrinsicRegular,
                initialRegular,
                remainingRegular,
                stateGasSpill,
                stateGasSpillReclassified,
                floorGas);

            Assert.That(executionRegularGasUsed, Is.GreaterThanOrEqualTo(0L));
            Assert.That(blockRegularGas, Is.EqualTo(Math.Max(intrinsicRegular + executionRegularGasUsed, floorGas)));
        }
    }

    [Test]
    public void Calculate_block_regular_gas_floor_clamps_low_regular_gas()
    {
        long blockRegularGas = Eip8037BlockGasInclusionCheck.CalculateBlockRegularGas(
            intrinsicRegularGas: 21_000,
            initialRegularGas: 300,
            remainingRegularGas: 100,
            stateGasSpill: 200,
            stateGasSpillReclassified: 0,
            floorGas: 53_000);

        Assert.That(blockRegularGas, Is.EqualTo(53_000));
    }

    [Test]
    public void Calculate_block_regular_gas_allows_negative_execution_intermediate()
    {
        long blockRegularGas = Eip8037BlockGasInclusionCheck.CalculateBlockRegularGas(
            intrinsicRegularGas: 21_000,
            initialRegularGas: 0,
            remainingRegularGas: 0,
            stateGasSpill: 200,
            stateGasSpillReclassified: 0,
            floorGas: 53_000);

        Assert.That(blockRegularGas, Is.EqualTo(53_000));
    }
}
