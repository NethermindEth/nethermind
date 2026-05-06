// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Unit tests for the per-tx 2D block-gas inclusion check from
/// execution-specs PR 2703. Each test mirrors a scenario from
/// <c>tests/amsterdam/eip8037_state_creation_gas_cost_increase/test_state_gas_reservoir.py</c>.
/// These tests target <see cref="Eip8037BlockGasInclusionCheck.Validate"/> directly so the
/// formula itself is pinned regardless of where it eventually gets wired in.
/// </summary>
[TestFixture]
public class Eip8037BlockGasInclusionCheckTests
{
    private const long CostPerStateByte = 1174;
    private const long GasNewAccount = 112; // EIP-8037 GAS_NEW_ACCOUNT (state bytes for new account)
    private const long IntrinsicNewAccountState = GasNewAccount * CostPerStateByte; // 131_488
    private const long BaseIntrinsicRegular = 21_000;
    private const long CreateIntrinsicRegular = 53_000; // base + CREATE intrinsic component
    private const long SStoreStateGas = 32 * CostPerStateByte; // 37_568, GasCostOf.SSetState

    /// <summary>
    /// PR 2703 <c>test_block_state_gas_limit_boundary[exact_fit]</c>: tx2 worst-case
    /// state contribution exactly equals state_available -> accepted (strict &gt; check).
    /// </summary>
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

    /// <summary>
    /// PR 2703 <c>test_block_state_gas_limit_boundary[exceeded]</c>: tx2 worst-case
    /// state contribution exceeds state_available by 1 -> rejected on state dimension.
    /// </summary>
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

    /// <summary>
    /// PR 2703 <c>test_creation_tx_regular_check_subtracts_intrinsic_state</c>:
    /// creation tx where tx.gas exceeds regular_available but
    /// (tx.gas - intrinsic.state) fits -> must be accepted under the new formula.
    /// The OLD formula <c>min(TX_MAX, tx.gas) > regular_available</c> would reject,
    /// proving the spec change is honored.
    /// </summary>
    [Test]
    public void Creation_tx_regular_check_subtracts_intrinsic_state_accepts()
    {
        long intrinsicState = IntrinsicNewAccountState; // GAS_NEW_ACCOUNT * cpsb = 131_488
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

    /// <summary>
    /// PR 2703 <c>test_single_tx_state_check_exceeds_block_limit</c>: a single tx
    /// whose worst-case state contribution exceeds the entire block_gas_limit must
    /// be rejected at inclusion (no prior txs needed).
    /// </summary>
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

    /// <summary>
    /// PR 2703 <c>test_creation_tx_state_check_exceeded</c>: creation tx whose state
    /// contribution exceeds remaining state budget while regular fits -> reject on
    /// state dimension specifically.
    /// </summary>
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

    /// <summary>
    /// EIP-7825 cap interaction: even if (tx.gas - intrinsic.state) is huge, the regular
    /// worst-case is capped at <c>TX_MAX_GAS_LIMIT</c>. This guards the
    /// <c>min(TX_MAX_GAS_LIMIT, ...)</c> branch.
    /// </summary>
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

    /// <summary>
    /// Empty block (no prior txs), benign call: trivially accepted.
    /// </summary>
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
    public void Calculate_block_regular_gas_floor_clamps_invalid_negative_transcript()
    {
        long blockRegularGas = Eip8037BlockGasInclusionCheck.CalculateBlockRegularGas(
            intrinsicRegularGas: 21_000,
            initialRegularGas: 100,
            remainingRegularGas: 100,
            stateGasSpill: 200,
            stateGasSpillReclassified: 0,
            floorGas: 53_000);

        Assert.That(blockRegularGas, Is.EqualTo(53_000));
    }
}
