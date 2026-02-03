// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

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
        _blockTree = Substitute.For<IBlockTree>();
        _snapshotManager = new SubnetSnapshotManager(_snapshotDb, _blockTree, penaltyHandler, Substitute.For<IMasternodeVotingContract>(), Substitute.For<ISpecProvider>());
    }

    [Test]
    public void GetSnapshot_ShouldReturnNullForNonExistentSnapshot()
    {
        // Act
        Snapshot? result = _snapshotManager.GetSnapshotByBlockNumber(0, _xdcReleaseSpec);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void GetSnapshot_ShouldRetrieveFromIfFound()
    {
        // Arrange
        const int gapBlock = 0;
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        var snapshot = new SubnetSnapshot(gapBlock, header.Hash!, [Address.FromNumber(1)], [Address.FromNumber(2)]);
        _snapshotManager.StoreSnapshot(snapshot);
        _blockTree.FindHeader(gapBlock).Returns(header);

        // Act
        SubnetSnapshot? result = _snapshotManager.GetSnapshotByGapNumber(gapBlock) as SubnetSnapshot;

        // assert that it was retrieved from cache
        result.Should().BeEquivalentTo(snapshot);
    }

    [Test]
    public void StoreSnapshot_ShouldRaiseExceptionIfTypeIsWrong()
    {
        // Act
        const int gapBlock = 0;
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        var snapshot = new Snapshot(gapBlock, header.Hash!, [Address.FromNumber(1)]);

        // Assert
        Assert.Throws<ArgumentException>(() => _snapshotManager.StoreSnapshot(snapshot));
    }

    [TestCase(450)]
    [TestCase(1350)]
    public void NewHeadBlock_(int gapNumber)
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

        SubnetSnapshotManager snapshotManager = new SubnetSnapshotManager(new MemDb(), blockTree, penaltyHandler, Substitute.For<IMasternodeVotingContract>(), specProvider);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithGeneratedExtraConsensusData(1)
            .WithNumber(gapNumber).TestObject;
        blockTree.FindHeader(Arg.Any<long>()).Returns(header);

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(new Block(header)));
        Snapshot? result = snapshotManager.GetSnapshotByGapNumber(gapNumber);
        result.Should().BeOfType<SubnetSnapshot>();

        SubnetSnapshot subnetSnapshot = (SubnetSnapshot)result!;
        subnetSnapshot.HeaderHash.Should().Be(header.Hash!);
        subnetSnapshot.NextEpochPenalties.Should().BeEquivalentTo(penalties);
    }
}
