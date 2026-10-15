// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Specs;
using NSubstitute;

namespace Nethermind.BeaconChain.Test.Spec;

public class GloasScheduleTests
{
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
            Assert.That(() => BeaconChainModule.ValidateGloasSchedule(spec, execution), Throws.ArgumentException);
    }

    [Test]
    public void An_unscheduled_gloas_rejects_a_scheduled_amsterdam([Values] bool scheduled)
    {
        ISpecProvider execution = Substitute.For<ISpecProvider>();
        IReleaseSpec final = Substitute.For<IReleaseSpec>();
        final.BlockLevelAccessListsEnabled.Returns(scheduled);
        execution.GetSpec(Arg.Any<ForkActivation>()).Returns(final);

        if (scheduled)
            Assert.That(() => BeaconChainModule.ValidateGloasSchedule(BeaconChainSpec.Mainnet, execution), Throws.ArgumentException);
        else
            Assert.That(() => BeaconChainModule.ValidateGloasSchedule(BeaconChainSpec.Mainnet, execution), Throws.Nothing);
    }
}
