// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State.Flat;
using NUnit.Framework;

namespace Nethermind.Db.Lmdb.Test;

public class LmdbDbFactoryDecoratorTests
{
    private string BasePath => Path.Combine("testdb-lmdb-factory", TestContext.CurrentContext.Test.ID);
    private RocksDbFactory _rocksDbFactory = null!;
    private LmdbDbFactoryDecorator _decorator = null!;
    private HyperClockCacheWrapper _sharedCache = null!;

    [SetUp]
    public void Setup()
    {
        if (Directory.Exists(BasePath)) Directory.Delete(BasePath, true);
        _sharedCache = new HyperClockCacheWrapper();
        _rocksDbFactory = new RocksDbFactory(
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            new DbConfig(),
            _sharedCache,
            LimboLogs.Instance,
            BasePath);
        _decorator = new LmdbDbFactoryDecorator(_rocksDbFactory, new LmdbConfig { InitialMapSizeBytes = 64L * 1024 * 1024 }, new SyncConfig(), LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _sharedCache.Dispose();
        if (Directory.Exists(BasePath)) Directory.Delete(BasePath, true);
    }

    [Test]
    public void CreateColumnsDb_for_the_flat_db_wraps_account_and_storage_in_Lmdb()
    {
        using IColumnsDb<FlatDbColumns> columnsDb = _decorator.CreateColumnsDb<FlatDbColumns>(new DbSettings("Flat", DbNames.Flat));

        Assert.That(columnsDb, Is.TypeOf<LmdbColumnsDbAdapter>());
        Assert.That(columnsDb.GetColumnDb(FlatDbColumns.Account), Is.TypeOf<LmdbDb>());
        Assert.That(columnsDb.GetColumnDb(FlatDbColumns.Storage), Is.TypeOf<LmdbDb>());
        Assert.That(columnsDb.GetColumnDb(FlatDbColumns.Metadata), Is.Not.TypeOf<LmdbDb>());
    }

    [Test]
    public void CreateColumnsDb_for_the_flat_db_creates_on_disk_stores_next_to_the_rocksdb_directory()
    {
        DbSettings settings = new("Flat", DbNames.Flat);
        string expectedBasePath = _rocksDbFactory.GetFullDbPath(settings);

        using IColumnsDb<FlatDbColumns> columnsDb = _decorator.CreateColumnsDb<FlatDbColumns>(settings);

        Assert.That(Directory.Exists(Path.Combine(expectedBasePath, "account.lmdb")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(expectedBasePath, "storage.lmdb")), Is.True);
    }

    [Test]
    public void CreateColumnsDb_matches_case_insensitively_on_the_title_cased_flat_db_name()
    {
        // ContainerBuilderExtensions.AddColumnDatabase<T> title-cases DbNames.Flat ("flat") into DbSettings.DbName
        // ("Flat") before it reaches IDbFactory - see LmdbDbFactoryDecorator's own comment.
        using IColumnsDb<FlatDbColumns> columnsDb = _decorator.CreateColumnsDb<FlatDbColumns>(new DbSettings("Flat", DbNames.Flat));
        Assert.That(columnsDb, Is.TypeOf<LmdbColumnsDbAdapter>());
    }

    [Test]
    public void CreateColumnsDb_for_a_different_db_delegates_unchanged()
    {
        using IColumnsDb<ReceiptsColumns> columnsDb = _decorator.CreateColumnsDb<ReceiptsColumns>(new DbSettings("Receipts", DbNames.Receipts));
        Assert.That(columnsDb, Is.TypeOf<ColumnsDb<ReceiptsColumns>>());
    }

    [Test]
    public void CreateColumnsDb_resolves_per_column_MaxOperationsPerWave_overrides()
    {
        // Account and Storage were observed to have very different natural batch shapes, and batch size (not
        // reader count or lock contention) was confirmed to be the dominant driver of physical-commit cost - see
        // PLAN-LMDB.md "Known issues". MaxOperationsPerWaveAccount/Storage let each column be tuned
        // independently; 0 (Storage here) falls back to the shared MaxOperationsPerWave.
        string basePath = Path.Combine("testdb-lmdb-factory-override", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(basePath)) Directory.Delete(basePath, true);
        using HyperClockCacheWrapper sharedCache = new();
        RocksDbFactory rocksDbFactory = new(
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            new DbConfig(),
            sharedCache,
            LimboLogs.Instance,
            basePath);
        LmdbConfig config = new()
        {
            InitialMapSizeBytes = 64L * 1024 * 1024,
            MaxOperationsPerWave = 1000,
            MaxOperationsPerWaveAccount = 4,
        };
        LmdbDbFactoryDecorator decorator = new(rocksDbFactory, config, new SyncConfig(), LimboLogs.Instance);

        try
        {
            using LmdbColumnsDbAdapter columnsDb = (LmdbColumnsDbAdapter)decorator.CreateColumnsDb<FlatDbColumns>(new DbSettings("Flat", DbNames.Flat));

            const int opsInBatch = 50;
            using (IWriteBatch accountBatch = columnsDb.Account.StartWriteBatch())
            {
                for (int i = 0; i < opsInBatch; i++) accountBatch.PutSpan(BitConverter.GetBytes(i), BitConverter.GetBytes(i));
            }
            using (IWriteBatch storageBatch = columnsDb.Storage.StartWriteBatch())
            {
                for (int i = 0; i < opsInBatch; i++) storageBatch.PutSpan(BitConverter.GetBytes(i), BitConverter.GetBytes(i));
            }

            LmdbDb.DiagnosticsSnapshot accountDiagnostics = columnsDb.Account.TakeAndResetDiagnostics();
            LmdbDb.DiagnosticsSnapshot storageDiagnostics = columnsDb.Storage.TakeAndResetDiagnostics();

            // Account's override (4) is far below the 50-op batch, so it must chunk into multiple physical commits.
            Assert.That(accountDiagnostics.WriteWaveCount, Is.GreaterThan(1));
            // Storage has no override and falls back to the shared 1000, comfortably above 50 ops - one commit.
            Assert.That(storageDiagnostics.WriteWaveCount, Is.EqualTo(1));
        }
        finally
        {
            if (Directory.Exists(basePath)) Directory.Delete(basePath, true);
        }
    }

    [Test]
    public void CreateColumnsDb_resolves_per_column_PageSizeBytes_overrides()
    {
        // Live sync data confirmed Storage's effectively-random key order gives it a stable, scale-invariant
        // ~65% B+tree leaf-page fill factor, while Account - near-100% sequential/append - is already
        // well-packed and doesn't need a page size change. PageSizeBytesAccount/Storage let each column be tuned
        // independently - see ILmdbConfig.PageSizeBytes's remarks; 0 (Account here) falls back to the shared
        // PageSizeBytes.
        string basePath = Path.Combine("testdb-lmdb-factory-pagesize-override", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(basePath)) Directory.Delete(basePath, true);
        using HyperClockCacheWrapper sharedCache = new();
        RocksDbFactory rocksDbFactory = new(
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            new DbConfig(),
            sharedCache,
            LimboLogs.Instance,
            basePath);
        LmdbConfig config = new()
        {
            InitialMapSizeBytes = 64L * 1024 * 1024,
            PageSizeBytes = 4096,
            PageSizeBytesStorage = 8192,
        };
        LmdbDbFactoryDecorator decorator = new(rocksDbFactory, config, new SyncConfig(), LimboLogs.Instance);

        try
        {
            using LmdbColumnsDbAdapter columnsDb = (LmdbColumnsDbAdapter)decorator.CreateColumnsDb<FlatDbColumns>(new DbSettings("Flat", DbNames.Flat));

            columnsDb.Account.Set(BitConverter.GetBytes(1), BitConverter.GetBytes(1));
            columnsDb.Storage.Set(BitConverter.GetBytes(1), BitConverter.GetBytes(1));

            Assert.That(columnsDb.Account.GetPerPartitionStats().PageSize, Is.EqualTo(4096), "Account has no override, so it falls back to the shared PageSizeBytes.");
            Assert.That(columnsDb.Storage.GetPerPartitionStats().PageSize, Is.EqualTo(8192), "Storage's own override must win over the shared PageSizeBytes.");
        }
        finally
        {
            if (Directory.Exists(basePath)) Directory.Delete(basePath, true);
        }
    }

    [Test]
    public void CreateColumnsDb_wires_EnableAccountPartitioning_from_SyncConfigs_partition_count_independently_of_Storage()
    {
        // The whole point of partitioning (see ILmdbConfig.EnableAccountPartitioning's remarks): a lower
        // partition's own monotonic stream can still use AppendData even after a higher partition already has
        // larger keys, which a single flat keyspace could never allow. This only proves anything if the count
        // actually came from ISyncConfig end-to-end, so SnapSyncAccountRangePartitionCount is set to a
        // non-default value (2) here - if the decorator ignored it and used some other count, the partition
        // boundary used below (0x80000000) would be wrong and this would fail.
        string basePath = Path.Combine("testdb-lmdb-factory-partitioning", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(basePath)) Directory.Delete(basePath, true);
        using HyperClockCacheWrapper sharedCache = new();
        RocksDbFactory rocksDbFactory = new(
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            new DbConfig(),
            sharedCache,
            LimboLogs.Instance,
            basePath);
        LmdbConfig config = new()
        {
            InitialMapSizeBytes = 64L * 1024 * 1024,
            EnableAccountPartitioning = true,
        };
        SyncConfig syncConfig = new() { SnapSyncAccountRangePartitionCount = 2 };
        LmdbDbFactoryDecorator decorator = new(rocksDbFactory, config, syncConfig, LimboLogs.Instance);

        try
        {
            using LmdbColumnsDbAdapter columnsDb = (LmdbColumnsDbAdapter)decorator.CreateColumnsDb<FlatDbColumns>(new DbSettings("Flat", DbNames.Flat));

            byte[] highKey = [0xFF, 0xFF, 0xFF, 0x01];
            byte[] lowKey = [0x00, 0x00, 0x00, 0x01];

            using (IWriteBatch batch = columnsDb.Account.StartWriteBatch()) batch.PutSpan(highKey, highKey);
            columnsDb.Account.TakeAndResetDiagnostics();
            using (IWriteBatch batch = columnsDb.Account.StartWriteBatch()) batch.PutSpan(lowKey, lowKey);
            LmdbDb.DiagnosticsSnapshot accountDiagnostics = columnsDb.Account.TakeAndResetDiagnostics();
            Assert.That(accountDiagnostics.AppendedPutCount, Is.EqualTo(1),
                "Account should be partitioned (2 partitions, from SyncConfig), so the lower partition can append independently of the higher one.");

            using (IWriteBatch batch = columnsDb.Storage.StartWriteBatch()) batch.PutSpan(highKey, highKey);
            columnsDb.Storage.TakeAndResetDiagnostics();
            using (IWriteBatch batch = columnsDb.Storage.StartWriteBatch()) batch.PutSpan(lowKey, lowKey);
            LmdbDb.DiagnosticsSnapshot storageDiagnostics = columnsDb.Storage.TakeAndResetDiagnostics();
            Assert.That(storageDiagnostics.AppendedPutCount, Is.EqualTo(0),
                "Storage must stay a single flat keyspace - the lower key here is genuinely below the existing max and must not append.");
        }
        finally
        {
            if (Directory.Exists(basePath)) Directory.Delete(basePath, true);
        }
    }

    [Test]
    public void CreateDb_delegates_unchanged()
    {
        using IDb db = _decorator.CreateDb(new DbSettings(DbNames.Code, DbNames.Code));
        Assert.That(db, Is.TypeOf<DbOnTheRocks>());
    }

    [Test]
    public void GetFullDbPath_delegates_unchanged()
    {
        DbSettings settings = new(DbNames.Code, DbNames.Code);
        Assert.That(_decorator.GetFullDbPath(settings), Is.EqualTo(_rocksDbFactory.GetFullDbPath(settings)));
    }

    [Test]
    public void Constructing_the_decorator_raises_the_process_wide_ThreadPool_minimum()
    {
        // Regression for the ThreadPool-starvation workaround - see PLAN-LMDB.md "Known issues". This is a
        // genuinely process-wide, one-time .NET runtime setting (a static constructor, not per-instance logic),
        // so this only checks that it was raised to AT LEAST the target by the time any test in this fixture
        // runs (guaranteed, since [SetUp] already constructs a decorator) - never that it equals exactly the
        // target, since it must never lower whatever an earlier test or the real host process already set.
        ThreadPool.GetMinThreads(out int workerMin, out int ioMin);
        Assert.That(workerMin, Is.GreaterThanOrEqualTo(512));
        Assert.That(ioMin, Is.GreaterThanOrEqualTo(512));
    }
}
