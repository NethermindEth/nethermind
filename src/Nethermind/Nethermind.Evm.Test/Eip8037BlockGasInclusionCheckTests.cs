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
    private const ulong CostPerStateByte = 1530;
    private const ulong GasNewAccount = 120; // EIP-8037 GAS_NEW_ACCOUNT
    private const ulong IntrinsicNewAccountState = GasNewAccount * CostPerStateByte;
    private const ulong BaseIntrinsicRegular = 21_000;
    private const ulong CreateIntrinsicRegular = 53_000;
    private const ulong SStoreStateGas = 64 * CostPerStateByte; // GasCostOf.SSetState

    [TestCase(0UL, Eip8037BlockGasInclusionCheck.Outcome.Ok, TestName = "Boundary_state_exact_fit_accepts")]
    [TestCase(1UL, Eip8037BlockGasInclusionCheck.Outcome.StateDimensionExceeded, TestName = "Boundary_state_exceeded_by_one_rejects_on_state_dimension")]
    public void Boundary_state(ulong delta, Eip8037BlockGasInclusionCheck.Outcome expected)
    {
        // tx1: 50 cold SSTOREs within the regular cap; tx1_gas = cap + tx1_state (spec test setup).
        const int numSstores = 50;
        ulong tx1State = numSstores * SStoreStateGas;
        ulong blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + tx1State + 100_000;

        ulong cumR_afterTx1 = BaseIntrinsicRegular + 5_000;
        ulong cumS_afterTx1 = tx1State;

        ulong stateAvailable = blockGasLimit - cumS_afterTx1;
        // EIP-8037: the state dimension reserves the full tx.gas (no intrinsic subtraction).
        ulong tx2Gas = stateAvailable + delta;

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, cumR_afterTx1, cumS_afterTx1, tx2Gas);

        Assert.That(outcome, Is.EqualTo(expected));
    }

    // Regression (spec test creation_tx_regular_check_uses_full_tx_gas): the regular check
    // reserves FULL tx.gas, rejecting even when tx.gas - intrinsic.state would have fit.
    [Test]
    public void Creation_tx_regular_check_uses_full_tx_gas_rejects()
    {
        ulong intrinsicState = IntrinsicNewAccountState;
        ulong intrinsicRegular = CreateIntrinsicRegular;
        ulong intrinsicTotal = intrinsicRegular + intrinsicState;

        // Filler consumed full cap. Remaining regular = intrinsic_regular + 1.
        ulong remainingRegular = intrinsicRegular + 1;
        ulong blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + remainingRegular;
        ulong cumR_afterFiller = blockGasLimit - remainingRegular;
        ulong cumS_afterFiller = 0;

        ulong createTxGas = intrinsicTotal;

        Assert.That(createTxGas, Is.GreaterThan(remainingRegular),
            "full tx.gas must exceed remaining regular so the strict check rejects");
        Assert.That(createTxGas - intrinsicState, Is.LessThanOrEqualTo(remainingRegular),
            "a formula subtracting intrinsic.state would have wrongly accepted");

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, cumR_afterFiller, cumS_afterFiller, createTxGas);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.RegularDimensionExceeded));
    }

    // Single tx whose full gas exceeds the block gas limit in the state dimension -> reject.
    [Test]
    public void Single_tx_state_check_exceeds_block_limit_rejects()
    {
        ulong blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + 100;
        // One over state_available; regular still fits because of the EIP-7825 cap.
        ulong txGas = blockGasLimit + 1;

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, 0, 0, txGas);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.StateDimensionExceeded));
    }

    // Regression: the state check reserves the FULL tx.gas (no intrinsic.regular subtraction).
    // Mirrors the spec test creation_tx_state_check_exceeded.
    [Test]
    public void Creation_tx_state_check_uses_full_tx_gas_rejects_on_state_dimension()
    {
        const int numSstores = 50;
        ulong tx1State = numSstores * SStoreStateGas;
        ulong blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + tx1State + 100_000;

        ulong cumR_afterTx1 = BaseIntrinsicRegular + 5_000;
        ulong cumS_afterTx1 = tx1State;
        ulong stateAvailable = blockGasLimit - cumS_afterTx1;

        // tx2 (creation): full tx.gas = state_available + 1 -> reject on the state dimension.
        ulong createTxGas = stateAvailable + 1;

        // Regular dimension check must pass so rejection is pinned to state.
        ulong regularAvailable = blockGasLimit - cumR_afterTx1;
        ulong worstCaseRegular = Math.Min(Eip7825Constants.DefaultTxGasLimitCap, createTxGas);
        Assert.That(worstCaseRegular, Is.LessThanOrEqualTo(regularAvailable),
            "regular check must pass so rejection is pinned to state dimension");

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, cumR_afterTx1, cumS_afterTx1, createTxGas);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.StateDimensionExceeded));
    }

    // EIP-7825 cap: the regular worst-case is clamped to TX_MAX_GAS_LIMIT regardless of tx.gas,
    // so a huge tx.gas passes the regular dimension but is rejected on the (uncapped) state one.
    [Test]
    public void Regular_check_caps_worst_case_at_tx_max_gas_limit()
    {
        ulong blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + 100; // tiny headroom

        ulong txGas = Eip7825Constants.DefaultTxGasLimitCap * 10;

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit, 0, 0, txGas);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.StateDimensionExceeded));
    }

    [Test]
    public void Empty_block_simple_call_accepts()
    {
        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            blockGasLimit: 30_000_000,
            cumulativeBlockRegular: 0,
            cumulativeBlockState: 0,
            txGas: 21_000);

        Assert.That(outcome, Is.EqualTo(Eip8037BlockGasInclusionCheck.Outcome.Ok));
    }

    [TestCase(379_970UL, 281_520UL, 0UL, 98_450UL, TestName = "Calculate_block_regular_gas_subtracts_state_component")]
    [TestCase(133_379UL, 97_920UL, 0UL, 35_459UL, TestName = "Calculate_block_regular_gas_subtracts_smaller_state_component")]
    [TestCase(12_625UL, 1_566_720UL, 0UL, 0UL, TestName = "Calculate_block_regular_gas_never_negative")]
    [TestCase(100_000UL, 60_000UL, 30_000UL, 40_000UL, TestName = "Calculate_block_regular_gas_uses_regular_gas_above_calldata_floor")]
    [TestCase(100_000UL, 60_000UL, 50_000UL, 50_000UL, TestName = "Calculate_block_regular_gas_applies_calldata_floor_after_state_subtraction")]
    [TestCase(12_625UL, 1_566_720UL, 21_000UL, 21_000UL, TestName = "Calculate_block_regular_gas_applies_calldata_floor_when_state_gas_dominates")]
    public void Calculate_block_regular_gas(
        ulong preRefundGas,
        ulong blockStateGas,
        ulong calldataFloor,
        ulong expected)
        => Assert.That(
            Eip8037BlockGasInclusionCheck.CalculateBlockRegularGas(preRefundGas, blockStateGas, calldataFloor),
            Is.EqualTo(expected));
}
