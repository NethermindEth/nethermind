// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Network;
using Nethermind.Synchronization;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcForkInfoTests
{
    private const ulong SwitchBlock = 80370000;
    private static readonly ForkActivation[] SpecTransitions = [new(1), new(100)];

    [Test]
    public void Consensus_switch_is_part_of_the_fork_schedule() =>
        Assert.That(XdcForkId(headNumber: 100).Next, Is.EqualTo(SwitchBlock));

    [Test]
    public void Consensus_switch_changes_the_checksum_once_passed() =>
        Assert.That(XdcForkId(headNumber: SwitchBlock).ForkHash, Is.Not.EqualTo(ForkId(headNumber: SwitchBlock).ForkHash));

    [Test]
    public void Schedule_matches_the_spec_transitions_before_the_switch() =>
        Assert.That(XdcForkId(headNumber: 1).ForkHash, Is.EqualTo(ForkId(headNumber: 1).ForkHash));

    [Test]
    public void Switch_at_genesis_is_not_a_transition()
    {
        Nethermind.Network.ForkId forkId = XdcForkId(headNumber: 100, switchBlock: 0);

        Assert.That(forkId, Is.EqualTo(ForkId(headNumber: 100)));
    }

    private static Nethermind.Network.ForkId XdcForkId(ulong headNumber, ulong switchBlock = SwitchBlock)
    {
        XdcChainSpecEngineParameters engineParameters = new() { SwitchBlock = switchBlock };
        XdcForkInfo forkInfo = new(SpecProvider(), SyncServer(), engineParameters);
        return forkInfo.GetForkId(headNumber, 0);
    }

    private static Nethermind.Network.ForkId ForkId(ulong headNumber)
    {
        ForkInfo forkInfo = new(SpecProvider(), SyncServer());
        return forkInfo.GetForkId(headNumber, 0);
    }

    private static ISpecProvider SpecProvider()
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.TransitionActivations.Returns(SpecTransitions);
        specProvider.TimestampFork.Returns(ISpecProvider.TimestampForkNever);
        return specProvider;
    }

    private static ISyncServer SyncServer()
    {
        ISyncServer syncServer = Substitute.For<ISyncServer>();
        syncServer.Genesis.Returns(Build.A.BlockHeader.WithNumber(0).TestObject);
        return syncServer;
    }
}
