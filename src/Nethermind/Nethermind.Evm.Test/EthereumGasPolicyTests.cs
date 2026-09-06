// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class EthereumGasPolicyTests
{
    [Test]
    public void Memory_cost_preserves_preexpanded_range_and_full_width_validation(
        [Values(0UL, 1UL, 31UL, 32UL, 33UL, ulong.MaxValue)] ulong offset,
        [Values(0UL, 1UL, 32UL, 64UL)] ulong length, [Values] bool highLimb,
        [Values] bool wideLength)
    {
        EvmPooledMemory memory = new();
        memory.CalculateMemoryCost(UInt256.Zero, 64, out _);
        UInt256 position = new(offset, highLimb ? 1UL : 0UL, 0, 0);
        UInt256 uint256Length = length;
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(0);

        bool success = wideLength
            ? EthereumGasPolicy.UpdateMemoryCost(ref gas, in position, in uint256Length, ref memory)
            : EthereumGasPolicy.UpdateMemoryCost(ref gas, in position, length, ref memory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.EqualTo(length == 0 || (!highLimb && offset <= 64 && length <= 64 - offset)));
            Assert.That(EthereumGasPolicy.GetRemainingGas(in gas), Is.Zero);
        }
    }

    [Test, Combinatorial]
    public void Specialized_account_access_matches_dynamic_policy_without_reading_fork_flags(
        [Values] bool eip8038,
        [Values] bool hotAndCold,
        [Values] bool prewarm,
        [Values] bool tracingAccess,
        [Values(AccountAccessKind.Default, AccountAccessKind.SelfDestructBeneficiary)] AccountAccessKind kind,
        [Values(0UL, 100UL, 2600UL, 10000UL)] ulong availableGas)
    {
        IReleaseSpec spec = CreateAccessSpec(hotAndCold, eip8038);
        using StackAccessTracker dynamicTracker = new();
        using StackAccessTracker specializedTracker = new();
        if (prewarm)
        {
            dynamicTracker.WarmUp(TestItem.AddressC);
            specializedTracker.WarmUp(TestItem.AddressC);
        }
        EthereumGasPolicy dynamicGas = EthereumGasPolicy.FromULong(availableGas);
        EthereumGasPolicy specializedGas = EthereumGasPolicy.FromULong(availableGas);
        bool expected = EthereumGasPolicy.TryConsumeAccountAccessGas(ref dynamicGas, spec, in dynamicTracker, tracingAccess, TestItem.AddressC, kind);
        spec.ClearReceivedCalls();

        bool actual = (hotAndCold, eip8038) switch
        {
            (true, true) => EthereumGasPolicy.TryConsumeAccountAccessGas<OnFlag, OnFlag>(ref specializedGas, spec, in specializedTracker, tracingAccess, TestItem.AddressC, kind),
            (true, false) => EthereumGasPolicy.TryConsumeAccountAccessGas<OnFlag, OffFlag>(ref specializedGas, spec, in specializedTracker, tracingAccess, TestItem.AddressC, kind),
            (false, true) => EthereumGasPolicy.TryConsumeAccountAccessGas<OffFlag, OnFlag>(ref specializedGas, spec, in specializedTracker, tracingAccess, TestItem.AddressC, kind),
            (false, false) => EthereumGasPolicy.TryConsumeAccountAccessGas<OffFlag, OffFlag>(ref specializedGas, spec, in specializedTracker, tracingAccess, TestItem.AddressC, kind),
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            AssertGasMatches(in specializedGas, in dynamicGas);
            Assert.That(specializedTracker.IsCold(TestItem.AddressC), Is.EqualTo(dynamicTracker.IsCold(TestItem.AddressC)));
        }
        _ = spec.DidNotReceive().IsEip8038Enabled;
        _ = spec.DidNotReceive().UseHotAndColdStorage;
    }

    [Test, Combinatorial]
    public void Specialized_storage_access_matches_dynamic_policy_without_reading_fork_flags(
        [Values] bool eip8038,
        [Values] bool hotAndCold,
        [Values] bool prewarm,
        [Values] bool tracingAccess,
        [Values(StorageAccessType.SLOAD, StorageAccessType.SSTORE)] StorageAccessType kind,
        [Values(0UL, 100UL, 2099UL, 2100UL)] ulong availableGas)
    {
        IReleaseSpec spec = CreateAccessSpec(hotAndCold, eip8038);
        StorageCell cell = new(TestItem.AddressC, 1);
        using StackAccessTracker dynamicTracker = new();
        using StackAccessTracker specializedTracker = new();
        if (prewarm)
        {
            dynamicTracker.WarmUp(in cell);
            specializedTracker.WarmUp(in cell);
        }
        EthereumGasPolicy dynamicGas = EthereumGasPolicy.FromULong(availableGas);
        EthereumGasPolicy specializedGas = EthereumGasPolicy.FromULong(availableGas);
        bool expected = EthereumGasPolicy.TryConsumeStorageAccessGas(ref dynamicGas, in dynamicTracker, tracingAccess, in cell, kind, spec);
        spec.ClearReceivedCalls();

        bool actual = (hotAndCold, eip8038) switch
        {
            (true, true) => EthereumGasPolicy.TryConsumeStorageAccessGas<OnFlag, OnFlag>(ref specializedGas, in specializedTracker, tracingAccess, in cell, kind, spec),
            (true, false) => EthereumGasPolicy.TryConsumeStorageAccessGas<OnFlag, OffFlag>(ref specializedGas, in specializedTracker, tracingAccess, in cell, kind, spec),
            (false, true) => EthereumGasPolicy.TryConsumeStorageAccessGas<OffFlag, OnFlag>(ref specializedGas, in specializedTracker, tracingAccess, in cell, kind, spec),
            (false, false) => EthereumGasPolicy.TryConsumeStorageAccessGas<OffFlag, OffFlag>(ref specializedGas, in specializedTracker, tracingAccess, in cell, kind, spec),
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            AssertGasMatches(in specializedGas, in dynamicGas);
            Assert.That(specializedTracker.IsCold(in cell), Is.EqualTo(dynamicTracker.IsCold(in cell)));
        }
        _ = spec.DidNotReceive().IsEip8038Enabled;
        _ = spec.DidNotReceive().UseHotAndColdStorage;
    }

    [Test, Combinatorial]
    public void Specialized_net_metered_sstore_matches_dynamic_policy_without_reading_fork_flags(
        [Values] bool eip8038,
        [Values(0UL, 100UL, 2200UL, 100000UL)] ulong availableGas)
    {
        IReleaseSpec spec = CreateGasCostSpec(eip8038);
        EthereumGasPolicy dynamicGas = EthereumGasPolicy.FromULong(availableGas);
        EthereumGasPolicy specializedGas = dynamicGas;
        bool expected = NetMeteredDynamic(ref dynamicGas, spec);
        spec.ClearReceivedCalls();
        bool actual = eip8038
            ? EthereumGasPolicy.TryConsumeNetMeteredSStoreGas<OnFlag>(ref specializedGas, spec)
            : EthereumGasPolicy.TryConsumeNetMeteredSStoreGas<OffFlag>(ref specializedGas, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            AssertGasMatches(in specializedGas, in dynamicGas);
        }
        _ = spec.DidNotReceive().IsEip8038Enabled;
    }

    [Test, Combinatorial]
    public void Specialized_storage_write_matches_dynamic_policy_without_reading_fork_flags(
        [Values] bool eip8037, [Values] bool slotCreation, [Values] bool eip8038,
        [Values(0UL, 100UL, 20000UL, 100000UL)] ulong availableGas)
    {
        IReleaseSpec spec = CreateGasCostSpec(eip8038);
        EthereumGasPolicy dynamicGas = EthereumGasPolicy.FromULong(availableGas);
        EthereumGasPolicy specializedGas = dynamicGas;
        bool expected = StorageWriteDynamic(ref dynamicGas, spec, eip8037, slotCreation);
        spec.ClearReceivedCalls();
        bool actual = StorageWriteSpecialized(ref specializedGas, spec, eip8037, slotCreation, eip8038);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            AssertGasMatches(in specializedGas, in dynamicGas);
        }
        _ = spec.DidNotReceive().IsEip8038Enabled;
    }

    // The net-metered charge has no non-generic form on the policy, so reach the interface default.
    private static bool NetMeteredDynamic<TPolicy>(ref TPolicy gas, IReleaseSpec spec)
        where TPolicy : struct, IGasPolicy<TPolicy> => TPolicy.TryConsumeNetMeteredSStoreGas(ref gas, spec);

    private static bool StorageWriteDynamic(ref EthereumGasPolicy gas, IReleaseSpec spec, bool eip8037, bool slotCreation) =>
        (eip8037, slotCreation) switch
        {
            (true, true) => EthereumGasPolicy.TryConsumeStorageWrite<OnFlag, OnFlag>(ref gas, spec),
            (true, false) => EthereumGasPolicy.TryConsumeStorageWrite<OnFlag, OffFlag>(ref gas, spec),
            (false, true) => EthereumGasPolicy.TryConsumeStorageWrite<OffFlag, OnFlag>(ref gas, spec),
            (false, false) => EthereumGasPolicy.TryConsumeStorageWrite<OffFlag, OffFlag>(ref gas, spec),
        };

    private static bool StorageWriteSpecialized(ref EthereumGasPolicy gas, IReleaseSpec spec, bool eip8037, bool slotCreation, bool eip8038) =>
        (eip8037, slotCreation, eip8038) switch
        {
            (true, true, true) => EthereumGasPolicy.TryConsumeStorageWrite<OnFlag, OnFlag, OnFlag>(ref gas, spec),
            (true, true, false) => EthereumGasPolicy.TryConsumeStorageWrite<OnFlag, OnFlag, OffFlag>(ref gas, spec),
            (true, false, true) => EthereumGasPolicy.TryConsumeStorageWrite<OnFlag, OffFlag, OnFlag>(ref gas, spec),
            (true, false, false) => EthereumGasPolicy.TryConsumeStorageWrite<OnFlag, OffFlag, OffFlag>(ref gas, spec),
            (false, true, true) => EthereumGasPolicy.TryConsumeStorageWrite<OffFlag, OnFlag, OnFlag>(ref gas, spec),
            (false, true, false) => EthereumGasPolicy.TryConsumeStorageWrite<OffFlag, OnFlag, OffFlag>(ref gas, spec),
            (false, false, true) => EthereumGasPolicy.TryConsumeStorageWrite<OffFlag, OffFlag, OnFlag>(ref gas, spec),
            (false, false, false) => EthereumGasPolicy.TryConsumeStorageWrite<OffFlag, OffFlag, OffFlag>(ref gas, spec),
        };

    // The costs have to agree with the flag: SpecGasCosts folds NetMeteredSStoreCost to Free under
    // EIP-8038, and that is the invariant the specialized forms rely on.
    private static IReleaseSpec CreateGasCostSpec(bool eip8038)
    {
        IReleaseSpec source = eip8038 ? Amsterdam.Instance : Osaka.Instance;
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8038Enabled.Returns(eip8038);
        spec.GasCosts.Returns(source.GasCosts);
        return spec;
    }

    private static IReleaseSpec CreateAccessSpec(bool hotAndCold, bool eip8038)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.UseHotAndColdStorage.Returns(hotAndCold);
        spec.IsEip8038Enabled.Returns(eip8038);
        spec.Precompiles.Returns(((IReleaseSpec)Cancun.Instance).Precompiles);
        return spec;
    }

    private static void AssertGasMatches(in EthereumGasPolicy actual, in EthereumGasPolicy expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EthereumGasPolicy.GetRemainingGas(in actual), Is.EqualTo(EthereumGasPolicy.GetRemainingGas(in expected)));
        }
    }

    [Test, Combinatorial]
    public void Specialized_child_reservation_matches_dynamic_policy(
        [Values] bool eip150, [Values] bool create,
        [Values(0UL, 1UL, 63UL, 64UL, 65UL)] ulong availableGas,
        [Values(0UL, 63UL, 64UL, ulong.MaxValue)] ulong requestedGas,
        [Values] bool exceedsUint64)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.Use63Over64Rule.Returns(eip150);
        EthereumGasPolicy dynamicGas = EthereumGasPolicy.FromULong(availableGas);
        EthereumGasPolicy specializedGas = dynamicGas;
        UInt256 request = exceedsUint64 ? UInt256.MaxValue : requestedGas;
        bool expected = create
            ? EthereumGasPolicy.TryReserveChildGas(ref dynamicGas, spec, out ulong expectedChild)
            : EthereumGasPolicy.TryReserveChildGas(ref dynamicGas, in request, spec, out expectedChild);
        spec.ClearReceivedCalls();
        bool actual = create
            ? eip150
                ? EthereumGasPolicy.TryReserveChildGas<OnFlag>(ref specializedGas, spec, out ulong actualChild)
                : EthereumGasPolicy.TryReserveChildGas<OffFlag>(ref specializedGas, spec, out actualChild)
            : eip150
                ? EthereumGasPolicy.TryReserveChildGas<OnFlag>(ref specializedGas, in request, spec, out actualChild)
                : EthereumGasPolicy.TryReserveChildGas<OffFlag>(ref specializedGas, in request, spec, out actualChild);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(actualChild, Is.EqualTo(expectedChild));
            AssertGasMatches(in specializedGas, in dynamicGas);
        }
        _ = spec.DidNotReceive().Use63Over64Rule;
    }

    [Test, Combinatorial]
    public void Specialized_create_charge_matches_dynamic_policy(
        [Values] bool eip8037, [Values] bool eip3860, [Values] bool eip8038, [Values] bool create2,
        [Values(0UL, 31999UL, 32000UL, 100000UL)] ulong availableGas, [Values(0UL, 5UL)] ulong words)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip3860Enabled.Returns(eip3860);
        spec.IsEip8038Enabled.Returns(eip8038);
        EthereumGasPolicy dynamicGas = EthereumGasPolicy.FromULong(availableGas);
        EthereumGasPolicy specializedGas = dynamicGas;
        bool expected = ChargeCreate<EvmInstructions.DynamicCreateSpec>(ref dynamicGas, spec, eip8037, create2, words);
        spec.ClearReceivedCalls();
        bool actual = (eip3860, eip8038) switch
        {
            (true, true) => ChargeCreate<EvmInstructions.CreateSpec<OffFlag, OffFlag, OnFlag, OnFlag>>(ref specializedGas, spec, eip8037, create2, words),
            (true, false) => ChargeCreate<EvmInstructions.CreateSpec<OffFlag, OffFlag, OnFlag, OffFlag>>(ref specializedGas, spec, eip8037, create2, words),
            (false, true) => ChargeCreate<EvmInstructions.CreateSpec<OffFlag, OffFlag, OffFlag, OnFlag>>(ref specializedGas, spec, eip8037, create2, words),
            (false, false) => ChargeCreate<EvmInstructions.CreateSpec<OffFlag, OffFlag, OffFlag, OffFlag>>(ref specializedGas, spec, eip8037, create2, words),
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            AssertGasMatches(in specializedGas, in dynamicGas);
        }
        _ = spec.DidNotReceive().IsEip3860Enabled;
        _ = spec.DidNotReceive().IsEip8038Enabled;
    }

    private static bool ChargeCreate<TSpec>(ref EthereumGasPolicy gas, IReleaseSpec spec, bool eip8037, bool create2, ulong words)
        where TSpec : struct, EvmInstructions.ICreateSpec =>
        (eip8037, create2) switch
        {
            (true, true) => TSpec.TryConsumeCreateGas<EthereumGasPolicy, OnFlag, EvmInstructions.OpCreate2>(ref gas, spec, words),
            (true, false) => TSpec.TryConsumeCreateGas<EthereumGasPolicy, OnFlag, EvmInstructions.OpCreate>(ref gas, spec, words),
            (false, true) => TSpec.TryConsumeCreateGas<EthereumGasPolicy, OffFlag, EvmInstructions.OpCreate2>(ref gas, spec, words),
            (false, false) => TSpec.TryConsumeCreateGas<EthereumGasPolicy, OffFlag, EvmInstructions.OpCreate>(ref gas, spec, words),
        };

    // Locks the TryConsumeDataCopyGas contract: the policy computes base access cost + per-word copy
    // cost internally, so any multidimensional policy can rely on (and re-categorize) the same total.
    [TestCase(false, 0UL, TestName = "CODECOPY/CALLDATACOPY/RETURNDATACOPY, empty")]
    [TestCase(false, 5UL, TestName = "CODECOPY/CALLDATACOPY/RETURNDATACOPY, 5 words")]
    [TestCase(true, 0UL, TestName = "EXTCODECOPY, empty")]
    [TestCase(true, 10UL, TestName = "EXTCODECOPY, 10 words")]
    public void ConsumeDataCopyGas_charges_base_access_plus_per_word_copy(bool isExternalCode, ulong words)
    {
        const ulong initial = 1_000_000;
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(initial);
        EthereumGasPolicy.TryConsumeDataCopyGas(ref gas, Cancun.Instance, isExternalCode, words);

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

    [TestCase(0UL)]
    [TestCase(100UL)]
    public void ClearExecutionGas_preserves_state_gas_accounting(ulong executionGas)
    {
        EthereumGasPolicy gas = new()
        {
            Value = executionGas,
            StateReservoir = 50,
            StateGasUsed = 30,
            StateGasSpill = 20,
            StateGasSpillRefunded = 10,
        };

        EthereumGasPolicy.ClearExecutionGas(ref gas);

        Assert.That((gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill, gas.StateGasSpillRefunded),
            Is.EqualTo((0UL, 50L, 30L, 20L, 10L)));
    }

    [Test]
    public void CreateAvailableFromIntrinsic_returns_out_of_gas_when_gas_limit_below_intrinsic()
    {
        EthereumGasPolicy intrinsic = new() { Value = 30_000, StateReservoir = 183_600 };

        Assert.That(EthereumGasPolicy.TryCreateAvailableFromIntrinsic(30_000, in intrinsic, Amsterdam.Instance, out EthereumGasPolicy available), Is.False);

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
