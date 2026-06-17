// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

internal class SnapshotManagerTests
{
    private ISnapshotManager _snapshotManager;
    private IBlockTree _blockTree;
    private IXdcReleaseSpec _xdcReleaseSpec;
    private ISpecProvider _specProvider;
    private IStateReader _stateReader;
    private IMasternodeVotingContract _votingContract;
    private IDb _snapshotDb;

    [SetUp]
    public void Setup()
    {
        _xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        _xdcReleaseSpec.EpochLength.Returns(900);
        _xdcReleaseSpec.Gap.Returns(450);

        _snapshotDb = new MemDb();
        _blockTree = Substitute.For<IBlockTree>();
        _stateReader = Substitute.For<IStateReader>();
        _votingContract = Substitute.For<IMasternodeVotingContract>();
        _specProvider = Substitute.For<ISpecProvider>();
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(_xdcReleaseSpec);

        _snapshotManager = new SnapshotManager(_snapshotDb, _blockTree, _votingContract, _specProvider, _stateReader, LimboLogs.Instance);
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
        Snapshot snapshot = new(gapBlock, header.Hash!, [Address.FromNumber(1)]);
        _snapshotManager.StoreSnapshot(snapshot);
        _blockTree.FindHeader(gapBlock).Returns(header);

        // Act
        Snapshot? result = _snapshotManager.GetSnapshotByGapNumber(gapBlock);

        // assert that it was retrieved from cache
        Assert.That(result, Is.EqualTo(snapshot).UsingXdcComparer());
    }

    [Test]
    public void GetSnapshot_ShouldReturnNullForEmptyDb()
    {
        // Act
        Snapshot? result = _snapshotManager.GetSnapshotByBlockNumber(0, _xdcReleaseSpec);
        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetSnapshot_ShouldRetrieveFromDbIfNotInCache()
    {
        // Arrange
        const int gapBlock = 0;
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        Snapshot snapshot = new(gapBlock, header.Hash!, [Address.FromNumber(1)]);
        _snapshotManager.StoreSnapshot(snapshot);
        _blockTree.FindHeader(gapBlock).Returns(header);

        // Act
        Snapshot? saved = _snapshotManager.GetSnapshotByGapNumber(gapBlock);

        // Assert
        Assert.That(saved, Is.EqualTo(snapshot).UsingXdcComparer());
    }

    [Test]
    public void StoreSnapshot_ShouldStoreSnapshotInDb()
    {
        // Arrange
        const int gapBlock = 0;
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        Snapshot snapshot = new(gapBlock, header.Hash!, [Address.FromNumber(1)]);
        _blockTree.FindHeader(gapBlock).Returns(header);

        // Act
        _snapshotManager.StoreSnapshot(snapshot);
        Snapshot? fromDb = _snapshotManager.GetSnapshotByGapNumber(gapBlock);

        // Assert
        Assert.That(fromDb, Is.EqualTo(snapshot).UsingXdcComparer());
    }

    [Test]
    public void GetSnapshot_ShouldReturnSnapshotIfExists()
    {
        // setup a snapshot and store it
        const int gapBlock1 = 0;
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        Snapshot snapshot1 = new(gapBlock1, header.Hash!, [Address.FromNumber(1)]);
        _snapshotManager.StoreSnapshot(snapshot1);
        _blockTree.FindHeader(gapBlock1).Returns(header);
        Snapshot? result = _snapshotManager.GetSnapshotByGapNumber(gapBlock1);

        // assert that it was retrieved from db
        Assert.That(result, Is.EqualTo(snapshot1).UsingXdcComparer());

        // store another snapshot with the same hash but different data

        const int gapBlock2 = 450;
        XdcBlockHeader header2 = Build.A.XdcBlockHeader().WithGeneratedExtraConsensusData(1).TestObject;
        Snapshot snapshot2 = new(gapBlock2, header2.Hash!, [Address.FromNumber(2)]);
        _snapshotManager.StoreSnapshot(snapshot2);
        _blockTree.FindHeader(gapBlock2).Returns(header2);
        _snapshotManager.StoreSnapshot(snapshot2);
        result = _snapshotManager.GetSnapshotByBlockNumber(900, _xdcReleaseSpec);

        // assert that the original snapshot is still returned
        Assert.That(result, Is.EqualTo(snapshot2).UsingXdcComparer());
    }

    [TestCase(1, 0)]
    [TestCase(451, 0)]
    [TestCase(899, 0)]
    [TestCase(900, 450)]
    [TestCase(1349, 450)]
    [TestCase(1350, 450)]
    [TestCase(1800, 1350)]
    public void GetSnapshot_DifferentBlockNumbers_ReturnsSnapshotFromCorrectGapNumber(int blockNumber, int expectedGapNumber)
    {
        // setup a snapshot and store it
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        Snapshot snapshot = new(expectedGapNumber, header.Hash!, [Address.FromNumber(1)]);
        _snapshotManager.StoreSnapshot(snapshot);
        _blockTree.FindHeader(expectedGapNumber).Returns(header);
        Snapshot? result = _snapshotManager.GetSnapshotByBlockNumber(blockNumber, _xdcReleaseSpec);

        // assert that it was retrieved from db
        Assert.That(result, Is.EqualTo(snapshot).UsingXdcComparer());
    }

    [TestCase(450)]
    [TestCase(1350)]
    public void OnUpdateMainChain_ShouldStoreSnapshot(int gapNumber)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(_xdcReleaseSpec);
        SnapshotManager snapshotManager = new(new MemDb(), blockTree, Substitute.For<IMasternodeVotingContract>(), specProvider, _stateReader, LimboLogs.Instance);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithGeneratedExtraConsensusData(1)
            .WithNumber(gapNumber).TestObject;
        blockTree.FindHeader(Arg.Any<long>()).Returns(header);

        blockTree.OnUpdateMainChain += Raise.EventWith(new OnUpdateMainChainArgs([header], true));
        Snapshot? result = snapshotManager.GetSnapshotByGapNumber(header.Number);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.HeaderHash, Is.EqualTo(header.Hash!));
    }

    [Test]
    public void TryRecoverSnapshot_ReturnsSnapshot_WhenStateAndProcessingAvailable()
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithGeneratedExtraConsensusData(1)
            .WithNumber(450).TestObject;
        _blockTree.FindHeader(450).Returns(header);
        _stateReader.HasStateForBlock(header).Returns(true);
        _blockTree.WasProcessed(450, header.Hash!).Returns(true);

        Snapshot? result = _snapshotManager.GetSnapshotByGapNumber(450);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.HeaderHash, Is.EqualTo(header.Hash!));
    }

    [Test]
    public void TryRecoverSnapshot_ReturnsNull_WhenStateUnavailable()
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithGeneratedExtraConsensusData(1)
            .WithNumber(450).TestObject;
        _blockTree.FindHeader(450).Returns(header);
        _blockTree.WasProcessed(450, header.Hash!).Returns(true);
        _stateReader.HasStateForBlock(header).Returns(false);

        Snapshot? result = _snapshotManager.GetSnapshotByGapNumber(450);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void TryRecoverSnapshot_ReturnsNull_WhenBlockNotProcessed()
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithGeneratedExtraConsensusData(1)
            .WithNumber(450).TestObject;
        _blockTree.FindHeader(450).Returns(header);
        _stateReader.HasStateForBlock(header).Returns(true);
        _blockTree.WasProcessed(450, header.Hash!).Returns(false);

        Snapshot? result = _snapshotManager.GetSnapshotByGapNumber(450);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void TryRecoverSnapshot_Throws_WhenCreateSnapshotThrows()
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithGeneratedExtraConsensusData(1)
            .WithNumber(450).TestObject;
        _blockTree.FindHeader(450).Returns(header);
        _stateReader.HasStateForBlock(header).Returns(true);
        _blockTree.WasProcessed(450, header.Hash!).Returns(true);
        _votingContract.GetCandidatesByStake(header).Throws(new Exception("contract failure"));

        Assert.Throws<Exception>(() => _snapshotManager.GetSnapshotByGapNumber(450));
    }

    [Test]
    public void TryRecoverSnapshot_ReturnsNull_WhenNotSnapshotBlock()
    {
        // Block 100 is not a snapshot block (100 % 900 != 450)
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithGeneratedExtraConsensusData(1)
            .WithNumber(100).TestObject;
        _blockTree.FindHeader(100).Returns(header);

        Snapshot? result = _snapshotManager.GetSnapshotByGapNumber(100);

        Assert.That(result, Is.Null);
        _stateReader.DidNotReceiveWithAnyArgs().HasStateForBlock(default);
    }
}
