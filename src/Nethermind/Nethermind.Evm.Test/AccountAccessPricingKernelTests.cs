// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class AccountAccessPricingKernelTests
{
    [TestCase(false, false, false, false, AccountAccessKind.Default, 2600UL, 100UL, false, 0UL,
        TestName = "Hot_cold_disabled_is_free")]
    [TestCase(true, true, true, false, AccountAccessKind.Default, 3000UL, 100UL, true, 3000UL,
        TestName = "Eip8038_cold_default_uses_cold_schedule")]
    [TestCase(true, true, false, false, AccountAccessKind.Default, 3000UL, 100UL, true, 100UL,
        TestName = "Eip8038_warm_default_uses_warm_schedule")]
    [TestCase(true, true, false, false, AccountAccessKind.Default, 3000UL, 17UL, true, 17UL,
        TestName = "Eip8038_warm_default_uses_supplied_non_100_schedule")]
    [TestCase(true, true, true, true, AccountAccessKind.Default, 3000UL, 100UL, true, 100UL,
        TestName = "Eip8038_cold_precompile_uses_warm_schedule")]
    [TestCase(true, false, true, false, AccountAccessKind.Default, 2600UL, 100UL, true, 2600UL,
        TestName = "Legacy_cold_default_uses_cold_schedule")]
    [TestCase(true, false, false, false, AccountAccessKind.Default, 2600UL, 100UL, true, 100UL,
        TestName = "Legacy_warm_default_uses_warm_schedule")]
    [TestCase(true, false, true, false, AccountAccessKind.SelfDestructBeneficiary, 2600UL, 100UL, true, 2600UL,
        TestName = "Legacy_cold_selfdestruct_uses_cold_schedule")]
    [TestCase(true, false, false, false, AccountAccessKind.SelfDestructBeneficiary, 2600UL, 100UL, false, 0UL,
        TestName = "Legacy_warm_selfdestruct_is_free")]
    [TestCase(true, false, true, true, AccountAccessKind.SelfDestructBeneficiary, 2600UL, 100UL, false, 0UL,
        TestName = "Legacy_precompile_selfdestruct_is_free")]
    [TestCase(true, true, true, false, AccountAccessKind.SelfDestructBeneficiary, 3000UL, 100UL, true, 3000UL,
        TestName = "Eip8038_cold_selfdestruct_uses_cold_schedule")]
    [TestCase(true, true, false, false, AccountAccessKind.SelfDestructBeneficiary, 3000UL, 100UL, true, 100UL,
        TestName = "Eip8038_warm_selfdestruct_uses_warm_schedule")]
    [TestCase(true, true, true, true, AccountAccessKind.SelfDestructBeneficiary, 3000UL, 100UL, true, 100UL,
        TestName = "Eip8038_precompile_selfdestruct_uses_warm_schedule")]
    public void Pure_kernel_returns_literal_boundary_decision_and_amount(
        bool hotAndColdEnabled,
        bool eip8038Enabled,
        bool isCold,
        bool isPrecompile,
        AccountAccessKind kind,
        ulong coldAccountAccessGas,
        ulong warmAccessGas,
        bool expectedCharge,
        ulong expectedAmount)
    {
        AccountAccessPricingResult result = AccountAccessPricingKernel.Price(
            hotAndColdEnabled,
            eip8038Enabled,
            isCold,
            isPrecompile,
            kind,
            coldAccountAccessGas,
            warmAccessGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Decision, Is.EqualTo(expectedCharge
                ? AccountAccessPricingDecision.Charge
                : AccountAccessPricingDecision.NoCharge));
            Assert.That(result.Amount, Is.EqualTo(expectedAmount));
        }
    }

    [TestCase(false, false, false, true, 0UL, true, 0UL,
        TestName = "Adapter_hot_cold_disabled_skips_tracing_and_access")]
    [TestCase(true, false, true, false, 99UL, false, 0UL,
        TestName = "Adapter_warm_default_rejects_one_short")]
    [TestCase(true, false, true, false, 100UL, true, 0UL,
        TestName = "Adapter_warm_default_accepts_exact_warm_cost")]
    [TestCase(true, false, false, false, 2599UL, false, 0UL,
        TestName = "Adapter_cold_default_rejects_one_short")]
    [TestCase(true, false, false, false, 2600UL, true, 0UL,
        TestName = "Adapter_cold_default_accepts_exact_legacy_cost")]
    [TestCase(true, true, false, false, 2999UL, false, 0UL,
        TestName = "Adapter_cold_default_rejects_one_short_eip8038")]
    [TestCase(true, true, false, false, 3000UL, true, 0UL,
        TestName = "Adapter_cold_default_accepts_exact_eip8038_cost")]
    [TestCase(true, true, false, true, 99UL, false, 0UL,
        TestName = "Adapter_tracing_warm_path_rejects_one_short")]
    [TestCase(true, true, false, true, 100UL, true, 0UL,
        TestName = "Adapter_tracing_warm_path_accepts_exact_cost")]
    public void Adapter_preserves_exact_boundaries_and_tracing_warmup(
        bool hotAndColdEnabled,
        bool eip8038Enabled,
        bool prewarm,
        bool isTracingAccess,
        ulong availableGas,
        bool expectedSuccess,
        ulong expectedRemainingGas)
    {
        IReleaseSpec spec = CreateSpec(hotAndColdEnabled, eip8038Enabled);
        using StackAccessTracker tracker = new();
        if (prewarm)
            tracker.WarmUp(TestItem.AddressC);

        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(availableGas);
        bool success = EthereumGasPolicy.TryConsumeAccountAccessGas(
            ref gas,
            spec,
            in tracker,
            isTracingAccess,
            TestItem.AddressC);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.EqualTo(expectedSuccess));
            Assert.That(EthereumGasPolicy.GetRemainingGas(in gas), Is.EqualTo(expectedRemainingGas));
            Assert.That(tracker.IsCold(TestItem.AddressC), Is.EqualTo(!hotAndColdEnabled));
        }
    }

    [TestCase(false, true, false, 100UL, true, 0UL,
        TestName = "Legacy_precompile_default_uses_warm_boundary")]
    [TestCase(false, true, true, 0UL, true, 0UL,
        TestName = "Legacy_precompile_selfdestruct_is_free")]
    [TestCase(true, true, true, 99UL, false, 0UL,
        TestName = "Eip8038_precompile_selfdestruct_rejects_one_short")]
    [TestCase(true, true, true, 100UL, true, 0UL,
        TestName = "Eip8038_precompile_selfdestruct_accepts_warm_boundary")]
    public void Adapter_preserves_precompile_and_selfdestruct_exceptions(
        bool eip8038,
        bool precompile,
        bool selfDestruct,
        ulong availableGas,
        bool expectedSuccess,
        ulong expectedRemainingGas)
    {
        IReleaseSpec spec = CreateSpec(hotAndCold: true, eip8038, includeIdentityPrecompile: precompile);
        using StackAccessTracker tracker = new();
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(availableGas);
        Address address = precompile ? IdentityPrecompile.Address : TestItem.AddressC;

        bool success = EthereumGasPolicy.TryConsumeAccountAccessGas(
            ref gas,
            spec,
            in tracker,
            isTracingAccess: false,
            address,
            selfDestruct ? AccountAccessKind.SelfDestructBeneficiary : AccountAccessKind.Default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.EqualTo(expectedSuccess));
            Assert.That(EthereumGasPolicy.GetRemainingGas(in gas), Is.EqualTo(expectedRemainingGas));
            Assert.That(tracker.IsCold(address), Is.False);
        }
    }

    private static IReleaseSpec CreateSpec(bool hotAndCold, bool eip8038, bool includeIdentityPrecompile = false)
    {
        IReleaseSpec spec = ReleaseSpecSubstitute.Create();
        spec.UseHotAndColdStorage.Returns(hotAndCold);
        spec.IsEip8038Enabled.Returns(eip8038);
        spec.Precompiles.Returns(includeIdentityPrecompile
            ? new AddressAsKey[] { IdentityPrecompile.Address }.ToFrozenSet()
            : FrozenSet<AddressAsKey>.Empty);
        return spec;
    }
}
