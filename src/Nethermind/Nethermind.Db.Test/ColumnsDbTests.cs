// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Db.Test;

public class ColumnsDbTests
{
    string DbPath => "testdb/" + TestContext.CurrentContext.Test.Name;
    private ColumnsDb<ReceiptsColumns> _db = null!;

    [SetUp]
    public void Setup()
    {
        if (Directory.Exists(DbPath))
        {
            Directory.Delete(DbPath, true);
        }

        Directory.CreateDirectory(DbPath);
        ColumnsDb<ReceiptsColumns> columnsDb = new(DbPath,
            new("Blocks", DbPath)
            {
                DeleteOnStart = true,
            },
            new DbConfig(),
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            LimboLogs.Instance,
            Enum.GetValues<ReceiptsColumns>()
        );

        _db = columnsDb;
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public void SmokeTest()
    {
        IDb colA = _db.GetColumnDb(ReceiptsColumns.Blocks);
        IDb colB = _db.GetColumnDb(ReceiptsColumns.Transactions);
        IDb defaultCol = _db.GetColumnDb(ReceiptsColumns.Default);

        colA.Set(TestItem.KeccakA, TestItem.KeccakA.BytesToArray());
        colB.Set(TestItem.KeccakA, TestItem.KeccakB.BytesToArray());

        Assert.That(colA.Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
        Assert.That(colB.Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakB.BytesToArray()));

        Assert.That(defaultCol.Get(TestItem.KeccakB), Is.Null);
    }

    [Test]
    public void SmokeTestGatherMetric()
    {
        IDb colA = _db.GetColumnDb(ReceiptsColumns.Blocks);
        IDb colB = _db.GetColumnDb(ReceiptsColumns.Transactions);

        colA.Set(TestItem.KeccakA, TestItem.KeccakA.BytesToArray());
        colB.Set(TestItem.KeccakA, TestItem.KeccakB.BytesToArray());

        long after = _db.GatherMetric().Size;
        Assert.That(after, Is.GreaterThan(0));
    }

    [Test]
    public void SmokeTestDefaultColumn()
    {
        IDb defaultCol = _db.GetColumnDb(ReceiptsColumns.Default);

        Assert.That(defaultCol.Get(TestItem.KeccakB), Is.Null);
        defaultCol.Set(TestItem.KeccakB, TestItem.KeccakC.BytesToArray());
        Assert.That(defaultCol.Get(TestItem.KeccakB), Is.EqualTo(TestItem.KeccakC.BytesToArray()));

        Assert.That(_db.Get(TestItem.KeccakB), Is.EqualTo(TestItem.KeccakC.BytesToArray()));
    }

    [Test]
    public void TestWriteBatch_WriteToAllColumn()
    {
        IColumnsWriteBatch<ReceiptsColumns> batch = _db.StartWriteBatch();
        IWriteBatch colA = batch.GetColumnBatch(ReceiptsColumns.Blocks);
        IWriteBatch colB = batch.GetColumnBatch(ReceiptsColumns.Transactions);

        colA.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());
        colB.Set(TestItem.KeccakA.Bytes, TestItem.KeccakB.BytesToArray());

        batch.Dispose();

        Assert.That(_db.GetColumnDb(ReceiptsColumns.Blocks).Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
        Assert.That(_db.GetColumnDb(ReceiptsColumns.Transactions).Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakB.BytesToArray()));
    }

    [Test]
    public void ColumnBatch_Clear_RemovesPendingOperationsWithoutClearingColumns()
    {
        IDb colA = _db.GetColumnDb(ReceiptsColumns.Blocks);
        IDb colB = _db.GetColumnDb(ReceiptsColumns.Transactions);
        colA.Set(TestItem.KeccakA, TestItem.KeccakA.BytesToArray());

        IColumnsWriteBatch<ReceiptsColumns> batch = _db.StartWriteBatch();
        IWriteBatch colABatch = batch.GetColumnBatch(ReceiptsColumns.Blocks);
        IWriteBatch colBBatch = batch.GetColumnBatch(ReceiptsColumns.Transactions);
        colABatch.Set(TestItem.KeccakB.Bytes, TestItem.KeccakB.BytesToArray());
        colBBatch.Set(TestItem.KeccakC.Bytes, TestItem.KeccakC.BytesToArray());
        colABatch.Clear();
        batch.Dispose();

        Assert.That(colA.Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
        Assert.That(colA.Get(TestItem.KeccakB), Is.Null);
        Assert.That(colB.Get(TestItem.KeccakC), Is.EqualTo(TestItem.KeccakC.BytesToArray()));
    }

    [Test]
    public void ColumnsBatch_Clear_RemovesAllPendingOperationsWithoutClearingColumns()
    {
        IDb colA = _db.GetColumnDb(ReceiptsColumns.Blocks);
        colA.Set(TestItem.KeccakA, TestItem.KeccakA.BytesToArray());

        IColumnsWriteBatch<ReceiptsColumns> batch = _db.StartWriteBatch();
        IWriteBatch colABatch = batch.GetColumnBatch(ReceiptsColumns.Blocks);
        colABatch.Set(TestItem.KeccakB.Bytes, TestItem.KeccakB.BytesToArray());
        batch.Clear();
        batch.Dispose();

        Assert.That(colA.Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
        Assert.That(colA.Get(TestItem.KeccakB), Is.Null);
    }

    [Test]
    public void ColumnBatch_ThrowsAfterParentBatchIsDisposed()
    {
        IColumnsWriteBatch<ReceiptsColumns> batch = _db.StartWriteBatch();
        IWriteBatch colA = batch.GetColumnBatch(ReceiptsColumns.Blocks);

        batch.Dispose();

        Assert.That(() => colA.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray()), Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void ColumnBatch_Dispose_DoesNotCommitParentBatch()
    {
        using IColumnsWriteBatch<ReceiptsColumns> batch = _db.StartWriteBatch();
        IWriteBatch colA = batch.GetColumnBatch(ReceiptsColumns.Blocks);
        IWriteBatch colB = batch.GetColumnBatch(ReceiptsColumns.Transactions);

        colA.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());
        colA.Dispose();
        colB.Set(TestItem.KeccakB.Bytes, TestItem.KeccakB.BytesToArray());

        Assert.That(_db.GetColumnDb(ReceiptsColumns.Blocks).Get(TestItem.KeccakA), Is.Null);
        Assert.That(_db.GetColumnDb(ReceiptsColumns.Transactions).Get(TestItem.KeccakB), Is.Null);

        batch.Dispose();

        Assert.That(_db.GetColumnDb(ReceiptsColumns.Blocks).Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
        Assert.That(_db.GetColumnDb(ReceiptsColumns.Transactions).Get(TestItem.KeccakB), Is.EqualTo(TestItem.KeccakB.BytesToArray()));
    }

    [Test]
    public void Clear_RemovesValuesFromAllColumns()
    {
        IDb colA = _db.GetColumnDb(ReceiptsColumns.Blocks);
        IDb colB = _db.GetColumnDb(ReceiptsColumns.Transactions);
        IDb defaultCol = _db.GetColumnDb(ReceiptsColumns.Default);

        colA.Set(TestItem.KeccakA, TestItem.KeccakA.BytesToArray());
        colB.Set(TestItem.KeccakB, TestItem.KeccakB.BytesToArray());
        defaultCol.Set(TestItem.KeccakC, TestItem.KeccakC.BytesToArray());

        _db.Clear();

        Assert.That(colA.Get(TestItem.KeccakA), Is.Null);
        Assert.That(colB.Get(TestItem.KeccakB), Is.Null);
        Assert.That(defaultCol.Get(TestItem.KeccakC), Is.Null);
    }

    [Test]
    public void SmokeTest_Snapshot()
    {
        IColumnsDb<ReceiptsColumns> asColumnsDb = _db;
        IDb colA = _db.GetColumnDb(ReceiptsColumns.Blocks);

        colA.Set(TestItem.KeccakA, TestItem.KeccakA.BytesToArray());

        using IColumnDbSnapshot<ReceiptsColumns> snapshot = asColumnsDb.CreateSnapshot();

        colA.Set(TestItem.KeccakA, TestItem.KeccakB.BytesToArray());
        Assert.That(colA.Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakB.BytesToArray()));

        Assert.That(snapshot.GetColumn(ReceiptsColumns.Blocks)
            .Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
    }

    [Test]
    public void SnapshotColumn_KeyExists_TracksSnapshotState()
    {
        IColumnsDb<ReceiptsColumns> asColumnsDb = _db;
        IDb colA = _db.GetColumnDb(ReceiptsColumns.Blocks);
        byte[] key = TestItem.KeccakA.BytesToArray();
        byte[] missingKey = TestItem.KeccakB.BytesToArray();

        colA.Set(key, TestItem.KeccakA.BytesToArray());

        using IColumnDbSnapshot<ReceiptsColumns> snapshot = asColumnsDb.CreateSnapshot();
        IReadOnlyKeyValueStore snapshotColumn = snapshot.GetColumn(ReceiptsColumns.Blocks);
        colA.Set(key, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(colA.KeyExists(key), Is.False);
            Assert.That(snapshotColumn.KeyExists(key), Is.True);
            Assert.That(snapshotColumn.KeyExists(missingKey), Is.False);
        }
    }

    [Test]
    public void Snapshot_DoubleDispose_DoesNotThrow()
    {
        IColumnsDb<ReceiptsColumns> asColumnsDb = _db;
        IColumnDbSnapshot<ReceiptsColumns> snapshot = asColumnsDb.CreateSnapshot();

        snapshot.Dispose();

        Assert.That(() => snapshot.Dispose(), Throws.Nothing);
    }

    [Test]
    public void Snapshot_GetColumn_AfterDispose_ThrowsObjectDisposedException()
    {
        IColumnsDb<ReceiptsColumns> asColumnsDb = _db;
        IColumnDbSnapshot<ReceiptsColumns> snapshot = asColumnsDb.CreateSnapshot();

        snapshot.Dispose();

        Assert.That(() => snapshot.GetColumn(ReceiptsColumns.Blocks), Throws.TypeOf<ObjectDisposedException>());
    }
}
