// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    public void SmokeTestMemtableSize()
    {
        IDb colA = _db.GetColumnDb(ReceiptsColumns.Blocks);
        IDb colB = _db.GetColumnDb(ReceiptsColumns.Transactions);

        long baseline = _db.GatherMetric().MemtableSize;

        colA.Set(TestItem.KeccakA, TestItem.KeccakA.BytesToArray());
        colB.Set(TestItem.KeccakA, TestItem.KeccakB.BytesToArray());

        // RocksDB lazily allocates per-column memtables; size reported is dominated by allocation
        // overhead (~1 MB per family) rather than payload. We only verify the metric is wired:
        // after touching two new families it must exceed the baseline and report a non-trivial size.
        long after = _db.GatherMetric().MemtableSize;
        Assert.That(after, Is.GreaterThan(baseline));
        Assert.That(after, Is.GreaterThan(1024));
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

        colA.PutSpan(TestItem.KeccakA.Bytes, TestItem.KeccakA.Bytes);
        colB.PutSpan(TestItem.KeccakA.Bytes, TestItem.KeccakB.Bytes);

        batch.Dispose();

        Assert.That(_db.GetColumnDb(ReceiptsColumns.Blocks).Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
        Assert.That(_db.GetColumnDb(ReceiptsColumns.Transactions).Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakB.BytesToArray()));
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

    private const int MultiGetKeyLength = 32;
    private const int MultiGetValueStride = 64;

    private static ValueHash256 MultiGetKey(int index) => ValueKeccak.Compute(BitConverter.GetBytes(index));

    /// <summary>Values cycling from 1 byte up to the full stride, so both ends of the copy path are covered.</summary>
    private static byte[] MultiGetValue(int index)
    {
        byte[] value = new byte[(index % MultiGetValueStride) + 1];
        for (int i = 0; i < value.Length; i++) value[i] = (byte)(index + i);
        return value;
    }

    private static void WriteMultiGetKeys(IDb column, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (i % 3 != 2) column.Set(MultiGetKey(i).Bytes, MultiGetValue(i)); // every third key stays absent
        }
    }

    /// <summary>Batch-reads <paramref name="keys"/> and asserts each result against the single-key read.</summary>
    private static void AssertMultiGetMatchesPerKey(IBatchedReadOnlyKeyValueStore batched, IReadOnlyKeyValueStore perKey, ValueHash256[] keys)
    {
        byte[] keyBytes = new byte[keys.Length * MultiGetKeyLength];
        for (int i = 0; i < keys.Length; i++) keys[i].Bytes.CopyTo(keyBytes.AsSpan(i * MultiGetKeyLength));

        byte[] values = new byte[keys.Length * MultiGetValueStride];
        int[] lengths = new int[keys.Length];
        batched.MultiGet(keyBytes, MultiGetKeyLength, values, MultiGetValueStride, lengths);

        byte[] expected = new byte[MultiGetValueStride];
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < keys.Length; i++)
            {
                int expectedLength = perKey.Get(keys[i].Bytes, expected);
                Assert.That(lengths[i], Is.EqualTo(expectedLength == 0 ? -1 : expectedLength), $"key {i} length");
                if (expectedLength > 0)
                {
                    Assert.That(values.AsSpan(i * MultiGetValueStride, expectedLength).ToArray(),
                        Is.EqualTo(expected.AsSpan(0, expectedLength).ToArray()), $"key {i} value");
                }
            }
        }
    }

    [TestCase(1)]
    [TestCase(31)]
    [TestCase(32)]
    [TestCase(33)]
    [TestCase(1000)]
    public void MultiGet_MixedHitsAndMisses_MatchesPerKeyGet(int count)
    {
        IDb column = _db.GetColumnDb(ReceiptsColumns.Blocks);
        WriteMultiGetKeys(column, count);

        ValueHash256[] keys = new ValueHash256[count];
        for (int i = 0; i < count; i++) keys[i] = MultiGetKey(i);

        AssertMultiGetMatchesPerKey((IBatchedReadOnlyKeyValueStore)column, column, keys);
    }

    // sorted_input is false, so the batch must tolerate any key order — including descending comparator
    // order — and repeated keys, which produce one independent slice each.
    [Test]
    public void MultiGet_UnsortedAndDuplicateKeys_MatchPerKeyGet()
    {
        const int count = 64;
        IDb column = _db.GetColumnDb(ReceiptsColumns.Blocks);
        WriteMultiGetKeys(column, count);

        ValueHash256[] descending = new ValueHash256[count];
        for (int i = 0; i < count; i++) descending[i] = MultiGetKey(i);
        Array.Sort(descending);
        Array.Reverse(descending);
        AssertMultiGetMatchesPerKey((IBatchedReadOnlyKeyValueStore)column, column, descending);

        // Same key repeated, mixing a present and an absent one.
        ValueHash256[] duplicates = new ValueHash256[8];
        for (int i = 0; i < duplicates.Length; i++) duplicates[i] = MultiGetKey(i % 2 == 0 ? 0 : 2);
        AssertMultiGetMatchesPerKey((IBatchedReadOnlyKeyValueStore)column, column, duplicates);
    }

    [Test]
    public void MultiGet_OnSnapshot_ReadsTheSnapshotView()
    {
        IColumnsDb<ReceiptsColumns> asColumnsDb = _db;
        IDb column = _db.GetColumnDb(ReceiptsColumns.Blocks);

        ValueHash256 key = MultiGetKey(0);
        byte[] before = MultiGetValue(1);
        column.Set(key.Bytes, before);

        using IColumnDbSnapshot<ReceiptsColumns> snapshot = asColumnsDb.CreateSnapshot();
        IReadOnlyKeyValueStore snapshotColumn = snapshot.GetColumn(ReceiptsColumns.Blocks);

        column.Set(key.Bytes, MultiGetValue(7));

        byte[] values = new byte[MultiGetValueStride];
        int[] lengths = new int[1];
        ((IBatchedReadOnlyKeyValueStore)snapshotColumn).MultiGet(key.Bytes, MultiGetKeyLength, values, MultiGetValueStride, lengths);

        Assert.That(lengths[0], Is.EqualTo(before.Length));
        Assert.That(values.AsSpan(0, before.Length).ToArray(), Is.EqualTo(before));
    }

    [Test]
    public void MultiGet_OversizeValue_Throws()
    {
        IDb column = _db.GetColumnDb(ReceiptsColumns.Blocks);
        const int stride = 8;

        ValueHash256 small = MultiGetKey(0);
        ValueHash256 big = MultiGetKey(1);
        column.Set(small.Bytes, MultiGetValue(0));
        column.Set(big.Bytes, new byte[stride + 1]);

        byte[] keyBytes = new byte[2 * MultiGetKeyLength];
        small.Bytes.CopyTo(keyBytes.AsSpan());
        big.Bytes.CopyTo(keyBytes.AsSpan(MultiGetKeyLength));

        Assert.That(
            () => ((IBatchedReadOnlyKeyValueStore)column).MultiGet(keyBytes, MultiGetKeyLength, new byte[2 * stride], stride, new int[2]),
            Throws.InstanceOf<ArgumentException>());

        // Every slice from the failed batch must still have been destroyed, so reads keep working.
        AssertMultiGetMatchesPerKey((IBatchedReadOnlyKeyValueStore)column, column, [small]);
    }

    [Test]
    public void MultiGet_EmptyBatch_IsANoOp()
    {
        IDb column = _db.GetColumnDb(ReceiptsColumns.Blocks);

        Assert.That(
            () => ((IBatchedReadOnlyKeyValueStore)column).MultiGet([], MultiGetKeyLength, [], MultiGetValueStride, []),
            Throws.Nothing);
    }

    [Test]
    public void Flush_MaterializesNamedColumnFamilies_SurvivingReopen()
    {
        // Regression: a DisableWAL write to a NAMED column has no WAL entry, so it is only durable if
        // Flush() materializes that column family's memtable into SST. Before the fix, ColumnsDb.Flush()
        // flushed only the WAL and the default column family, so this write was lost after a reopen.
        byte[] value = TestItem.KeccakA.BytesToArray();
        _db.GetColumnDb(ReceiptsColumns.Blocks).Set(TestItem.KeccakA.Bytes, value, WriteFlags.DisableWAL);

        _db.Flush();
        _db.Dispose();

        // Reopen the same on-disk DB (no DeleteOnStart) — the value must survive.
        _db = new ColumnsDb<ReceiptsColumns>(DbPath,
            new("Blocks", DbPath),
            new DbConfig(),
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            LimboLogs.Instance,
            Enum.GetValues<ReceiptsColumns>());

        Assert.That(_db.GetColumnDb(ReceiptsColumns.Blocks).Get(TestItem.KeccakA), Is.EqualTo(value));
    }
}
