// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Modules;
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
        TestRocksDbFactory.OpenColumnsDb<ReceiptsColumns>(path,
            new("Blocks", path)
            {
                DeleteOnStart = true,
            },
            dbConfig,
            factory ?? new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false)
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
        Assert.That(checksumReads, Is.GreaterThan(0), "Opening must resolve the checksum setting");
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
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.TableConfig.VerifyChecksumReads, Is.EqualTo(checksumReads));
            Assert.That(factory.TableConfig.ReadAheadSizeReads, Is.EqualTo(readAheadReads));
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

        public IRocksDbConfig GetForDatabase(string? databaseName, string? columnName)
        {
            IRocksDbConfig config = inner.GetForDatabase(databaseName, columnName);
            return columnName is null ? TableConfig = new CountingRocksDbConfig(config) : config;
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
    public void Snapshot_prefetch_starts_one_batch_and_leaves_values_unchanged([Values] bool disposeSnapshotFirst)
    {
        const int count = 100;
        const int keyLength = 8;
        IDb column = _db.GetColumnDb(ReceiptsColumns.Blocks);
        byte[] keys = new byte[count * keyLength];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteInt64BigEndian(keys.AsSpan(i * keyLength, keyLength), i * 31L);
            if (i % 4 != 3) column.Set(keys.AsSpan(i * keyLength, keyLength), [(byte)i, 1]);
        }
        _db.Flush();
        long before = Metrics.PrefetchedKeys;

        using IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)_db).CreateSnapshot();
        IReadPrefetcher prefetcher = (IReadPrefetcher)snapshot.GetColumn(ReceiptsColumns.Blocks);
        prefetcher.Prefetch(keys, keyLength);
        if (disposeSnapshotFirst) snapshot.Dispose();

        Assert.That(Metrics.PrefetchedKeys - before, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            byte[]? value = column.Get(keys.AsSpan(i * keyLength, keyLength));
            Assert.That(value, i % 4 == 3 ? Is.Null : Is.EqualTo(new byte[] { (byte)i, 1 }));
        }

        if (!disposeSnapshotFirst) snapshot.Dispose();
        Assert.DoesNotThrow(() => prefetcher.Prefetch(keys, 0));
    }

    [Test]
    public void Snapshot_dispose_waits_for_a_pinned_read_in_flight([Values] bool hintCacheMiss)
    {
        byte[] key = [1, 2, 3];
        _db.GetColumnDb(ReceiptsColumns.Blocks).PutSpan(key, new byte[64], WriteFlags.None);
        IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)_db).CreateSnapshot();
        IReadOnlyKeyValueStore column = snapshot.GetColumn(ReceiptsColumns.Blocks);
        using DbOnTheRocksTests.ReadGate gate = new();
        Task reading = gate.Read(column, key, hintCacheMiss ? ReadFlags.HintCacheMiss : ReadFlags.None);

        Task disposing = Task.Run(snapshot.Dispose);
        Assert.That(disposing.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "the snapshot was disposed under a pinned read");

        gate.Release();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading.Wait(TimeSpan.FromSeconds(10)), "the read never finished");
            Assert.That(disposing.Wait(TimeSpan.FromSeconds(10)), "dispose never finished after the read ended");
            Assert.That(() => column.TryGet(key, ReadFlags.None, static (_, _) => { }, 0), Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    [Test]
    public void Snapshot_dispose_under_concurrent_pinned_reads_lets_each_read_finish_or_throw_object_disposed()
    {
        const int Readers = 8;
        byte[] key = [1, 2, 3];
        ReceiptsColumns[] columns = Enum.GetValues<ReceiptsColumns>();
        foreach (ReceiptsColumns column in columns) _db.GetColumnDb(column).PutSpan(key, new byte[64], WriteFlags.None);
        IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)_db).CreateSnapshot();

        using CountdownEvent started = new(Readers);
        Task<int>[] readers = new Task<int>[Readers];
        for (int i = 0; i < Readers; i++)
        {
            IReadOnlyKeyValueStore column = snapshot.GetColumn(columns[i % columns.Length]);
            readers[i] = Task.Factory.StartNew(() =>
            {
                started.Signal();
                int reads = 0;
                try
                {
                    while (true)
                    {
                        if (!column.TryGet(key, ReadFlags.None, DbOnTheRocksTests.CheckValueLength, 64))
                        {
                            throw new InvalidOperationException("the key was not found");
                        }

                        reads++;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return reads;
                }
            }, TaskCreationOptions.LongRunning);
        }

        Assert.That(started.Wait(TimeSpan.FromSeconds(10)), "the readers never started");
        Thread.Sleep(50);
        snapshot.Dispose();

        Assert.That(Task.WaitAll(readers, TimeSpan.FromSeconds(10)), "a reader never stopped");
        int reads = readers.Sum(static r => r.Result);
        Assert.That(reads, Is.GreaterThan(0));
    }

    [Test]
    public void Snapshot_disposed_inside_its_own_read_releases_the_snapshot_and_iterator_managers_when_the_read_ends()
    {
        byte[] key = [1, 2, 3];
        _db.GetColumnDb(ReceiptsColumns.Blocks).PutSpan(key, new byte[64], WriteFlags.None);
        DbOnTheRocks column = (DbOnTheRocks)_db.GetColumnDb(ReceiptsColumns.Blocks);
        IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)_db).CreateSnapshot(sequentialReadAhead: true);
        IReadOnlyKeyValueStore blocks = snapshot.GetColumn(ReceiptsColumns.Blocks);
        RocksDbReader transactions = (RocksDbReader)snapshot.GetColumn(ReceiptsColumns.Transactions);
        ulong snapshotsInRead = 0;

        blocks.TryGet(key, ReadFlags.None, (_, _) =>
        {
            snapshot.Dispose();
            snapshotsInRead = DbOnTheRocksTests.LiveSnapshots(column);
        }, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshotsInRead, Is.EqualTo(1), "the snapshot was released under its own read");
            Assert.That(DbOnTheRocksTests.LiveSnapshots(column), Is.Zero, "the read did not release the snapshot when it ended");
            Assert.That(() => transactions.IteratorManager!.Value, Throws.InstanceOf<ObjectDisposedException>(), "the iterator managers were not released");
        }
    }

    [Test]
    public void Prefetch_ignores_a_trailing_partial_key()
    {
        const int count = 10;
        const int keyLength = 8;
        byte[] keys = new byte[count * keyLength + 3];
        for (int i = 0; i < count; i++) BinaryPrimitives.WriteInt64BigEndian(keys.AsSpan(i * keyLength, keyLength), i);
        long before = Metrics.PrefetchedKeys;

        using IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)_db).CreateSnapshot();
        IReadPrefetcher prefetcher = (IReadPrefetcher)snapshot.GetColumn(ReceiptsColumns.Blocks);

        Assert.DoesNotThrow(() => prefetcher.Prefetch(keys, keyLength));
        Assert.That(Metrics.PrefetchedKeys - before, Is.EqualTo(count));
    }

    [Test]
    public void Column_prefetch_reads_ahead_on_Linux_only()
    {
        const int count = 10;
        const int keyLength = 8;
        byte[] keys = new byte[count * keyLength];
        for (int i = 0; i < count; i++) BinaryPrimitives.WriteInt64BigEndian(keys.AsSpan(i * keyLength, keyLength), i);
        long before = Metrics.PrefetchedKeys;

        ((IReadPrefetcher)_db.GetColumnDb(ReceiptsColumns.Blocks)).Prefetch(keys, keyLength);

        Assert.That(Metrics.PrefetchedKeys - before, Is.EqualTo(OperatingSystem.IsLinux() ? count : 0));
    }

    [Test]
    public void Snapshot_cache_probe_reports_disk_reads_as_not_cached()
    {
        string path = DbPath + "-cache-probe";
        byte[] key = [1, 2, 3, 4, 5, 6, 7, 8];
        using (ColumnsDb<ReceiptsColumns> db = CreateDb(path, new DbConfig()))
        {
            db.GetColumnDb(ReceiptsColumns.Blocks).Set(key, [9, 9]);
            db.Flush();
        }

        using IContainer container = CreateConfigContainer(path);
        using ColumnsDb<ReceiptsColumns> reopened = Reopen(path, container);
        using IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)reopened).CreateSnapshot();
        IReadPrefetcher prefetcher = (IReadPrefetcher)snapshot.GetColumn(ReceiptsColumns.Blocks);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(prefetcher.IsCached(key), Is.False);
            Assert.That(reopened.GetColumnDb(ReceiptsColumns.Blocks).Get(key), Is.EqualTo(new byte[] { 9, 9 }));
        }
        Assert.That(prefetcher.IsCached(key), Is.True);
    }

    [Test]
    public void Snapshot_prefetch_reads_every_key_when_batches_of_several_lengths_and_sizes_share_a_thread()
    {
        string path = DbPath + "-prefetch-batches";
        // One key per data block, so a key is cached only when the prefetch read that exact key.
        byte[] value = new byte[64 * 1024];
        int[] batchSizes = [64, 3, 64, 1, 17];
        byte[][] blockKeys = CreateKeys(batchSizes, keyLength: 8, seed: 1);
        byte[][] transactionKeys = CreateKeys(batchSizes, keyLength: 32, seed: 2);
        using (ColumnsDb<ReceiptsColumns> db = CreateDb(path, new DbConfig()))
        {
            foreach (byte[] key in blockKeys) db.GetColumnDb(ReceiptsColumns.Blocks).Set(key, value);
            foreach (byte[] key in transactionKeys) db.GetColumnDb(ReceiptsColumns.Transactions).Set(key, value);
            db.Flush();
        }

        using IContainer container = CreateConfigContainer(path);
        using ColumnsDb<ReceiptsColumns> reopened = Reopen(path, container);
        using IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)reopened).CreateSnapshot();
        IReadPrefetcher blocks = (IReadPrefetcher)snapshot.GetColumn(ReceiptsColumns.Blocks);
        IReadPrefetcher transactions = (IReadPrefetcher)snapshot.GetColumn(ReceiptsColumns.Transactions);
        Assert.That(blockKeys.Concat(transactionKeys).Count(key => KeyIsCached(key)), Is.Zero);

        int start = 0;
        foreach (int size in batchSizes)
        {
            blocks.Prefetch(Concat(blockKeys, start, size), 8);
            transactions.Prefetch(Concat(transactionKeys, start, size), 32);
            start += size;
        }

        Assert.That(() => blockKeys.All(key => blocks.IsCached(key)) && transactionKeys.All(key => transactions.IsCached(key)),
            Is.True.After(10_000, 20));

        bool KeyIsCached(byte[] key) => key.Length == 8 ? blocks.IsCached(key) : transactions.IsCached(key);

        static byte[][] CreateKeys(int[] batchSizes, int keyLength, int seed)
        {
            Random random = new(seed);
            byte[][] keys = new byte[batchSizes.Sum()][];
            for (int i = 0; i < keys.Length; i++) random.NextBytes(keys[i] = new byte[keyLength]);
            return keys;
        }

        static byte[] Concat(byte[][] keys, int start, int count) => keys.Skip(start).Take(count).SelectMany(static key => key).ToArray();
    }

    [Test]
    public void Dispose_waits_for_prefetches_in_flight_and_refuses_new_ones()
    {
        const int count = 64;
        const int keyLength = 8;
        string path = DbPath + "-prefetch-dispose";
        // One key per data block, so the prefetch after the reopen reads every block from disk.
        byte[] value = new byte[64 * 1024];
        byte[] keys = new byte[count * keyLength];
        new Random(3).NextBytes(keys);
        using (ColumnsDb<ReceiptsColumns> db = CreateDb(path, new DbConfig()))
        {
            for (int i = 0; i < count; i++) db.GetColumnDb(ReceiptsColumns.Blocks).Set(keys.AsSpan(i * keyLength, keyLength), value);
            db.Flush();
        }

        using IContainer container = CreateConfigContainer(path);
        // Disposed explicitly below; the declaration also closes it when an earlier step throws.
        using ColumnsDb<ReceiptsColumns> reopened = Reopen(path, container);
        using IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)reopened).CreateSnapshot();
        IReadPrefetcher prefetcher = (IReadPrefetcher)snapshot.GetColumn(ReceiptsColumns.Blocks);
        for (int i = 0; i < 8; i++) prefetcher.Prefetch(keys, keyLength);

        reopened.Dispose();
        long before = Metrics.PrefetchedKeys;
        prefetcher.Prefetch(keys, keyLength);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reopened.PrefetchesInFlight, Is.Zero);
            Assert.That(Metrics.PrefetchedKeys, Is.EqualTo(before));
        }
    }

    private static IContainer CreateConfigContainer(string path)
    {
        InitConfig initConfig = new() { BaseDbPath = path };
        return new ContainerBuilder()
            .AddModule(new DbModule(initConfig, new ReceiptConfig(), new SyncConfig()))
            // Large enough for every block a prefetch test reads, so no prefetched block is evicted before it is checked.
            .AddSingleton<IDbConfig>(new DbConfig { ReceiptsDbAdditionalRocksDbOptions = "block_based_table_factory.block_cache=64000000;" })
            .AddSingleton<IInitConfig>(initConfig)
            .AddSingleton<IPruningConfig>(new PruningConfig())
            .AddSingleton<IHardwareInfo>(new TestHardwareInfo())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .Build();
    }

    private static ColumnsDb<ReceiptsColumns> Reopen(string path, IContainer container) =>
        TestRocksDbFactory.OpenColumnsDb<ReceiptsColumns>(path, new(nameof(DbNames.Receipts), path), container.Resolve<IDbConfig>(),
            container.Resolve<IRocksDbConfigFactory>());

    [Test]
    public void SmokeTest()
    {
        IDb colA = _db.GetColumnDb(ReceiptsColumns.Blocks);
        IDb colB = _db.GetColumnDb(ReceiptsColumns.Transactions);
        IDb defaultCol = _db.GetColumnDb(ReceiptsColumns.Default);

        colA.Set(TestItem.KeccakA, TestItem.KeccakA.BytesToArray());
        colB.Set(TestItem.KeccakA, TestItem.KeccakB.BytesToArray());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(colA.Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
            Assert.That(colB.Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakB.BytesToArray()));

            Assert.That(defaultCol.Get(TestItem.KeccakB), Is.Null);
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(defaultCol.Get(TestItem.KeccakB), Is.EqualTo(TestItem.KeccakC.BytesToArray()));

            // "Default" is RocksDB's own default column family, where data written without a column lives.
            Assert.That(defaultCol.Name, Is.EqualTo("default"));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_db.GetColumnDb(ReceiptsColumns.Blocks).Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
            Assert.That(_db.GetColumnDb(ReceiptsColumns.Transactions).Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakB.BytesToArray()));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(colA.Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakB.BytesToArray()));

            Assert.That(snapshot.GetColumn(ReceiptsColumns.Blocks)
                .Get(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
        }
    }

    [Test]
    public void Snapshot_Get_WithHintReadAhead_ReadsFromSnapshot([Values(3, 8, 28)] int shortKeyLength, [Values] bool spanAction)
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
                Assert.That(KeyValueStoreReads.Read(snapshotColumn, keys[i], ReadFlags.HintReadAhead, spanAction), Is.EqualTo(new byte[] { (byte)i, 1 }), $"key {i}");
            }

            // Probes past the last key (exhausts the iterator) and misses.
            byte[] missingKey = new byte[34];
            missingKey[0] = 0x23;
            Assert.That(KeyValueStoreReads.Read(snapshotColumn, missingKey, ReadFlags.HintReadAhead, spanAction), Is.Null);
            for (int i = keys.Length - 1; i >= 0; i--)
            {
                Assert.That(KeyValueStoreReads.Read(snapshotColumn, keys[i], ReadFlags.HintReadAhead, spanAction), Is.EqualTo(new byte[] { (byte)i, 1 }), $"reverse key {i}");
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(KeyValueStoreReads.Read(snapshotColumn, keys[0], ReadFlags.HintReadAhead, spanAction), Is.EqualTo(new byte[] { 0, 1 }));

                // Skip-ahead reads. The iterator is on keys[0]. A key further away than the five steps of its probe
                // is found by a seek.
                Assert.That(KeyValueStoreReads.Read(snapshotColumn, keys[8], ReadFlags.HintReadAhead, spanAction), Is.EqualTo(new byte[] { 8, 1 }), "key 8 from key 0");
            }

            // From here the iterator is on keys[i]. Next lands on keys[i + 1], before the requested key, so the
            // bounded probe takes over. It steps past a key that is absent between keys[i + 1] and keys[i + 2] and
            // settles the miss without a point read. The read of keys[i + 2], where it stopped, steps past it, so a
            // seek serves that key, and keys[i + 4] is found by the probe. Several rounds, so one read that the
            // cleanup timer of the iterator pool sends to a seek cannot hide the probe branches.
            for (int i = 8; i + 4 < keys.Length; i += 4)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(KeyValueStoreReads.Read(snapshotColumn, [.. keys[i + 1], 0], ReadFlags.HintReadAhead, spanAction), Is.Null, $"absent key after key {i + 1}");
                    Assert.That(KeyValueStoreReads.Read(snapshotColumn, keys[i + 2], ReadFlags.HintReadAhead, spanAction), Is.EqualTo(new byte[] { (byte)(i + 2), 1 }), $"key {i + 2} after the miss");
                    Assert.That(KeyValueStoreReads.Read(snapshotColumn, keys[i + 4], ReadFlags.HintReadAhead, spanAction), Is.EqualTo(new byte[] { (byte)(i + 4), 1 }), $"key {i + 4} after a skip");
                }
            }
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

    [Test]
    public void Snapshot_ReadAhead_DoesNotFillBlockCache([Values] bool sequentialReadAhead, [Values] bool spanAction)
    {
        const int keyCount = 4096;
        const int valueSize = 256;
        ColumnDb column = (ColumnDb)_db.GetColumnDb(ReceiptsColumns.Blocks);
        Random random = new(42);
        byte[][] values = new byte[keyCount][];
        for (int i = 0; i < keyCount; i++)
        {
            values[i] = new byte[valueSize];
            random.NextBytes(values[i]);
            column.Set(Key(i), values[i]);
        }

        _db.Flush();
        long usageBefore = BlockCacheUsage(column);

        using (IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)_db).CreateSnapshot(sequentialReadAhead))
        {
            IReadOnlyKeyValueStore snapshotColumn = snapshot.GetColumn(ReceiptsColumns.Blocks);
            for (int i = 0; i < keyCount; i++)
            {
                Assert.That(KeyValueStoreReads.Read(snapshotColumn, Key(i), ReadFlags.HintReadAhead, spanAction), Is.EqualTo(values[i]));
            }
        }

        long added = BlockCacheUsage(column) - usageBefore;
        Assert.That(added, sequentialReadAhead ? Is.LessThan(keyCount * valueSize / 10) : Is.GreaterThan(keyCount * valueSize / 2));
    }

    [Test]
    public void Snapshot_ReadAhead_Misses_FillBlockCacheOnlyThroughFallbackWithoutHintCacheMiss(
        [Values] bool probe, [Values] bool hintCacheMiss, [Values] bool spanAction)
    {
        const int keyCount = 4096;
        const int valueSize = 256;
        const int storedBytes = keyCount / 4 * 3 * valueSize;
        // The bloom filter is skipped on the last level (optimize_filters_for_hits), here the one file the flush
        // writes, so a point read of an absent key has to read the data block that would hold it. With the filter
        // consulted almost no miss does, and the bounds below could not tell the cases apart. No automatic
        // compaction either, so nothing in the background moves blocks in or out of the cache.
        using ColumnsDb<ReceiptsColumns> db = CreateDb(DbPath + "-misses", new DbConfig
        {
            BlocksDbAdditionalRocksDbOptions = "optimize_filters_for_hits=true;disable_auto_compactions=true;"
        });
        ColumnDb column = (ColumnDb)db.GetColumnDb(ReceiptsColumns.Blocks);
        Random random = new(42);
        byte[][] values = new byte[keyCount][];
        for (int i = 0; i < keyCount; i++)
        {
            if (i % 4 == 3) continue; // absent, between stored neighbours
            random.NextBytes(values[i] = new byte[valueSize]);
            column.Set(Key(i), values[i]);
        }

        db.Flush();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(long.Parse(column.GatherProperty("rocksdb.num-entries-active-mem-table")!), Is.Zero, "reads must reach SST files, not the memtable");
            Assert.That(long.Parse(column.GatherProperty("rocksdb.num-files-at-level0")!), Is.EqualTo(1), "the flush must write one SST file");
        }
        long usageBefore = BlockCacheUsage(column);

        ReadFlags flags = ReadFlags.HintReadAhead | (hintCacheMiss ? ReadFlags.HintCacheMiss : ReadFlags.None);
        using (IColumnDbSnapshot<ReceiptsColumns> snapshot = ((IColumnsDb<ReceiptsColumns>)db).CreateSnapshot(sequentialReadAhead: true))
        {
            IReadOnlyKeyValueStore snapshotColumn = snapshot.GetColumn(ReceiptsColumns.Blocks);
            for (int i = 0; i + 4 < keyCount; i += 4)
            {
                // From i + 1 the iterator steps to i + 2 and its probe passes the absent i + 3: a settled miss.
                // From i + 2 it steps straight past i + 3, cannot settle the read, and falls back to a point read.
                int stored = probe ? i + 1 : i + 2;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(KeyValueStoreReads.Read(snapshotColumn, Key(stored), flags, spanAction), Is.EqualTo(values[stored]), $"key {stored}");
                    Assert.That(KeyValueStoreReads.Read(snapshotColumn, Key(i + 3), flags, spanAction), Is.Null, $"absent key {i + 3}");
                }
            }
        }

        long added = BlockCacheUsage(column) - usageBefore;
        // Only the point reads of the fallback can fill the cache, and HintCacheMiss must keep them out of it.
        // The fallback without the flag is the control: it shows that these misses do read data blocks. A miss that
        // the cleanup timer of the iterator pool sends to the fallback adds one or two blocks, far below the bound.
        bool fills = !probe && !hintCacheMiss;
        Assert.That(added, fills ? Is.GreaterThan(storedBytes / 2) : Is.LessThan(storedBytes / 10));
    }

    private static byte[] Key(int i)
    {
        byte[] key = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(key, i);
        return key;
    }

    private static long BlockCacheUsage(ColumnDb column) => long.Parse(column.GatherProperty("rocksdb.block-cache-usage")!);

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
        _db = TestRocksDbFactory.OpenColumnsDb<ReceiptsColumns>(DbPath,
            new("Blocks", DbPath),
            new DbConfig(),
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false));

        Assert.That(_db.GetColumnDb(ReceiptsColumns.Blocks).Get(TestItem.KeccakA), Is.EqualTo(value));
    }
}
