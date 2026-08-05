// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class EthereumGasPolicyTests
{
    // Locks the ConsumeDataCopyGas contract: the policy computes base access cost + per-word copy
    // cost internally, so any multidimensional policy can rely on (and re-categorize) the same total.
    [TestCase(false, 0UL, TestName = "CODECOPY/CALLDATACOPY/RETURNDATACOPY, empty")]
    [TestCase(false, 5UL, TestName = "CODECOPY/CALLDATACOPY/RETURNDATACOPY, 5 words")]
    [TestCase(true, 0UL, TestName = "EXTCODECOPY, empty")]
    [TestCase(true, 10UL, TestName = "EXTCODECOPY, 10 words")]
    public void ConsumeDataCopyGas_charges_base_access_plus_per_word_copy(bool isExternalCode, ulong words)
    {
        const ulong initial = 1_000_000;
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(initial);
        EthereumGasPolicy.ConsumeDataCopyGas(ref gas, Cancun.Instance, isExternalCode, words);

        ulong baseCost = isExternalCode ? Cancun.Instance.GasCosts.ExtCodeCost : GasCostOf.VeryLow;
        ulong expected = baseCost + GasCostOf.Memory * words;
        Assert.That(initial - EthereumGasPolicy.GetRemainingGas(in gas), Is.EqualTo(expected));
    }

    [Test]
    public void Default_gas_policy_implementations_are_aggressively_inlined()
    {
        int defaultImplementations = 0;
        foreach (MethodInfo method in typeof(IGasPolicy<>).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.IsAbstract) continue;

            defaultImplementations++;
            Assert.That(
                method.MethodImplementationFlags.HasFlag(MethodImplAttributes.AggressiveInlining),
                Is.True,
                $"{method} must carry [MethodImpl(MethodImplOptions.AggressiveInlining)]: without it, per-opcode gas " +
                "charges compile to real calls in no-dynamic-PGO regimes (e.g. the NativeAOT zkEVM guest).");
        }

        Assert.That(defaultImplementations, Is.GreaterThan(0));
    }

    [Test]
    public void CreateAvailableFromIntrinsic_returns_out_of_gas_when_gas_limit_below_intrinsic()
    {
        EthereumGasPolicy intrinsic = new() { Value = 30_000, StateReservoir = 183_600 };

        EthereumGasPolicy available = EthereumGasPolicy.CreateAvailableFromIntrinsic(30_000, in intrinsic, Amsterdam.Instance);

        Assert.That(EthereumGasPolicy.IsOutOfGas(in available), Is.True);
        Assert.That(EthereumGasPolicy.GetRemainingGas(in available), Is.EqualTo(0UL));
        Assert.That(EthereumGasPolicy.GetStateReservoir(in available), Is.EqualTo(0L));
    }

    [Test]
    public void MinRequiredGasLimit_includes_state_reservoir_unlike_state_blind_minimal_gas()
    {
        EthereumGasPolicy standard = new() { Value = 30_000, StateReservoir = 183_600 };
        EthereumGasPolicy floor = new() { Value = 21_000 };
        IntrinsicGas<EthereumGasPolicy> intrinsic = new(standard, floor);

        Assert.That(intrinsic.StandardGas, Is.EqualTo(213_600UL));
        Assert.That(intrinsic.MinRequiredGasLimit, Is.EqualTo(213_600UL));
        Assert.That(EthereumGasPolicy.GetRemainingGas(intrinsic.MinimalGas), Is.EqualTo(30_000UL));
    }

    [Test]
    public void MinRequiredGasLimit_matches_state_blind_minimal_gas_without_state()
    {
        EthereumGasPolicy standard = new() { Value = 25_000 };
        EthereumGasPolicy floor = new() { Value = 30_000 };
        IntrinsicGas<EthereumGasPolicy> intrinsic = new(standard, floor);

        Assert.That(intrinsic.MinRequiredGasLimit, Is.EqualTo(30_000UL));
        Assert.That(intrinsic.MinRequiredGasLimit, Is.EqualTo(EthereumGasPolicy.GetRemainingGas(intrinsic.MinimalGas)));
    }

    [TestCase(100UL, 40UL, 10L, 50UL, TestName = "positive_reservoir_is_subtracted")]
    [TestCase(100UL, 40UL, -10L, 70UL, TestName = "negative_reservoir_spill_is_added_back")]
#if !DEBUG
    // In Debug, the invariant guard terminates the test process before the Release fallback can run.
    [TestCase(100UL, 101UL, 0L, 100UL, TestName = "gas_left_above_limit_falls_back_to_gas_limit")]
    [TestCase(ulong.MaxValue, 0UL, -1L, ulong.MaxValue, TestName = "spill_overflowing_ulong_falls_back_to_gas_limit")]
#endif
    public void GetPreRefundGas_handles_signed_reservoir_without_wrapping(
        ulong gasLimit,
        ulong remainingGas,
        long stateReservoir,
        ulong expected)
    {
        EthereumGasPolicy gas = new() { Value = remainingGas, StateReservoir = stateReservoir };

        ulong preRefundGas = GetPreRefundGas(in gas, gasLimit);

        Assert.That(preRefundGas, Is.EqualTo(expected));
    }

    private static ulong GetPreRefundGas<TGasPolicy>(in TGasPolicy gas, ulong gasLimit)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        => TGasPolicy.GetPreRefundGas(in gas, gasLimit);
}
