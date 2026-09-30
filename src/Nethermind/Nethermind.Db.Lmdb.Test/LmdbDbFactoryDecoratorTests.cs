// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading;
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
        _decorator = new LmdbDbFactoryDecorator(_rocksDbFactory, new LmdbConfig { InitialMapSizeBytes = 64L * 1024 * 1024 }, LimboLogs.Instance);
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
