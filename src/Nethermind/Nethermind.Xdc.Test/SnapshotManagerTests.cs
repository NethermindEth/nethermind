// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        // Initialize _snapshotManager with a mock or real implementation
        _snapshotManager = new SnapshotManager(_snapshotDb, blockTree, xdcConfig); // Replace with actual initialization
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
        var snapshot = new Snapshot(1, TestItem.KeccakC, [Address.Zero, Address.MaxValue]);
        // Act
        _snapshotManager.TryCacheSnapshot(snapshot);
        // Assert
        Assert.That(_snapshotManager.TryGetSnapshot(TestItem.KeccakC, out var cachedSnapshot), Is.True);
        Assert.That(snapshot, Is.EqualTo(cachedSnapshot));
    }

    [Test]
    public void TryGetSnapshot_ShouldReturnFalseForNonExistentSnapshot()
    {
        // Act
        var result = _snapshotManager.TryGetSnapshot(TestItem.KeccakD, out var snapshot);
        // Assert
        Assert.That(result, Is.False);
        Assert.That(snapshot, Is.Null);
    }

    [Test]
    public void TryCacheSnapshot_ShouldHandleNullSnapshot()
    {
        // Act
        _snapshotManager.TryCacheSnapshot(null!);
        // Assert
        Assert.Pass("No exception thrown for null snapshot");
    }

    [Test]
    public void TryGetSnapshot_ShouldRetrieveFromCacheIfFound()
    {
        // Arrange
        var snapshot = new Snapshot(2, TestItem.KeccakE, [Address.FromNumber(1), Address.FromNumber(2)]);

        // does not store in DB, only in cache
        _snapshotManager.TryCacheSnapshot(snapshot);
        // Act
        var result = _snapshotManager.TryGetSnapshot(TestItem.KeccakE, out var retrievedSnapshot);

        // assert that it was retrieved from cache
        Assert.That(result, Is.True);
        Assert.That(retrievedSnapshot, Is.EqualTo(snapshot));
    }

    [Test]
    public void TryGetSnapshot_ShouldReturnFalseForEmptyDb()
    {
        // Arrange
        var hash = TestItem.KeccakF;
        // Act
        var result = _snapshotManager.TryGetSnapshot(hash, out var snapshot);
        // Assert
        Assert.That(result, Is.False);
        Assert.That(snapshot, Is.Null);
    }

    [Test]
    public void TryGetSnapshot_ShouldRetrieveFromDbIfNotInCache()
    {
        // Arrange
        var snapshot = new Snapshot(3, TestItem.KeccakG, [Address.FromNumber(3)]);

        var result = _snapshotManager.TryStoreSnapshot(snapshot);
        Assert.That(result, Is.True);

        // Act
        result = _snapshotManager.TryGetSnapshot(TestItem.KeccakG, out var retrievedSnapshot);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(retrievedSnapshot, Is.EqualTo(snapshot));
    }

    [Test]
    public void TryStoreSnapshot_ShouldStoreSnapshotInDb()
    {
        // Arrange
        var snapshot = new Snapshot(4, TestItem.KeccakH, [Address.FromNumber(4)]);
        // Act
        var result = _snapshotManager.TryStoreSnapshot(snapshot);
        // Assert
        Assert.That(result, Is.True);
        Assert.That(_snapshotManager.TryGetSnapshot(TestItem.KeccakH, out var retrievedSnapshot), Is.True);
        Assert.That(retrievedSnapshot, Is.EqualTo(snapshot));
    }

    [Test]
    public void TryStoreSnapshot_ShouldReturnFalseForNullSnapshot()
    {
        // Act
        var result = _snapshotManager.TryStoreSnapshot(null!);
        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void TryGetSnapshot_ShouldReturnSnapshotIfExists()
    {
        // Arrange
        var snapshot1 = new Snapshot(5, TestItem.KeccakA, [Address.FromNumber(5)]);
        _snapshotManager.TryStoreSnapshot(snapshot1);
        // Act
        var result = _snapshotManager.TryGetSnapshot(TestItem.KeccakA, out var retrievedSnapshot);
        // Assert
        Assert.That(result, Is.True);
        Assert.That(retrievedSnapshot, Is.EqualTo(snapshot1));

        var snapshot2 = new Snapshot(6, TestItem.KeccakA, [Address.FromNumber(5)]);
        result = _snapshotManager.TryStoreSnapshot(snapshot2);
        Assert.That(result, Is.False);

        result = _snapshotManager.TryGetSnapshot(TestItem.KeccakA, out retrievedSnapshot);
        Assert.That(result, Is.True);
        Assert.That(retrievedSnapshot, Is.Not.Null);
    }

    [Test]
    public void TryGetSnapshotByHeader_ShouldReturnFalseIfNotExists()
    {
        XdcBlockHeaderBuilder builder = Build.A.XdcBlockHeader();
        XdcBlockHeader header = builder.WithBaseFee((UInt256)1_000_000_000).TestObject;
        header.Hash = TestItem.KeccakH;
        // Act
        var result = _snapshotManager.TryGetSnapshot(header, out var snapshot);
        // Assert
        Assert.That(result, Is.False);
        Assert.That(snapshot, Is.Null);
    }

    [Test]
    public void TryGetSnapshotByHeader_ShouldReturnSnapshotIfExists()
    {
        // Arrange
        var snapshot = new Snapshot(7, TestItem.KeccakB, [Address.FromNumber(6)]);
        _snapshotManager.TryStoreSnapshot(snapshot);
        XdcBlockHeaderBuilder builder = Build.A.XdcBlockHeader();
        XdcBlockHeader header = builder.WithBaseFee((UInt256)1_000_000_000).TestObject;
        header.Hash = TestItem.KeccakB;

        // Act
        var result = _snapshotManager.TryGetSnapshot(header, out var retrievedSnapshot);
        // Assert
        Assert.That(result, Is.True);
        Assert.That(retrievedSnapshot, Is.EqualTo(snapshot));
    }
}
