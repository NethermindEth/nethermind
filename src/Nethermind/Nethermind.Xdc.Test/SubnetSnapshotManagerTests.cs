// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
internal class SubnetSnapshotManagerTests
{
    private ISnapshotManager _snapshotManager;
    private IBlockTree _blockTree;
    private IXdcReleaseSpec _xdcReleaseSpec;
    private IDb _snapshotDb;

    [SetUp]
    public void Setup()
    {
        _xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        _xdcReleaseSpec.EpochLength.Returns(900);
        _xdcReleaseSpec.Gap.Returns(450);

        _snapshotDb = new MemDb();

        IPenaltyHandler penaltyHandler = Substitute.For<IPenaltyHandler>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(_xdcReleaseSpec);
        _blockTree = Substitute.For<IBlockTree>();
        _snapshotManager = new SubnetSnapshotManager(_snapshotDb, _blockTree, Substitute.For<IMasternodeVotingContract>(), specProvider, Substitute.For<IStateReader>(), LimboLogs.Instance, penaltyHandler);
    }

    [Test]
    public void GetSnapshot_ShouldReturnNullForNonExistentSnapshot()
    {
        // Act
        Snapshot? result = _snapshotManager.GetSnapshotByBlockNumber(0, _xdcReleaseSpec);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetSnapshot_ShouldRetrieveFromIfFound()
    {
        // Arrange
        const int gapBlock = 0;
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        SubnetSnapshot snapshot = new(gapBlock, header.Hash!, [Address.FromNumber(1)], [Address.FromNumber(2)]);
        _snapshotManager.StoreSnapshot(snapshot);
        _blockTree.FindHeader(gapBlock).Returns(header);

        // Act
        SubnetSnapshot? result = _snapshotManager.GetSnapshotByGapNumber(gapBlock) as SubnetSnapshot;

        Assert.That(result, Is.EqualTo(snapshot).UsingXdcComparer());
    }

    [Test]
    public void StoreSnapshot_ShouldRaiseExceptionIfTypeIsWrong()
    {
        // Act
        const int gapBlock = 0;
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        Snapshot snapshot = new(gapBlock, header.Hash!, [Address.FromNumber(1)]);

        // Assert
        Assert.Throws<ArgumentException>(() => _snapshotManager.StoreSnapshot(snapshot));
    }

    [TestCase(450)]
    [TestCase(1350)]
    public void OnUpdateMainChain_StoresSnapshot(int gapNumber)
    {
        IXdcReleaseSpec releaseSpec = Substitute.For<IXdcReleaseSpec>();
        releaseSpec.EpochLength.Returns(900);
        releaseSpec.Gap.Returns(450);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        Address[] penalties = [Address.FromNumber(1), Address.FromNumber(2)];
        IPenaltyHandler penaltyHandler = Substitute.For<IPenaltyHandler>();
        penaltyHandler.HandlePenalties(Arg.Any<long>(), Arg.Any<Hash256>(), Arg.Any<Address[]>()).Returns(penalties);

        SubnetSnapshotManager snapshotManager = new(new MemDb(), blockTree, Substitute.For<IMasternodeVotingContract>(), specProvider, Substitute.For<IStateReader>(), LimboLogs.Instance, penaltyHandler);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithGeneratedExtraConsensusData(1)
            .WithNumber(gapNumber).TestObject;
        blockTree.FindHeader(Arg.Any<long>()).Returns(header);

        blockTree.OnUpdateMainChain += Raise.EventWith(new OnUpdateMainChainArgs([header], true));
        Snapshot? result = snapshotManager.GetSnapshotByGapNumber(gapNumber);

        Assert.That(result, Is.TypeOf<SubnetSnapshot>());
        SubnetSnapshot subnetSnapshot = (SubnetSnapshot)result!;
        Assert.That(subnetSnapshot.HeaderHash, Is.EqualTo(header.Hash!));
        Assert.That(subnetSnapshot.NextEpochPenalties, Is.EquivalentTo(penalties));
    }

}
