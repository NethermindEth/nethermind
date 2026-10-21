// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
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
    // RocksDB appends temporary filenames; test names can exceed Windows path limits.
    string DbPath => "testdb/" + TestContext.CurrentContext.Test.ID;
    private ColumnsDb<ReceiptsColumns> _db = null!;

    [SetUp]
    public void Setup()
    {
        if (Directory.Exists(DbPath))
        {
            Directory.Delete(DbPath, true);
        }

        Directory.CreateDirectory(DbPath);
        _db = CreateDb(DbPath, new DbConfig());
    }

    private static ColumnsDb<ReceiptsColumns> CreateDb(string path, DbConfig dbConfig, IRocksDbConfigFactory? factory = null) =>
        new(path,
            new("Blocks", path)
            {
                DeleteOnStart = true,
            },
            dbConfig,
            factory ?? new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            LimboLogs.Instance,
            Enum.GetValues<ReceiptsColumns>()
        );

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public void Snapshots_and_views_do_not_resolve_read_settings_again([Values] bool sequentialReadAhead)
    {
        DbConfig config = new();
        CountingRocksDbConfigFactory factory = new(new RocksDbConfigFactory(config, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false));
        using ColumnsDb<ReceiptsColumns> db = CreateDb(DbPath + "-read-settings", config, factory);
        IDb column = db.GetColumnDb(ReceiptsColumns.Blocks);
        column.Set([1], [2]);
        int checksumReads = factory.TableConfig.VerifyChecksumReads;
        int readAheadReads = factory.TableConfig.ReadAheadSizeReads;

        for (int i = 0; i < 3; i++)
        {
            using IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)db).CreateSnapshot(sequentialReadAhead);
            IReadOnlyKeyValueStore snapshotColumn = snapshot.GetColumn(ReceiptsColumns.Blocks);
            Assert.That(snapshotColumn.Get([1], ReadFlags.HintReadAhead), Is.EqualTo(new byte[] { 2 }));
            AssertView((ISortedKeyValueStore)snapshotColumn);
            AssertView((ISortedKeyValueStore)column);
            using IKeyValueStoreSnapshot columnSnapshot = ((ColumnDb)column).CreateSnapshot();
            AssertView((ISortedKeyValueStore)columnSnapshot);
            using IKeyValueStoreSnapshot tableSnapshot = ((DbOnTheRocks)db).CreateSnapshot();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.TableConfig.VerifyChecksumReads, Is.EqualTo(checksumReads));
            Assert.That(factory.TableConfig.ReadAheadSizeReads, Is.EqualTo(readAheadReads));
            Assert.That(factory.VerifyChecksumReadsBeforeColumns, Is.GreaterThan(0), "Init must see the resolved checksum setting");
        }

        static void AssertView(ISortedKeyValueStore store)
        {
            using ISortedView view = store.GetViewBetween([1], [3], ReadFlags.HintReadAhead);
            Assert.That(view.MoveNext(), Is.True);
            Assert.That(view.CurrentValue, Is.SequenceEqualTo(new byte[] { 2 }));
        }
    }

    [Test]
    public void Read_settings_are_fixed_while_open_and_resolved_for_new_instances()
    {
        string path = DbPath + "-read-settings";
        DbConfig config = new();
        using (ColumnsDb<ReceiptsColumns> db = CreateDb(path, config))
        {
            config.VerifyChecksum = false;
            config.ReadAheadSize = 0;
            AssertReadSettings(db, true, 256 * 1024);
        }

        using ColumnsDb<ReceiptsColumns> reopened = CreateDb(path, config);
        AssertReadSettings(reopened, false, 0);

        static void AssertReadSettings(ColumnsDb<ReceiptsColumns> db, bool checksum, ulong readAhead)
        {
            using IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)db).CreateSnapshot(sequentialReadAhead: true);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(db.VerifyChecksum, Is.EqualTo(checksum));
                Assert.That(db.ReadAheadSize, Is.EqualTo(readAhead));
                Assert.That(((RocksDbReader)snapshot.GetColumn(ReceiptsColumns.Blocks)).IteratorManager is not null, Is.EqualTo(readAhead > 0));
            }
        }
    }

    private sealed class CountingRocksDbConfigFactory(IRocksDbConfigFactory inner) : IRocksDbConfigFactory
    {
        public CountingRocksDbConfig TableConfig { get; private set; } = null!;
        public int? VerifyChecksumReadsBeforeColumns { get; private set; }

        public IRocksDbConfig GetForDatabase(string databaseName, string? columnName)
        {
            IRocksDbConfig config = inner.GetForDatabase(databaseName, columnName);
            if (columnName is null) return TableConfig = new CountingRocksDbConfig(config);
            VerifyChecksumReadsBeforeColumns ??= TableConfig.VerifyChecksumReads;
            return config;
        }
    }

    private sealed class CountingRocksDbConfig(IRocksDbConfig inner) : IRocksDbConfig
    {
        public int VerifyChecksumReads { get; private set; }
        public int ReadAheadSizeReads { get; private set; }
        public bool? VerifyChecksum { get { VerifyChecksumReads++; return inner.VerifyChecksum; } }
        public ulong? ReadAheadSize { get { ReadAheadSizeReads++; return inner.ReadAheadSize; } }
        public ulong? WriteBufferSize => inner.WriteBufferSize;
        public ulong? WriteBufferNumber => inner.WriteBufferNumber;
        public string RocksDbOptions => inner.RocksDbOptions;
        public string AdditionalRocksDbOptions => inner.AdditionalRocksDbOptions;
        public int? MaxOpenFiles => inner.MaxOpenFiles;
        public bool WriteAheadLogSync => inner.WriteAheadLogSync;
        public bool EnableDbStatistics => inner.EnableDbStatistics;
        public uint StatsDumpPeriodSec => inner.StatsDumpPeriodSec;
        public ulong? RowCacheSize => inner.RowCacheSize;
        public bool EnableFileWarmer => inner.EnableFileWarmer;
        public double CompressibilityHint => inner.CompressibilityHint;
        public FlushOnExitMode FlushOnExit => inner.FlushOnExit;
        public nint? BlockCache => inner.BlockCache;
        public bool CompactOnDeletions => inner.CompactOnDeletions;
    }

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
    public void WriteBatch_PutSpan_DoesNotCopyValueToManagedArray()
    {
        const int valueLength = 128 * 1024;
        byte[] value = GC.AllocateUninitializedArray<byte>(valueLength);
        long allocated;
        using (IColumnsWriteBatch<ReceiptsColumns> batch = _db.StartWriteBatch())
        {
            IWriteBatch column = batch.GetColumnBatch(ReceiptsColumns.Blocks);
            column.PutSpan(TestItem.KeccakA.Bytes, [1]);

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            column.PutSpan(TestItem.KeccakB.Bytes, value);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocated, Is.LessThan(valueLength));
            Assert.That(_db.GetColumnDb(ReceiptsColumns.Blocks).Get(TestItem.KeccakB), Is.EqualTo(value));
            Assert.That(_db.GetColumnDb(ReceiptsColumns.Transactions).Get(TestItem.KeccakB), Is.Null);
        }
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

    [TestCase(3)]
    [TestCase(8)]
    [TestCase(28)]
    public void Snapshot_Get_WithHintReadAhead_ReadsFromSnapshot(int shortKeyLength)
    {
        IDb colA = _db.GetColumnDb(ReceiptsColumns.Blocks);

        // Realistic flat-layout key shapes: a run of short keys followed by a run of long
        // (34-byte) keys, ascending overall. Short keys pin the sequential-keys bypass of the
        // "probably hash db" length guard on the iterator fast path.
        byte[][] keys = new byte[64][];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = new byte[i < 32 ? shortKeyLength : 34];
            keys[i][0] = i < 32 ? (byte)0x10 : (byte)0x22;
            keys[i][^1] = (byte)i;
            colA.Set(keys[i], [(byte)i, 1]);
        }

        IColumnsDb<ReceiptsColumns> asColumnsDb = _db;
        using (IColumnDbSnapshot<ReceiptsColumns> snapshot = asColumnsDb.CreateSnapshot(sequentialReadAhead: true))
        {
            // Mutate after the snapshot: overwrite even keys, delete odd keys.
            for (int i = 0; i < keys.Length; i++)
            {
                colA.Set(keys[i], i % 2 == 0 ? [(byte)i, 2] : null);
            }

            IReadOnlyKeyValueStore snapshotColumn = snapshot.GetColumn(ReceiptsColumns.Blocks);
            Assert.That(((RocksDbReader)snapshotColumn).IteratorManager, Is.Not.Null,
                "opt-in snapshot readers must be wired for the readahead iterator path");

            // Ascending key order exercises the readahead iterator's forward-scan (Next) fast path;
            // values must still come from the snapshot, not the mutated head state.
            for (int i = 0; i < keys.Length; i++)
            {
                Assert.That(snapshotColumn.Get(keys[i], ReadFlags.HintReadAhead), Is.EqualTo(new byte[] { (byte)i, 1 }), $"key {i}");
            }

            // Probes past the last key (exhausts the iterator) and misses.
            byte[] missingKey = new byte[34];
            missingKey[0] = 0x23;
            Assert.That(snapshotColumn.Get(missingKey, ReadFlags.HintReadAhead), Is.Null);
            for (int i = keys.Length - 1; i >= 0; i--)
            {
                Assert.That(snapshotColumn.Get(keys[i], ReadFlags.HintReadAhead), Is.EqualTo(new byte[] { (byte)i, 1 }), $"reverse key {i}");
            }
            Assert.That(snapshotColumn.Get(keys[0], ReadFlags.HintReadAhead), Is.EqualTo(new byte[] { 0, 1 }));
        }

        // Disposing the snapshot tears down its iterators; the head db must stay fully usable.
        Assert.That(colA.Get(keys[0]), Is.EqualTo(new byte[] { 0, 2 }));
    }

    [Test]
    public void Snapshot_Default_HasNoIteratorManager()
    {
        IColumnsDb<ReceiptsColumns> asColumnsDb = _db;
        using IColumnDbSnapshot<ReceiptsColumns> snapshot = asColumnsDb.CreateSnapshot();

        Assert.That(((RocksDbReader)snapshot.GetColumn(ReceiptsColumns.Blocks)).IteratorManager, Is.Null,
            "snapshots without the sequential-read-ahead opt-in must keep point-Get behavior for HintReadAhead");
    }

    [Test]
    public void Snapshot_SequentialReadAhead_WithReadAheadDisabled_HasNoIteratorManager()
    {
        string path = DbPath + "-no-readahead";
        Directory.CreateDirectory(path);
        using ColumnsDb<ReceiptsColumns> db = CreateDb(path, new DbConfig { ReadAheadSize = 0 });
        using IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)db).CreateSnapshot(sequentialReadAhead: true);

        Assert.That(((RocksDbReader)snapshot.GetColumn(ReceiptsColumns.Blocks)).IteratorManager, Is.Null,
            "ReadAheadSize = 0 must opt snapshots out of readahead iterators, as it does for DbOnTheRocks");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Snapshot_ReadAhead_ConcurrentColumnsKeepIndependentSnapshotValues(bool flush)
    {
        ReceiptsColumns[] columns = Enum.GetValues<ReceiptsColumns>();
        for (int c = 0; c < columns.Length; c++)
        {
            IDb column = _db.GetColumnDb(columns[c]);
            for (int i = 0; i < 64; i++) column.Set([(byte)i, 0, 0], [(byte)c, (byte)i]);
        }
        if (flush) _db.Flush();
        using IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)_db).CreateSnapshot(sequentialReadAhead: true);
        for (int c = 0; c < columns.Length; c++)
        {
            IDb column = _db.GetColumnDb(columns[c]);
            for (int i = 0; i < 64; i++) column.Set([(byte)i, 0, 0], null);
        }

        Parallel.For(0, 16, worker =>
        {
            int c = worker % columns.Length;
            IReadOnlyKeyValueStore column = snapshot.GetColumn(columns[c]);
            for (int i = 0; i < 64; i++)
            {
                Assert.That(column.Get([(byte)i, 0, 0], ReadFlags.HintReadAhead), Is.EqualTo(new byte[] { (byte)c, (byte)i }));
            }
            Assert.That(column.Get([255, 0, 0], ReadFlags.HintReadAhead), Is.Null);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Snapshot_DoubleDispose_DoesNotThrow(bool sequentialReadAhead)
    {
        IColumnsDb<ReceiptsColumns> asColumnsDb = _db;
        IColumnDbSnapshot<ReceiptsColumns> snapshot = asColumnsDb.CreateSnapshot(sequentialReadAhead);

        snapshot.Dispose();

        Assert.That(() => snapshot.Dispose(), Throws.Nothing);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Snapshot_GetColumn_AfterDispose_ThrowsObjectDisposedException(bool sequentialReadAhead)
    {
        IColumnsDb<ReceiptsColumns> asColumnsDb = _db;
        IColumnDbSnapshot<ReceiptsColumns> snapshot = asColumnsDb.CreateSnapshot(sequentialReadAhead);

        snapshot.Dispose();

        Assert.That(() => snapshot.GetColumn(ReceiptsColumns.Blocks), Throws.TypeOf<ObjectDisposedException>());
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
