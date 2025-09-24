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
        _snapshotManager = new SnapshotManager(_snapshotDb, blockTree, xdcConfig);
    }

    [TearDown]
    public void TearDown()
    {
        _snapshotDb.Dispose();
    }

    [Test]
    public void TryCacheSnapshot_ShouldCacheSnapshot()
    {
        // Arrange
        var snapshot = new Snapshot(1, TestItem.KeccakC, [Address.Zero, Address.MaxValue], [Address.FromNumber(1)]);
        // Act
        _snapshotManager.TryCacheSnapshot(snapshot);
        // Assert
        var result = _snapshotManager.TryGetSnapshot(TestItem.KeccakC, out var cachedSnapshot);

        result.Should().BeTrue();
        cachedSnapshot.Should().BeEquivalentTo(snapshot);
    }

    [Test]
    public void TryGetSnapshot_ShouldReturnFalseForNonExistentSnapshot()
    {
        // Act
        var result = _snapshotManager.TryGetSnapshot(TestItem.KeccakD, out var snapshot);
        // Assert

        result.Should().BeFalse();
        snapshot.Should().BeNull();
    }

    [Test]
    public void TryGetSnapshot_ShouldRetrieveFromCacheIfFound()
    {
        // Arrange
        var snapshot = new Snapshot(2, TestItem.KeccakE, [Address.FromNumber(1), Address.FromNumber(2)], [Address.FromNumber(1)]);

        // does not store in DB, only in cache
        _snapshotManager.TryCacheSnapshot(snapshot);
        // Act
        var result = _snapshotManager.TryGetSnapshot(TestItem.KeccakE, out var retrievedSnapshot);

        // assert that it was retrieved from cache

        result.Should().BeTrue();
        retrievedSnapshot.Should().BeEquivalentTo(snapshot);
    }

    [Test]
    public void TryGetSnapshot_ShouldReturnFalseForEmptyDb()
    {
        // Arrange
        var hash = TestItem.KeccakF;
        // Act
        var result = _snapshotManager.TryGetSnapshot(hash, out var snapshot);
        // Assert
        result.Should().BeFalse();
        snapshot.Should().BeNull();
    }

    [Test]
    public void TryGetSnapshot_ShouldRetrieveFromDbIfNotInCache()
    {
        // Arrange
        var snapshot = new Snapshot(3, TestItem.KeccakG, [Address.FromNumber(3)], [Address.FromNumber(3)]);

        var result = _snapshotManager.TryStoreSnapshot(snapshot);
        Assert.That(result, Is.True);

        // Act
        result = _snapshotManager.TryGetSnapshot(TestItem.KeccakG, out var retrievedSnapshot);

        // Assert
        result.Should().BeTrue();
        retrievedSnapshot.Should().BeEquivalentTo(snapshot);
    }

    [Test]
    public void TryStoreSnapshot_ShouldStoreSnapshotInDb()
    {
        // Arrange
        var snapshot = new Snapshot(4, TestItem.KeccakH, [Address.FromNumber(4)], [Address.FromNumber(4)]);
        // Act
        var result = _snapshotManager.TryStoreSnapshot(snapshot);
        result.Should().BeTrue();
        // Assert

        var existsInDb = _snapshotManager.TryGetSnapshot(TestItem.KeccakH, out var retrievedSnapshot);
        existsInDb.Should().BeTrue();

        retrievedSnapshot.Should().BeEquivalentTo(snapshot);
    }

    [Test]
    public void TryStoreSnapshot_ShouldReturnFalseForNullSnapshot()
    {
        // Act
        var result = _snapshotManager.TryStoreSnapshot(null!);
        // Assert

        result.Should().BeFalse();
    }

    [Test]
    public void TryGetSnapshot_ShouldReturnSnapshotIfExists()
    {
        // Arrange
        var snapshot1 = new Snapshot(5, TestItem.KeccakA, [Address.FromNumber(5)], [Address.FromNumber(5)]);
        _snapshotManager.TryStoreSnapshot(snapshot1);
        // Act
        var result = _snapshotManager.TryGetSnapshot(TestItem.KeccakA, out var retrievedSnapshot);
        // Assert

        result.Should().BeTrue();
        retrievedSnapshot.Should().BeEquivalentTo(snapshot1);

        var snapshot2 = new Snapshot(6, TestItem.KeccakA, [Address.FromNumber(5)], [Address.FromNumber(5)]);
        result = _snapshotManager.TryStoreSnapshot(snapshot2);
        result.Should().BeFalse();

        result = _snapshotManager.TryGetSnapshot(TestItem.KeccakA, out retrievedSnapshot);
        result.Should().BeTrue();

        retrievedSnapshot.Should().BeEquivalentTo(snapshot1);
    }

    [Test]
    public void TryGetSnapshotByHeader_ShouldReturnFalseIfNotExists()
    {
        XdcBlockHeaderBuilder builder = Build.A.XdcBlockHeader();
        XdcBlockHeader header = builder.WithBaseFee((UInt256)1_000_000_000).TestObject;
        header.Hash = TestItem.KeccakH;
        // Act
        var result = _snapshotManager.TryGetSnapshotByHeader(header, out var snapshot);
        // Assert
        result.Should().BeFalse();
        snapshot.Should().BeNull();
    }

    [Test]
    public void TryGetSnapshotByHeader_ShouldReturnSnapshotIfExists()
    {
        // Arrange
        var snapshot = new Snapshot(7, TestItem.KeccakB, [Address.FromNumber(6)], [Address.FromNumber(6)]);
        _snapshotManager.TryStoreSnapshot(snapshot);
        XdcBlockHeaderBuilder builder = Build.A.XdcBlockHeader();
        XdcBlockHeader header = builder.WithBaseFee((UInt256)1_000_000_000).TestObject;
        header.Hash = TestItem.KeccakB;

        // Act
        var result = _snapshotManager.TryGetSnapshotByHeader(header, out var retrievedSnapshot);
        // Assert

        result.Should().BeTrue();
        retrievedSnapshot.Should().BeEquivalentTo(snapshot);
    }
}
