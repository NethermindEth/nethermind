// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class TransactionGasInitializationKernelTests
{
    [TestCaseSource(nameof(TransactionCases))]
    public void Transaction_initialization_matches_independent_expected_values(
        ulong gasLimit,
        ulong intrinsicExecutionGas,
        long intrinsicStateGas,
        bool eip8037Enabled,
        bool expectedSuccess,
        ulong expectedGasLeft,
        long expectedStateReservoir,
        long expectedStateGasUsed)
    {
        IReleaseSpec spec = eip8037Enabled ? Amsterdam.Instance : Amsterdam.NoEip8037Instance;
        TransactionGasInitializationResult kernel = TransactionGasInitializationKernel.TryCreate(
            gasLimit,
            intrinsicExecutionGas,
            intrinsicStateGas,
            eip8037Enabled,
            Eip7825Constants.DefaultTxGasLimitCap);
        EthereumGasPolicy intrinsic = new()
        {
            Value = intrinsicExecutionGas,
            StateReservoir = intrinsicStateGas,
            StateGasSpill = 123,
            StateGasSpillRefunded = 456,
        };

        bool success = EthereumGasPolicy.TryCreateAvailableFromIntrinsic(gasLimit, in intrinsic, spec, out EthereumGasPolicy available);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(kernel.Outcome, Is.EqualTo(expectedSuccess
                ? TransactionGasInitializationOutcome.Success
                : TransactionGasInitializationOutcome.IntrinsicGasExceedsLimit));
            Assert.That(success, Is.EqualTo(expectedSuccess));
            Assert.That(
                (kernel.Value, kernel.StateReservoir, kernel.StateGasUsed, kernel.StateGasSpill, kernel.StateGasSpillRefunded),
                Is.EqualTo((expectedGasLeft, expectedStateReservoir, expectedStateGasUsed, 0L, 0L)));
            Assert.That(
                (available.Value, available.StateReservoir, available.StateGasUsed, available.StateGasSpill, available.StateGasSpillRefunded),
                Is.EqualTo((expectedGasLeft, expectedStateReservoir, expectedStateGasUsed, 0L, 0L)));
        }
    }

    [TestCase(0UL, 0UL, 0UL)]
    [TestCase(100UL, 40UL, 100UL)]
    [TestCase(40UL, 100UL, 100UL)]
    [TestCase(100UL, 100UL, 100UL)]
    [TestCase(ulong.MaxValue, 0UL, ulong.MaxValue)]
    [TestCase(ulong.MaxValue - 1UL, ulong.MaxValue, ulong.MaxValue)]
    public void Block_gas_combination_is_the_maximum_of_both_dimensions(
        ulong blockExecutionGas,
        ulong blockStateGas,
        ulong expectedCombinedGas)
    {
        Assert.That(
            BlockGasAccountingKernel.Combine(blockExecutionGas, blockStateGas),
            Is.EqualTo(expectedCombinedGas));
        Assert.That(
            EthereumGasPolicy.CombineBlockGas(blockExecutionGas, blockStateGas),
            Is.EqualTo(expectedCombinedGas));
    }

    private static TestCaseData[] TransactionCases =>
    [
        new TestCaseData(150UL, 100UL, 50L, false, true, 0UL, 0L, 50L).SetName("disabled_exact_intrinsic"),
        new TestCaseData(200UL, 100UL, 50L, false, true, 50UL, 0L, 50L).SetName("disabled_above_intrinsic"),
        new TestCaseData(200UL, 100UL, 50L, true, true, 50UL, 0L, 50L).SetName("enabled_below_cap"),
        new TestCaseData(Eip7825Constants.DefaultTxGasLimitCap + 150UL, 100UL, 50L, true, true,
            Eip7825Constants.DefaultTxGasLimitCap - 100UL, 100L, 50L).SetName("enabled_exact_post_intrinsic_cap"),
        new TestCaseData(Eip7825Constants.DefaultTxGasLimitCap + 151UL, 100UL, 50L, true, true,
            Eip7825Constants.DefaultTxGasLimitCap - 100UL, 101L, 50L).SetName("enabled_one_above_post_intrinsic_cap"),
        new TestCaseData(56_190UL, 21_000UL, GasCostOf.PerAuthBaseState, true, true, 0UL, 0L,
            GasCostOf.PerAuthBaseState).SetName("authorization_state_baseline_exact"),
        new TestCaseData(56_189UL, 21_000UL, GasCostOf.PerAuthBaseState, true, false, 0UL, 0L, 0L)
            .SetName("authorization_state_baseline_one_short"),
        new TestCaseData(Eip7825Constants.DefaultTxGasLimitCap + (ulong)GasCostOf.PerAuthBaseState,
            21_000UL, GasCostOf.PerAuthBaseState, true, true,
            Eip7825Constants.DefaultTxGasLimitCap - 21_000UL, 0L, GasCostOf.PerAuthBaseState)
            .SetName("authorization_state_baseline_at_cap_boundary"),
        new TestCaseData(Eip7825Constants.DefaultTxGasLimitCap + (ulong)GasCostOf.PerAuthBaseState + 1UL,
            21_000UL, GasCostOf.PerAuthBaseState, true, true,
            Eip7825Constants.DefaultTxGasLimitCap - 21_000UL, 1L, GasCostOf.PerAuthBaseState)
            .SetName("authorization_state_baseline_one_above_cap_boundary"),
        new TestCaseData(21_000UL + (ulong)GasCostOf.NewAccountState + (ulong)GasCostOf.PerAuthBaseState,
            21_000UL, GasCostOf.NewAccountState + GasCostOf.PerAuthBaseState, true, true, 0UL, 0L,
            GasCostOf.NewAccountState + GasCostOf.PerAuthBaseState)
            .SetName("new_authority_state_baseline_exact"),
        new TestCaseData(Eip7825Constants.DefaultTxGasLimitCap + (ulong)GasCostOf.PerAuthBaseState + 1UL,
            Eip7825Constants.DefaultTxGasLimitCap, GasCostOf.PerAuthBaseState, true, true, 0UL, 1L,
            GasCostOf.PerAuthBaseState).SetName("execution_intrinsic_at_cap_with_state_reservoir"),
        new TestCaseData(Eip7825Constants.DefaultTxGasLimitCap + 2UL, Eip7825Constants.DefaultTxGasLimitCap + 1UL, 0L, true,
            true, 0UL, 1L, 0L).SetName("intrinsic_execution_above_cap"),
        new TestCaseData(149UL, 100UL, 50L, true, false, 0UL, 0L, 0L).SetName("intrinsic_exceeds_limit"),
        new TestCaseData(ulong.MaxValue, ulong.MaxValue, 0L, false, true, 0UL, 0L, 0L).SetName("maximum_execution_gas_exact"),
        new TestCaseData((ulong)long.MaxValue, 0UL, long.MaxValue, false, true, 0UL, 0L, long.MaxValue)
            .SetName("maximum_intrinsic_state_gas_exact"),
        // Direct policy callers can still present values that normal transaction validation rejects.
        // Pin the fixed-width behavior until the validation boundary is part of the refinement.
        new TestCaseData(ulong.MaxValue, 0UL, 0L, true, true, Eip7825Constants.DefaultTxGasLimitCap,
            -(long)Eip7825Constants.DefaultTxGasLimitCap - 1L, 0L).SetName("maximum_reservoir_cast_wraps_to_signed_long"),
        new TestCaseData(0UL, ulong.MaxValue, 1L, false, true, 0UL, 0L, 1L)
            .SetName("intrinsic_total_uint64_addition_wraps_before_limit_check"),
    ];
}
