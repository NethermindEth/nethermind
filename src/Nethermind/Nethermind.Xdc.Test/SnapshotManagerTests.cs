// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

internal class SnapshotManagerTests
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

        IPenaltyHandler penaltyHandler = NSubstitute.Substitute.For<IPenaltyHandler>();
        _blockTree = Substitute.For<IBlockTree>();
        _snapshotManager = new SnapshotManager(_snapshotDb, _blockTree, penaltyHandler);
    }

    [Test]
    public void GetSnapshot_ShouldReturnNullForNonExistentSnapshot()
    {
        // Act
        var result = _snapshotManager.GetSnapshotByBlockNumber(0, _xdcReleaseSpec);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void GetSnapshot_ShouldRetrieveFromIfFound()
    {
        // Arrange
        const int gapBlock = 0;
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        var snapshot = new Snapshot(gapBlock, header.Hash!, [Address.FromNumber(1)]);
        _snapshotManager.StoreSnapshot(snapshot);
        _blockTree.FindHeader(gapBlock).Returns(header);

        // Act
        var result = _snapshotManager.GetSnapshotByGapNumber(gapBlock);

        // assert that it was retrieved from cache
        result.Should().BeEquivalentTo(snapshot);
    }

    [Test]
    public void GetSnapshot_ShouldReturnNullForEmptyDb()
    {
        // Act
        var result = _snapshotManager.GetSnapshotByBlockNumber(0, _xdcReleaseSpec);
        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void GetSnapshot_ShouldRetrieveFromDbIfNotInCache()
    {
        // Arrange
        const int gapBlock = 0;
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        var snapshot = new Snapshot(gapBlock, header.Hash!, [Address.FromNumber(1)]);
        _snapshotManager.StoreSnapshot(snapshot);
        _blockTree.FindHeader(gapBlock).Returns(header);

        // Act
        var saved = _snapshotManager.GetSnapshotByGapNumber(gapBlock);

        // Assert
        saved.Should().BeEquivalentTo(snapshot);
    }

    [Test]
    public void StoreSnapshot_ShouldStoreSnapshotInDb()
    {
        // Arrange
        const int gapBlock = 0;
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        var snapshot = new Snapshot(gapBlock, header.Hash!, [Address.FromNumber(1)]);
        _blockTree.FindHeader(gapBlock).Returns(header);

        // Act
        _snapshotManager.StoreSnapshot(snapshot);
        var fromDb = _snapshotManager.GetSnapshotByGapNumber(gapBlock);

        // Assert
        fromDb.Should().BeEquivalentTo(snapshot);
    }

    [Test]
    public void GetSnapshot_ShouldReturnSnapshotIfExists()
    {
        // setup a snapshot and store it
        const int gapBlock1 = 0;
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        var snapshot1 = new Snapshot(gapBlock1, header.Hash!, [Address.FromNumber(1)]);
        _snapshotManager.StoreSnapshot(snapshot1);
        _blockTree.FindHeader(gapBlock1).Returns(header);
        var result = _snapshotManager.GetSnapshotByGapNumber(gapBlock1);

        // assert that it was retrieved from db
        result.Should().BeEquivalentTo(snapshot1);

        // store another snapshot with the same hash but different data

        const int gapBlock2 = 450;
        XdcBlockHeader header2 = Build.A.XdcBlockHeader().WithGeneratedExtraConsensusData(1).TestObject;
        var snapshot2 = new Snapshot(gapBlock2, header2.Hash!, [Address.FromNumber(2)]);
        _snapshotManager.StoreSnapshot(snapshot2);
        _blockTree.FindHeader(gapBlock2).Returns(header2);
        _snapshotManager.StoreSnapshot(snapshot2);
        result = _snapshotManager.GetSnapshotByBlockNumber(900, _xdcReleaseSpec);

        // assert that the original snapshot is still returned
        result.Should().BeEquivalentTo(snapshot2);
    }

    [TestCase(1ul, 0ul)]
    [TestCase(451ul, 0ul)]
    [TestCase(899ul, 0ul)]
    [TestCase(900ul, 450ul)]
    [TestCase(1349ul, 450ul)]
    [TestCase(1350ul, 450ul)]
    [TestCase(1800ul, 1350ul)]
    public void GetSnapshot_DifferentBlockNumbers_ReturnsSnapshotFromCorrectGapNumber(ulong blockNumber, ulong expectedGapNumber)
    {
        // setup a snapshot and store it
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        var snapshot = new Snapshot(expectedGapNumber, header.Hash!, [Address.FromNumber(1)]);
        _snapshotManager.StoreSnapshot(snapshot);
        _blockTree.FindHeader(expectedGapNumber, BlockTreeLookupOptions.RequireCanonical).Returns(header);
        var result = _snapshotManager.GetSnapshotByBlockNumber(blockNumber, _xdcReleaseSpec);

        // assert that it was retrieved from db
        result.Should().BeEquivalentTo(snapshot);
    }
}
