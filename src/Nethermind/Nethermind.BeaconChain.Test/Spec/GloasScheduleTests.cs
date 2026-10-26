// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using NSubstitute;

namespace Nethermind.BeaconChain.Test.Spec;

public class GloasScheduleTests
{
    [Test]
    public void Consensus_slot_timing_must_match_the_compiled_preset([Values(-1, 0, 1)] int slotsOffset, [Values(-1, 0, 1)] int secondsOffset)
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet with
        {
            SlotsPerEpoch = (ulong)((long)Presets.SlotsPerEpoch + slotsOffset),
            SecondsPerSlot = (ulong)((long)Presets.SecondsPerSlot + secondsOffset),
        };
        if (slotsOffset == 0 && secondsOffset == 0)
            Assert.That(() => BeaconChainModule.ValidatePreset(spec), Throws.Nothing);
        else
            AssertConflictingConfiguration(() => BeaconChainModule.ValidatePreset(spec));
    }

    private static readonly ISpecProvider[] ExecutionNetworks =
        [MainnetSpecProvider.Instance, HoodiSpecProvider.Instance, SepoliaSpecProvider.Instance, GnosisSpecProvider.Instance, ChiadoSpecProvider.Instance];

    [Test]
    public void Shipped_consensus_and_execution_schedules_agree([ValueSource(nameof(ExecutionNetworks))] ISpecProvider execution)
    {
        BeaconChainSpec spec = BeaconChainSpec.ForChainId(execution.ChainId);
        Assert.That(() => BeaconChainModule.ValidateGloasSchedule(spec, execution), Throws.Nothing);
        if (spec.GloasForkEpoch == Presets.FarFutureEpoch) return;

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(spec.ForkAtEpoch(spec.GloasForkEpoch - 1), Is.EqualTo(BeaconFork.Fulu));
        Assert.That(spec.ForkAtEpoch(spec.GloasForkEpoch), Is.EqualTo(BeaconFork.Gloas));
        Assert.That(spec.VersionForEpoch(spec.GloasForkEpoch), Is.EqualTo(spec.GloasForkVersion));
        Assert.That(spec.VersionForEpoch(spec.GloasForkEpoch - 1), Is.Not.EqualTo(spec.GloasForkVersion));
    }

    [Test]
    public void Gloas_requires_the_same_execution_activation([Values(-1, 0, 1)] int offset)
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet with { GloasForkEpoch = 500_000 };
        ulong activation = spec.GenesisTime + spec.GloasForkEpoch * spec.SlotsPerEpoch * spec.SecondsPerSlot;
        ISpecProvider execution = Substitute.For<ISpecProvider>();
        IReleaseSpec before = Substitute.For<IReleaseSpec>();
        IReleaseSpec after = Substitute.For<IReleaseSpec>();
        after.BlockLevelAccessListsEnabled.Returns(true);
        execution.GetSpec(Arg.Any<ForkActivation>()).Returns(call =>
            call.Arg<ForkActivation>().Timestamp >= (ulong)((long)activation + offset) ? after : before);

        if (offset == 0)
            Assert.That(() => BeaconChainModule.ValidateGloasSchedule(spec, execution), Throws.Nothing);
        else
            AssertConflictingConfiguration(() => BeaconChainModule.ValidateGloasSchedule(spec, execution));
    }

    [Test]
    public void An_unscheduled_gloas_rejects_a_scheduled_amsterdam([Values] bool scheduled)
    {
        ISpecProvider execution = Substitute.For<ISpecProvider>();
        IReleaseSpec final = Substitute.For<IReleaseSpec>();
        final.BlockLevelAccessListsEnabled.Returns(scheduled);
        execution.GetSpec(Arg.Any<ForkActivation>()).Returns(final);

        if (scheduled)
            AssertConflictingConfiguration(() => BeaconChainModule.ValidateGloasSchedule(BeaconChainSpec.Mainnet, execution));
        else
            Assert.That(() => BeaconChainModule.ValidateGloasSchedule(BeaconChainSpec.Mainnet, execution), Throws.Nothing);
    }

    // Startup surfaces only InvalidConfigurationException as the operator's message and the configuration exit code.
    private static void AssertConflictingConfiguration(Action validate) =>
        Assert.That(validate, Throws.TypeOf<InvalidConfigurationException>()
            .With.Property(nameof(InvalidConfigurationException.ExitCode)).EqualTo(ExitCodes.ConflictingConfigurations));
}
