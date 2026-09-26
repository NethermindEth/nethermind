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
    public void Fork_summary_sees_the_switch_as_a_pending_fork()
    {
        // The summary must read the same schedule the checksum was built from, or eth_config reports no
        // pending fork while the consensus switch is still ahead.
        ForkActivationsSummary summary = CreateXdcForkInfo().GetForkActivationsSummary(Build.A.BlockHeader.WithNumber(100).TestObject);

        Assert.That(summary.Next?.Activation, Is.EqualTo(new ForkActivation(SwitchBlock)));
        Assert.That(summary.Last?.Activation, Is.EqualTo(new ForkActivation(SwitchBlock)));
    }

    [Test]
    public void Engine_parameter_forks_are_part_of_the_schedule()
    {
        // Mainnet values. These gate fee, orderbook and consensus rules rather than a release spec, so none of
        // them reaches the spec transitions - the reference client gathers them from the chain config instead.
        XdcChainSpecEngineParameters engineParameters = new()
        {
            SwitchBlock = SwitchBlock,
            TIP2019Block = 1,
            TipTrc21Fee = 38383838,
            TipXDCX = 38383838,
            BlackListHFNumber = 38383838,
            TIPXDCXMinerDisable = SwitchBlock,
            TIPXDCXReceiverDisable = 80370900,
            DynamicGasLimitBlock = 99999999999999,
        };
        XdcForkInfo forkInfo = new(SpecProvider(), SyncServer(), engineParameters);

        Assert.Multiple(() =>
        {
            Assert.That(forkInfo.GetForkId(100, 0).Next, Is.EqualTo(38383838));
            Assert.That(forkInfo.GetForkId(38383838, 0).Next, Is.EqualTo(SwitchBlock));
            Assert.That(forkInfo.GetForkId(SwitchBlock, 0).Next, Is.EqualTo(80370900));
            Assert.That(forkInfo.GetForkId(80370900, 0).Next, Is.EqualTo(99999999999999));
            Assert.That(forkInfo.GetForkId(99999999999999, 0).Next, Is.EqualTo(0));
        });
    }

    [Test]
    public void Switch_at_genesis_is_not_a_transition()
    {
        Nethermind.Network.ForkId forkId = XdcForkId(headNumber: 100, switchBlock: 0);

        Assert.That(forkId, Is.EqualTo(ForkId(headNumber: 100)));
    }

    private static Nethermind.Network.ForkId XdcForkId(ulong headNumber, ulong switchBlock = SwitchBlock) =>
        CreateXdcForkInfo(switchBlock).GetForkId(headNumber, 0);

    private static XdcForkInfo CreateXdcForkInfo(ulong switchBlock = SwitchBlock) =>
        new(SpecProvider(), SyncServer(), new XdcChainSpecEngineParameters { SwitchBlock = switchBlock });

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
