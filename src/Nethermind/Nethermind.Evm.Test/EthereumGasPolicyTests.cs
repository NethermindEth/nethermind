// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
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

    [Test]
    public void Memory_cost_rejects_full_width_length(
        [Values] bool maxOffset, [Values(0UL, ulong.MaxValue)] ulong availableGas)
    {
        EvmPooledMemory memory = new();
        memory.CalculateMemoryCost(UInt256.Zero, 64, out _);
        UInt256 position = maxOffset ? UInt256.MaxValue : UInt256.Zero;
        UInt256 length = new(0, 1, 0, 0);
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(availableGas);

        bool success = EthereumGasPolicy.UpdateMemoryCost(ref gas, in position, in length, ref memory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.False);
            Assert.That(memory.Size, Is.EqualTo(64));
            Assert.That(EthereumGasPolicy.GetRemainingGas(in gas), Is.EqualTo(availableGas));
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

    [TestCase(2999UL, false, TestName = "Specialized_eip8038_cold_account_access_rejects_one_short")]
    [TestCase(3000UL, true, TestName = "Specialized_eip8038_cold_account_access_accepts_exact_cost")]
    public void Specialized_eip8038_cold_account_access_honors_exact_boundaries(ulong availableGas, bool expectedSuccess)
    {
        IReleaseSpec spec = CreateAccessSpec(hotAndCold: true, eip8038: true);
        using StackAccessTracker tracker = new();
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(availableGas);

        spec.ClearReceivedCalls();
        bool success = EthereumGasPolicy.TryConsumeAccountAccessGas<OnFlag, OnFlag>(
            ref gas, spec, in tracker, isTracingAccess: false, TestItem.AddressC);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.EqualTo(expectedSuccess));
            Assert.That(EthereumGasPolicy.GetRemainingGas(in gas), Is.Zero);
            Assert.That(tracker.IsCold(TestItem.AddressC), Is.False);
        }
        _ = spec.DidNotReceive().IsEip8038Enabled;
        _ = spec.DidNotReceive().UseHotAndColdStorage;
    }

    [TestCase(false, true, 0UL, true, 0UL, TestName = "Pre_Eip8038_warm_selfdestruct_beneficiary_has_no_access_surcharge")]
    [TestCase(false, false, 2600UL, true, 0UL, TestName = "Pre_Eip8038_cold_selfdestruct_beneficiary_charges_cold_access")]
    [TestCase(true, true, 99UL, false, 0UL, TestName = "Eip8038_warm_selfdestruct_beneficiary_rejects_one_short")]
    [TestCase(true, true, 100UL, true, 0UL, TestName = "Eip8038_warm_selfdestruct_beneficiary_charges_warm_access")]
    [TestCase(true, false, 2999UL, false, 0UL, TestName = "Eip8038_cold_selfdestruct_beneficiary_rejects_one_short")]
    [TestCase(true, false, 3000UL, true, 0UL, TestName = "Eip8038_cold_selfdestruct_beneficiary_charges_cold_access")]
    public void Selfdestruct_beneficiary_access_follows_eip8038(
        bool eip8038,
        bool prewarm,
        ulong availableGas,
        bool expectedSuccess,
        ulong expectedRemainingGas)
    {
        IReleaseSpec spec = CreateAccessSpec(hotAndCold: true, eip8038);
        using StackAccessTracker tracker = new();
        if (prewarm) tracker.WarmUp(TestItem.AddressC);
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(availableGas);

        bool success = EthereumGasPolicy.TryConsumeAccountAccessGas(
            ref gas,
            spec,
            in tracker,
            isTracingAccess: false,
            TestItem.AddressC,
            AccountAccessKind.SelfDestructBeneficiary);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.EqualTo(expectedSuccess));
            Assert.That(EthereumGasPolicy.GetRemainingGas(in gas), Is.EqualTo(expectedRemainingGas));
            Assert.That(tracker.IsCold(TestItem.AddressC), Is.False);
        }
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
        IReleaseSpec spec = ReleaseSpecSubstitute.Create();
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
        bool expected = ChargeCreateDynamic(ref dynamicGas, spec, eip8037, create2, words);
        spec.ClearReceivedCalls();
        bool actual = (eip3860, eip8038) switch
        {
            (true, true) => ChargeCreate<EvmInstructions.CreateSpec<OffFlag, OffFlag, OnFlag, Eip8038On>>(ref specializedGas, spec, eip8037, create2, words),
            (true, false) => ChargeCreate<EvmInstructions.CreateSpec<OffFlag, OffFlag, OnFlag, Eip8038Off>>(ref specializedGas, spec, eip8037, create2, words),
            (false, true) => ChargeCreate<EvmInstructions.CreateSpec<OffFlag, OffFlag, OffFlag, Eip8038On>>(ref specializedGas, spec, eip8037, create2, words),
            (false, false) => ChargeCreate<EvmInstructions.CreateSpec<OffFlag, OffFlag, OffFlag, Eip8038Off>>(ref specializedGas, spec, eip8037, create2, words),
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

    /// <summary>The policy's own spec-reading create charge, the oracle the specialized specs must match.</summary>
    private static bool ChargeCreateDynamic<TGasPolicy>(ref TGasPolicy gas, IReleaseSpec spec, bool eip8037, bool create2, ulong words)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
        (eip8037, create2) switch
        {
            (true, true) => TGasPolicy.TryConsumeCreateGas<OnFlag, EvmInstructions.OpCreate2>(ref gas, spec, words),
            (true, false) => TGasPolicy.TryConsumeCreateGas<OnFlag, EvmInstructions.OpCreate>(ref gas, spec, words),
            (false, true) => TGasPolicy.TryConsumeCreateGas<OffFlag, EvmInstructions.OpCreate2>(ref gas, spec, words),
            (false, false) => TGasPolicy.TryConsumeCreateGas<OffFlag, EvmInstructions.OpCreate>(ref gas, spec, words),
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

    [TestCase(18UL, 15UL, 3UL, 0, 0UL, 18UL, TestName = "Precompile_pricing_accepts_exact_total")]
    [TestCase(17UL, 15UL, 3UL, 2, 0UL, 0UL, TestName = "Precompile_pricing_rejects_one_short")]
    [TestCase(73UL, ulong.MaxValue, 1UL, 1, 73UL, 0UL, TestName = "Precompile_pricing_preserves_gas_on_base_data_overflow")]
    [TestCase(ulong.MaxValue, 0UL, ulong.MaxValue, 0, 0UL, ulong.MaxValue, TestName = "Precompile_pricing_accepts_maximum_data_cost")]
    [TestCase(ulong.MaxValue, ulong.MaxValue, 0UL, 0, 0UL, ulong.MaxValue, TestName = "Precompile_pricing_accepts_maximum_base_cost")]
    public void Precompile_pricing_charges_execution_gas_without_mutating_state_gas(
        ulong availableGas,
        ulong baseGasCost,
        ulong dataGasCost,
        byte expectedOutcome,
        ulong expectedRemainingGas,
        ulong expectedChargedGas)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        IPrecompile precompile = Substitute.For<IPrecompile>();
        ReadOnlyMemory<byte> inputData = new byte[] { 0x01 };
        precompile.BaseGasCost(spec).Returns(baseGasCost);
        precompile.DataGasCost(inputData, spec).Returns(dataGasCost);
        precompile.ClearReceivedCalls();
        EthereumGasPolicy gas = new()
        {
            Value = availableGas,
            StateReservoir = 41,
            StateGasUsed = 17,
            StateGasSpill = 29,
            StateGasSpillRefunded = 11,
        };
        StateGasSnapshot stateBefore = ToStateGasSnapshot(in gas);

        PrecompileGasPricingResult pricing = PrecompileGasPricingKernel.TryConsume(
            availableGas,
            baseGasCost,
            dataGasCost);
        PrecompileGasPricingOutcome outcome = (PrecompileGasPricingOutcome)expectedOutcome;
        bool success = EthereumGasPolicy.TryConsumePrecompileGas(ref gas, precompile, inputData, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pricing.Outcome, Is.EqualTo(outcome));
            Assert.That(pricing.RemainingGas, Is.EqualTo(expectedRemainingGas));
            Assert.That(pricing.ChargedGas, Is.EqualTo(expectedChargedGas));
            Assert.That(success, Is.EqualTo(outcome is PrecompileGasPricingOutcome.Success));
            Assert.That(gas.Value, Is.EqualTo(expectedRemainingGas));
            Assert.That(
                ToStateGasSnapshot(in gas),
                Is.EqualTo(stateBefore with { Value = expectedRemainingGas }));
        }
        Received.InOrder(() =>
        {
            precompile.BaseGasCost(spec);
            precompile.DataGasCost(inputData, spec);
        });
        _ = precompile.Received(1).BaseGasCost(spec);
        _ = precompile.Received(1).DataGasCost(inputData, spec);
    }

    [TestCase(18UL, 15UL, 3UL, true, 0UL, TestName = "Precompile_generic_dispatch_charges_exact_total")]
    [TestCase(17UL, 15UL, 3UL, false, 0UL, TestName = "Precompile_generic_dispatch_clears_one_short")]
    [TestCase(73UL, ulong.MaxValue, 1UL, false, 73UL, TestName = "Precompile_generic_dispatch_preserves_overflow")]
    public void Full_frame_local_copy_and_inline_by_ref_dispatch_to_standard_precompile_pricing(
        ulong availableGas,
        ulong baseGasCost,
        ulong dataGasCost,
        bool expectedSuccess,
        ulong expectedRemainingGas)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        ReadOnlyMemory<byte> inputData = new byte[] { 0x02 };
        EthereumGasPolicy initial = new()
        {
            Value = availableGas,
            StateReservoir = 31,
            StateGasUsed = 19,
            StateGasSpill = 23,
            StateGasSpillRefunded = 7,
        };
        IPrecompile fullFramePrecompile = CreatePrecompile(spec, baseGasCost, dataGasCost);
        IPrecompile inlinePrecompile = CreatePrecompile(spec, baseGasCost, dataGasCost);

        bool fullFrameSuccess = TryConsumePrecompileWithFullFrameLocalCopy(
            initial,
            fullFramePrecompile,
            inputData,
            spec,
            out EthereumGasPolicy fullFrameGas);
        EthereumGasPolicy inlineGas = initial;
        bool inlineSuccess = TryConsumePrecompileWithInlineByRef(
            ref inlineGas,
            inlinePrecompile,
            inputData,
            spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fullFrameSuccess, Is.EqualTo(expectedSuccess));
            Assert.That(inlineSuccess, Is.EqualTo(expectedSuccess));
            Assert.That(
                ToStateGasSnapshot(in fullFrameGas),
                Is.EqualTo(ToStateGasSnapshot(in initial) with { Value = expectedRemainingGas }));
            Assert.That(
                ToStateGasSnapshot(in inlineGas),
                Is.EqualTo(ToStateGasSnapshot(in initial) with { Value = expectedRemainingGas }));
        }
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

    [TestCaseSource(nameof(StateGasChargeCases))]
    public void TryConsumeStateGas_delegates_to_value_kernel(
        StateGasSnapshot initial,
        long stateGasCost,
        bool expectedSuccess,
        StateGasSnapshot expected)
    {
        StateGasChargeResult result = StateGasChargeKernel.TryCharge(
            initial.Value,
            initial.StateReservoir,
            initial.StateGasUsed,
            initial.StateGasSpill,
            initial.StateGasSpillRefunded,
            stateGasCost);
        EthereumGasPolicy gas = new()
        {
            Value = initial.Value,
            StateReservoir = initial.StateReservoir,
            StateGasUsed = initial.StateGasUsed,
            StateGasSpill = initial.StateGasSpill,
            StateGasSpillRefunded = initial.StateGasSpillRefunded,
        };

        bool success = EthereumGasPolicy.TryConsumeStateGas(ref gas, stateGasCost);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome,
                Is.EqualTo(expectedSuccess ? StateGasChargeOutcome.Success : StateGasChargeOutcome.OutOfGas));
            Assert.That(success, Is.EqualTo(expectedSuccess));
            Assert.That(ToStateGasSnapshot(in result), Is.EqualTo(expected));
            Assert.That(ToStateGasSnapshot(in gas), Is.EqualTo(expected));
        }
    }

    [TestCaseSource(nameof(StateGasTransitionCases))]
    public void State_gas_transition_operations_delegate_to_value_kernel(StateGasTransitionTestCase testCase)
    {
        StateGasSnapshot initial = testCase.Initial;
        StateGasSnapshot child = testCase.Child;
        EthereumGasPolicy gas = ToEthereumGasPolicy(in initial);
        EthereumGasPolicy childGas = ToEthereumGasPolicy(in child);
        StateGasTransitionAdapterOutcome outcome;
        long unappliedAmount = 0;

        switch (testCase.Operation)
        {
            case StateGasTransitionOperation.Refund:
                outcome = StateGasTransitionAdapterKernel.Refund(
                    gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill, gas.StateGasSpillRefunded,
                    childGas.Value, childGas.StateReservoir, childGas.StateGasUsed, childGas.StateGasSpill, childGas.StateGasSpillRefunded);
                EthereumGasPolicy.Refund(ref gas, in childGas);
                break;
            case StateGasTransitionOperation.RepayStateGasSpill:
                outcome = StateGasTransitionAdapterKernel.RepayStateGasSpill(
                    gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill, gas.StateGasSpillRefunded);
                EthereumGasPolicy.RepayStateGasSpill(ref gas);
                break;
            case StateGasTransitionOperation.RestoreChildStateGas:
                outcome = StateGasTransitionAdapterKernel.RestoreChildStateGas(
                    gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill, gas.StateGasSpillRefunded,
                    childGas.StateReservoir, childGas.StateGasUsed, childGas.StateGasSpill, childGas.StateGasSpillRefunded);
                EthereumGasPolicy.RestoreChildStateGas(ref gas, in childGas);
                break;
            case StateGasTransitionOperation.RestoreChildStateGasOnHalt:
                outcome = StateGasTransitionAdapterKernel.RestoreChildStateGasOnHalt(
                    gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill, gas.StateGasSpillRefunded,
                    childGas.StateReservoir, childGas.StateGasUsed, childGas.StateGasSpill, childGas.StateGasSpillRefunded);
                EthereumGasPolicy.RestoreChildStateGasOnHalt(ref gas, in childGas);
                break;
            case StateGasTransitionOperation.RevertRefundToHalt:
                outcome = StateGasTransitionAdapterKernel.RevertRefundToHalt(
                    gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill, gas.StateGasSpillRefunded,
                    childGas.StateGasUsed, childGas.StateGasSpill, childGas.StateGasSpillRefunded);
                EthereumGasPolicy.RevertRefundToHalt(ref gas, in childGas);
                break;
            case StateGasTransitionOperation.RefundStateGas:
                outcome = StateGasTransitionAdapterKernel.RefundStateGas(
                    gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill, gas.StateGasSpillRefunded,
                    testCase.Amount, testCase.StateGasFloor, testCase.TrackSpillRefund);
                EthereumGasPolicy.RefundStateGas(ref gas, testCase.Amount, testCase.StateGasFloor, testCase.TrackSpillRefund);
                break;
            case StateGasTransitionOperation.DiscardStateGas:
                outcome = StateGasTransitionAdapterKernel.DiscardStateGas(
                    gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill, gas.StateGasSpillRefunded,
                    testCase.Amount, testCase.StateGasFloor);
                unappliedAmount = EthereumGasPolicy.DiscardStateGas(ref gas, testCase.Amount, testCase.StateGasFloor);
                break;
            case StateGasTransitionOperation.AddStateGasRefundToReservoir:
                outcome = StateGasTransitionAdapterKernel.AddStateGasRefundToReservoir(
                    gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill, gas.StateGasSpillRefunded,
                    testCase.Amount, testCase.TrackSpillRefund);
                EthereumGasPolicy.AddStateGasRefundToReservoir(ref gas, testCase.Amount, testCase.TrackSpillRefund);
                break;
            case StateGasTransitionOperation.RemoveStateGasRefundFromReservoir:
                outcome = StateGasTransitionAdapterKernel.RemoveStateGasRefundFromReservoir(
                    gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill, gas.StateGasSpillRefunded,
                    testCase.Amount);
                EthereumGasPolicy.RemoveStateGasRefundFromReservoir(ref gas, testCase.Amount);
                break;
            default:
            throw new ArgumentOutOfRangeException(nameof(testCase));
        }

        StateGasTransitionAdapterOutcomeKind expectedOutcome =
            testCase.Operation is StateGasTransitionOperation.DiscardStateGas
                ? StateGasTransitionAdapterOutcomeKind.CompletedDiscard
                : StateGasTransitionAdapterOutcomeKind.CompletedVoid;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Kind, Is.EqualTo(expectedOutcome));
            Assert.That(ToStateGasSnapshot(in outcome.Transition), Is.EqualTo(testCase.Expected));
            Assert.That(ToStateGasSnapshot(in gas), Is.EqualTo(testCase.Expected));
            Assert.That(outcome.Transition.UnappliedAmount, Is.EqualTo(testCase.ExpectedUnappliedAmount));
            Assert.That(unappliedAmount, Is.EqualTo(testCase.ExpectedUnappliedAmount));
        }
    }

    [TestCase(-1L)]
    [TestCase(long.MinValue)]
    public void RemoveStateGasRefundFromReservoir_preserves_invalid_amount_exception(long amount)
    {
        EthereumGasPolicy gas = new()
        {
            Value = 17,
            StateReservoir = 1,
            StateGasUsed = 2,
            StateGasSpill = 3,
            StateGasSpillRefunded = 4,
        };
        StateGasSnapshot before = ToStateGasSnapshot(in gas);
        StateGasTransitionAdapterOutcome outcome = StateGasTransitionAdapterKernel.RemoveStateGasRefundFromReservoir(
            gas.Value,
            gas.StateReservoir,
            gas.StateGasUsed,
            gas.StateGasSpill,
            gas.StateGasSpillRefunded,
            amount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Kind, Is.EqualTo(StateGasTransitionAdapterOutcomeKind.ArgumentException));
            Assert.That(ToStateGasSnapshot(in outcome.Transition), Is.EqualTo(before));
            Assert.That(
                () => EthereumGasPolicy.RemoveStateGasRefundFromReservoir(ref gas, amount),
                Throws.ArgumentException);
            Assert.That(ToStateGasSnapshot(in gas), Is.EqualTo(before));
        }
    }

    [TestCase(29_999UL, false, 0UL)]
    [TestCase(30_000UL, false, 0UL)]
    [TestCase(213_599UL, false, 0UL)]
    [TestCase(213_600UL, true, 0UL)]
    [TestCase(213_601UL, true, 1UL)]
    public void CreateAvailableFromIntrinsic_checks_execution_and_state_gas(ulong gasLimit, bool expectedSuccess, ulong expectedRemaining)
    {
        EthereumGasPolicy intrinsic = new() { Value = 30_000, StateReservoir = 183_600 };

        bool success = EthereumGasPolicy.TryCreateAvailableFromIntrinsic(gasLimit, in intrinsic, Amsterdam.Instance, out EthereumGasPolicy available);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.EqualTo(expectedSuccess));
            Assert.That(EthereumGasPolicy.GetRemainingGas(in available), Is.EqualTo(expectedRemaining));
            Assert.That(EthereumGasPolicy.GetStateReservoir(in available), Is.Zero);
            Assert.That(EthereumGasPolicy.GetStateGasUsed(in available), Is.EqualTo(expectedSuccess ? intrinsic.StateReservoir : 0));
        }
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

    [Test, Combinatorial]
    public void Specialized_sload_base_matches_price_book(
        [Values] bool hotAndCold,
        [Values(0UL, 99UL, 800UL, 10000UL)] ulong availableGas)
    {
        IReleaseSpec spec = CreateAccessSpec(hotAndCold, false);
        EthereumGasPolicy expectedGas = EthereumGasPolicy.FromULong(availableGas);
        EthereumGasPolicy actualGas = expectedGas;
        bool expected = EthereumGasPolicy.UpdateGas(ref expectedGas, spec.GasCosts.SLoadCost);
        spec.ClearReceivedCalls();

        bool actual = hotAndCold
            ? EthereumGasPolicy.TryConsumeSLoadBaseGas<OnFlag>(ref actualGas, spec)
            : EthereumGasPolicy.TryConsumeSLoadBaseGas<OffFlag>(ref actualGas, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            AssertGasMatches(in actualGas, in expectedGas);
            if (hotAndCold) Assert.That(spec.ReceivedCalls(), Is.Empty);
        }
    }

    [Test, Combinatorial]
    public void Specialized_exp_price_matches_price_book(
        [Values] bool eip160,
        [Values(0UL, 1UL, 32UL)] ulong exponentBytes,
        [Values(0UL, 9UL, 10UL, 49UL, 50UL, 1599UL, 1600UL)] ulong availableGas)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.UseExpDDosProtection.Returns(eip160);
        SpecGasCosts gasCosts = new(spec);
        spec.GasCosts.Returns(gasCosts);
        EthereumGasPolicy expectedGas = EthereumGasPolicy.FromULong(availableGas);
        EthereumGasPolicy actualGas = expectedGas;
        bool expected = EthereumGasPolicy.UpdateGas(ref expectedGas, spec.GasCosts.ExpByteCost * exponentBytes);
        spec.ClearReceivedCalls();

        bool actual = eip160
            ? EthereumGasPolicy.TryConsumeExpBytes<OnFlag>(ref actualGas, spec, exponentBytes)
            : EthereumGasPolicy.TryConsumeExpBytes<OffFlag>(ref actualGas, spec, exponentBytes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            AssertGasMatches(in actualGas, in expectedGas);
            Assert.That(spec.ReceivedCalls(), Is.Empty);
        }
    }

    private static ulong GetPreRefundGas<TGasPolicy>(in TGasPolicy gas, ulong gasLimit)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        => TGasPolicy.GetPreRefundGas(in gas, gasLimit);

    private static IPrecompile CreatePrecompile(IReleaseSpec spec, ulong baseGasCost, ulong dataGasCost)
    {
        IPrecompile precompile = Substitute.For<IPrecompile>();
        precompile.BaseGasCost(spec).Returns(baseGasCost);
        precompile.DataGasCost(Arg.Any<ReadOnlyMemory<byte>>(), spec).Returns(dataGasCost);
        return precompile;
    }

    private static bool TryConsumePrecompileWithFullFrameLocalCopy<TGasPolicy>(
        TGasPolicy initial,
        IPrecompile precompile,
        ReadOnlyMemory<byte> inputData,
        IReleaseSpec spec,
        out TGasPolicy result)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        result = initial;
        return TGasPolicy.TryConsumePrecompileGas(ref result, precompile, inputData, spec);
    }

    private static bool TryConsumePrecompileWithInlineByRef<TGasPolicy>(
        ref TGasPolicy gas,
        IPrecompile precompile,
        ReadOnlyMemory<byte> inputData,
        IReleaseSpec spec)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
        TGasPolicy.TryConsumePrecompileGas(ref gas, precompile, inputData, spec);

    public readonly record struct StateGasSnapshot(
        ulong Value,
        long StateReservoir,
        long StateGasUsed,
        long StateGasSpill,
        long StateGasSpillRefunded);

    private static TestCaseData[] StateGasChargeCases =>
    [
        StateGasChargeCase("zero", new StateGasSnapshot(7, 3, 5, 11, 2), 0, true,
            new StateGasSnapshot(7, 3, 5, 11, 2)),
        StateGasChargeCase("exact_reservoir", new StateGasSnapshot(7, 10, 5, 11, 2), 10, true,
            new StateGasSnapshot(7, 0, 15, 11, 2)),
        StateGasChargeCase("partial_spill", new StateGasSnapshot(100, 10, 5, 11, 2), 25, true,
            new StateGasSnapshot(85, 0, 30, 26, 2)),
        StateGasChargeCase("exact_execution_gas", new StateGasSnapshot(15, 10, 5, 11, 2), 25, true,
            new StateGasSnapshot(0, 0, 30, 26, 2)),
        StateGasChargeCase("one_short_out_of_gas", new StateGasSnapshot(14, 10, 5, 11, 2), 25, false,
            new StateGasSnapshot(14, 10, 5, 11, 2)),
        StateGasChargeCase("negative_reservoir", new StateGasSnapshot(25, -3, 5, 11, 2), 10, true,
            new StateGasSnapshot(15, -3, 15, 21, 2)),
        StateGasChargeCase("maximum_reservoir", new StateGasSnapshot(ulong.MaxValue, long.MaxValue, 0, long.MinValue, long.MaxValue), long.MaxValue, true,
            new StateGasSnapshot(ulong.MaxValue, 0, long.MaxValue, long.MinValue, long.MaxValue)),
        StateGasChargeCase("maximum_spill", new StateGasSnapshot(ulong.MaxValue, 0, 0, 0, long.MinValue), long.MaxValue, true,
            new StateGasSnapshot(ulong.MaxValue - (ulong)long.MaxValue, 0, long.MaxValue, long.MaxValue, long.MinValue)),
        StateGasChargeCase("minimum_reservoir", new StateGasSnapshot(1, long.MinValue, 9, -11, 13), 1, true,
            new StateGasSnapshot(0, long.MinValue, 10, -10, 13)),
        StateGasChargeCase("negative_cost", new StateGasSnapshot(7, 3, 5, 11, 2), -2, true,
            new StateGasSnapshot(7, 5, 3, 11, 2)),
    ];

    public enum StateGasTransitionOperation : byte
    {
        Refund,
        RepayStateGasSpill,
        RestoreChildStateGas,
        RestoreChildStateGasOnHalt,
        RevertRefundToHalt,
        RefundStateGas,
        DiscardStateGas,
        AddStateGasRefundToReservoir,
        RemoveStateGasRefundFromReservoir,
    }

    public readonly record struct StateGasTransitionTestCase(
        StateGasTransitionOperation Operation,
        StateGasSnapshot Initial,
        StateGasSnapshot Child,
        long Amount,
        long StateGasFloor,
        bool TrackSpillRefund,
        StateGasSnapshot Expected,
        long ExpectedUnappliedAmount);

    private static TestCaseData[] StateGasTransitionCases =>
    [
        StateGasTransitionCase(
            "success_merge",
            StateGasTransitionOperation.Refund,
            new StateGasSnapshot(10, 20, 30, 40, 50),
            new StateGasSnapshot(15, 25, 35, 45, 55),
            0,
            0,
            false,
            new StateGasSnapshot(25, 45, 65, 85, 105)),
        StateGasTransitionCase(
            "success_merge_wraps_all_dimensions",
            StateGasTransitionOperation.Refund,
            new StateGasSnapshot(ulong.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue),
            new StateGasSnapshot(1, 1, 1, 1, 1),
            0,
            0,
            false,
            new StateGasSnapshot(0, long.MinValue, long.MinValue, long.MinValue, long.MinValue)),
        StateGasTransitionCase(
            "success_merge_repays_outstanding_spill",
            StateGasTransitionOperation.RepayStateGasSpill,
            new StateGasSnapshot(100, 8, 9, 10, 3),
            default,
            0,
            0,
            false,
            new StateGasSnapshot(107, 1, 9, 10, 10)),
        StateGasTransitionCase(
            "repayment_preserves_machine_width_wrap",
            StateGasTransitionOperation.RepayStateGasSpill,
            new StateGasSnapshot(0, long.MaxValue, 7, long.MinValue, 1),
            default,
            0,
            0,
            false,
            new StateGasSnapshot((ulong)long.MaxValue, 0, 7, long.MinValue, long.MinValue)),
        StateGasTransitionCase(
            "revert_restores_net_spill_to_execution_gas",
            StateGasTransitionOperation.RestoreChildStateGas,
            new StateGasSnapshot(100, 2, 3, 4, 5),
            new StateGasSnapshot(30, 7, 11, 17, 6),
            0,
            0,
            false,
            new StateGasSnapshot(111, 9, 3, 4, 5)),
        StateGasTransitionCase(
            "revert_restoration_preserves_machine_width_wrap",
            StateGasTransitionOperation.RestoreChildStateGas,
            new StateGasSnapshot(ulong.MaxValue, long.MaxValue, 6, 7, 8),
            new StateGasSnapshot(0, 1, 1, 1, 0),
            0,
            0,
            false,
            new StateGasSnapshot(0, long.MinValue, 6, 7, 8)),
        StateGasTransitionCase(
            "exceptional_halt_burns_child_net_spill",
            StateGasTransitionOperation.RestoreChildStateGasOnHalt,
            new StateGasSnapshot(100, 2, 3, 4, 5),
            new StateGasSnapshot(30, 7, 11, 17, 6),
            0,
            0,
            false,
            new StateGasSnapshot(100, 9, 3, 4, 5)),
        StateGasTransitionCase(
            "halt_restoration_preserves_machine_width_wrap",
            StateGasTransitionOperation.RestoreChildStateGasOnHalt,
            new StateGasSnapshot(ulong.MaxValue, long.MaxValue, 6, 7, 8),
            new StateGasSnapshot(0, 1, 1, 1, 0),
            0,
            0,
            false,
            new StateGasSnapshot(ulong.MaxValue, long.MinValue, 6, 7, 8)),
        StateGasTransitionCase(
            "merged_create_halt_removes_child_accounting",
            StateGasTransitionOperation.RevertRefundToHalt,
            new StateGasSnapshot(100, 20, 30, 40, 50),
            new StateGasSnapshot(70, 5, 7, 11, 3),
            0,
            0,
            false,
            new StateGasSnapshot(100, 19, 23, 29, 47)),
        StateGasTransitionCase(
            "merged_create_halt_preserves_signed_wrap",
            StateGasTransitionOperation.RevertRefundToHalt,
            new StateGasSnapshot(5, long.MinValue, long.MinValue, long.MinValue, long.MinValue),
            new StateGasSnapshot(0, 0, long.MaxValue, 1, 0),
            0,
            0,
            false,
            new StateGasSnapshot(5, -2, 1, long.MaxValue, long.MinValue)),
        StateGasTransitionCase(
            "refund_state_gas_refills_spill_before_reservoir",
            StateGasTransitionOperation.RefundStateGas,
            new StateGasSnapshot(100, 0, 200, 100, 20),
            default,
            150,
            75,
            true,
            new StateGasSnapshot(180, 45, 75, 100, 100)),
        StateGasTransitionCase(
            "untracked_state_refund_only_refills_reservoir",
            StateGasTransitionOperation.RefundStateGas,
            new StateGasSnapshot(100, 0, 200, 100, 20),
            default,
            150,
            75,
            false,
            new StateGasSnapshot(100, 125, 75, 100, 20)),
        StateGasTransitionCase(
            "state_refund_handles_maximum_signed_amount",
            StateGasTransitionOperation.RefundStateGas,
            new StateGasSnapshot(0, 0, long.MaxValue, 0, 0),
            default,
            long.MaxValue,
            0,
            false,
            new StateGasSnapshot(0, long.MaxValue, 0, 0, 0)),
        StateGasTransitionCase(
            "state_refund_preserves_signed_wrap",
            StateGasTransitionOperation.RefundStateGas,
            new StateGasSnapshot(ulong.MaxValue, long.MaxValue, long.MinValue, 1, 0),
            default,
            1,
            long.MaxValue,
            false,
            new StateGasSnapshot(ulong.MaxValue, long.MinValue, long.MaxValue, 1, 0)),
        StateGasTransitionCase(
            "discard_state_gas_stops_at_frame_floor",
            StateGasTransitionOperation.DiscardStateGas,
            new StateGasSnapshot(100, 9, 80, 30, 4),
            default,
            50,
            40,
            false,
            new StateGasSnapshot(100, 9, 40, 30, 4),
            expectedUnappliedAmount: 10),
        StateGasTransitionCase(
            "discard_state_gas_uses_unchecked_floor_subtraction",
            StateGasTransitionOperation.DiscardStateGas,
            new StateGasSnapshot(100, 9, long.MaxValue, 30, 4),
            default,
            5,
            -1,
            false,
            new StateGasSnapshot(100, 9, long.MaxValue, 30, 4),
            expectedUnappliedAmount: 5),
        StateGasTransitionCase(
            "advanced_refund_refills_spill_before_reservoir",
            StateGasTransitionOperation.AddStateGasRefundToReservoir,
            new StateGasSnapshot(100, 5, 80, 20, 8),
            default,
            15,
            0,
            true,
            new StateGasSnapshot(112, 8, 80, 20, 20)),
        StateGasTransitionCase(
            "untracked_advanced_refund_refills_reservoir",
            StateGasTransitionOperation.AddStateGasRefundToReservoir,
            new StateGasSnapshot(100, 5, 80, 20, 8),
            default,
            15,
            0,
            false,
            new StateGasSnapshot(100, 20, 80, 20, 8)),
        StateGasTransitionCase(
            "advanced_refund_preserves_signed_spill_wrap",
            StateGasTransitionOperation.AddStateGasRefundToReservoir,
            new StateGasSnapshot(0, 0, 0, long.MinValue, 1),
            default,
            long.MaxValue,
            0,
            true,
            new StateGasSnapshot((ulong)long.MaxValue, 0, 0, long.MinValue, long.MinValue)),
        StateGasTransitionCase(
            "remove_advanced_refund_uses_reservoir_before_usage",
            StateGasTransitionOperation.RemoveStateGasRefundFromReservoir,
            new StateGasSnapshot(100, 50, 70, 20, 8),
            default,
            60,
            0,
            false,
            new StateGasSnapshot(100, 0, 60, 20, 8)),
        StateGasTransitionCase(
            "remove_advanced_refund_restores_reservoir_debt",
            StateGasTransitionOperation.RemoveStateGasRefundFromReservoir,
            new StateGasSnapshot(100, -10, 5, 20, 8),
            default,
            20,
            0,
            false,
            new StateGasSnapshot(100, -25, 0, 20, 8)),
        StateGasTransitionCase(
            "remove_advanced_refund_preserves_signed_wrap",
            StateGasTransitionOperation.RemoveStateGasRefundFromReservoir,
            new StateGasSnapshot(100, long.MinValue, long.MinValue, 20, 8),
            default,
            long.MaxValue,
            0,
            false,
            new StateGasSnapshot(100, long.MinValue + 1, 0, 20, 8)),
    ];

    private static TestCaseData StateGasChargeCase(
        string name,
        StateGasSnapshot initial,
        long stateGasCost,
        bool expectedSuccess,
        StateGasSnapshot expected) =>
        new TestCaseData(initial, stateGasCost, expectedSuccess, expected).SetName(name);

    private static TestCaseData StateGasTransitionCase(
        string name,
        StateGasTransitionOperation operation,
        StateGasSnapshot initial,
        StateGasSnapshot child,
        long amount,
        long stateGasFloor,
        bool trackSpillRefund,
        StateGasSnapshot expected,
        long expectedUnappliedAmount = 0) =>
        new TestCaseData(new StateGasTransitionTestCase(
            operation,
            initial,
            child,
            amount,
            stateGasFloor,
            trackSpillRefund,
            expected,
            expectedUnappliedAmount)).SetName(name);

    private static EthereumGasPolicy ToEthereumGasPolicy(in StateGasSnapshot snapshot) =>
        new()
        {
            Value = snapshot.Value,
            StateReservoir = snapshot.StateReservoir,
            StateGasUsed = snapshot.StateGasUsed,
            StateGasSpill = snapshot.StateGasSpill,
            StateGasSpillRefunded = snapshot.StateGasSpillRefunded,
        };

    private static StateGasSnapshot ToStateGasSnapshot(in EthereumGasPolicy gas) =>
        new(gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill, gas.StateGasSpillRefunded);

    private static StateGasSnapshot ToStateGasSnapshot(in StateGasChargeResult result) =>
        new(result.Value, result.StateReservoir, result.StateGasUsed, result.StateGasSpill, result.StateGasSpillRefunded);

    private static StateGasSnapshot ToStateGasSnapshot(in StateGasTransitionResult result) =>
        new(result.Value, result.StateReservoir, result.StateGasUsed, result.StateGasSpill, result.StateGasSpillRefunded);
}
