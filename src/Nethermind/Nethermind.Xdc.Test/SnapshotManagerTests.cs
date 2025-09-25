// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class SnapshotManagerTests
{
    private ISnapshotManager _snapshotManager;
    private readonly IDb _snapshotDb = new MemDb();

    [SetUp]
    public void Setup()
    {
        IBlockTree blockTree = NSubstitute.Substitute.For<IBlockTree>();
        IXdcConfig xdcConfig = NSubstitute.Substitute.For<IXdcConfig>();
        _snapshotManager = new SnapshotManager(_snapshotDb, blockTree);
    }

    [TearDown]
    public void TearDown()
    {
        _snapshotDb.Dispose();
    }

    [Test]
    public void GetSnapshot_ShouldReturnFalseForNonExistentSnapshot()
    {
        // Act
        var result = _snapshotManager.GetSnapshot(TestItem.KeccakD);
        // Assert

        result.Should().BeNull();
    }

    [Test]
    public void GetSnapshot_ShouldRetrieveFromIfFound()
    {
        // Arrange
        var snapshot = new Snapshot(2, TestItem.KeccakE, [Address.FromNumber(1), Address.FromNumber(2)], [Address.FromNumber(1)]);

        _snapshotManager.StoreSnapshot(snapshot);

        // Act
        var result = _snapshotManager.GetSnapshot(TestItem.KeccakE);

        // assert that it was retrieved from cache
        result.Should().BeEquivalentTo(snapshot);
    }

    [Test]
    public void GetSnapshot_ShouldReturnFalseForEmptyDb()
    {
        // Arrange
        var hash = TestItem.KeccakF;
        // Act
        var result = _snapshotManager.GetSnapshot(hash);
        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void GetSnapshot_ShouldRetrieveFromDbIfNotInCache()
    {
        // Arrange
        var snapshot = new Snapshot(3, TestItem.KeccakG, [Address.FromNumber(3)], [Address.FromNumber(3)]);

        var result = _snapshotManager.StoreSnapshot(snapshot);
        Assert.That(result, Is.True);

        // Act
        var saved = _snapshotManager.GetSnapshot(TestItem.KeccakG);

        // Assert
        saved.Should().BeEquivalentTo(snapshot);
    }

    [Test]
    public void StoreSnapshot_ShouldStoreSnapshotInDb()
    {
        // Arrange
        var snapshot = new Snapshot(4, TestItem.KeccakH, [Address.FromNumber(4)], [Address.FromNumber(4)]);
        // Act
        var result = _snapshotManager.StoreSnapshot(snapshot);
        result.Should().BeTrue();
        // Assert

        var fromDb = _snapshotManager.GetSnapshot(TestItem.KeccakH);

        fromDb.Should().BeEquivalentTo(snapshot);
    }

    [Test]
    public void StoreSnapshot_ShouldReturnFalseForNullSnapshot()
    {
        // Act
        var result = _snapshotManager.StoreSnapshot(null!);
        // Assert

        result.Should().BeFalse();
    }

    [Test]
    public void GetSnapshot_ShouldReturnSnapshotIfExists()
    {
        // Arrange
        var snapshot1 = new Snapshot(5, TestItem.KeccakA, [Address.FromNumber(5)], [Address.FromNumber(5)]);
        _snapshotManager.StoreSnapshot(snapshot1);
        // Act
        var result = _snapshotManager.GetSnapshot(TestItem.KeccakA);
        // Assert

        result.Should().BeEquivalentTo(snapshot1);

        var snapshot2 = new Snapshot(6, TestItem.KeccakA, [Address.FromNumber(5)], [Address.FromNumber(5)]);
        var wasSaved = _snapshotManager.StoreSnapshot(snapshot2);
        wasSaved.Should().BeFalse();

        result = _snapshotManager.GetSnapshot(TestItem.KeccakA);

        result.Should().BeEquivalentTo(snapshot1);
    }

    [Test]
    public void GetSnapshotByHeader_ShouldReturnFalseIfNotExists()
    {
        XdcBlockHeaderBuilder builder = Build.A.XdcBlockHeader();
        XdcBlockHeader header = builder.WithBaseFee((UInt256)1_000_000_000).TestObject;
        header.Hash = TestItem.KeccakH;
        // Act
        var result = _snapshotManager.GetSnapshotByHeader(header);
        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void GetSnapshotByHeader_ShouldReturnSnapshotIfExists()
    {
        // Arrange
        var snapshot = new Snapshot(7, TestItem.KeccakB, [Address.FromNumber(6)], [Address.FromNumber(6)]);
        _snapshotManager.StoreSnapshot(snapshot);
        XdcBlockHeaderBuilder builder = Build.A.XdcBlockHeader();
        XdcBlockHeader header = builder.WithBaseFee((UInt256)1_000_000_000).TestObject;
        header.Hash = TestItem.KeccakB;

        // Act
        var result = _snapshotManager.GetSnapshotByHeader(header);

        // Assert
        result.Should().BeEquivalentTo(snapshot);
    }
}
