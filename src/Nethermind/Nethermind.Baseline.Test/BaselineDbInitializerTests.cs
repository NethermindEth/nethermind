// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Baseline.Config;
using Nethermind.Baseline.Database;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    [Parallelizable(ParallelScope.All)]
    public class BaselineDbInitializerTests
    {
        private string _folderWithDbs;

        [OneTimeSetUp]
        public void Initialize()
        {
            _folderWithDbs = Guid.NewGuid().ToString();
        }

        [Test]
        public async Task ProviderInitTests_MemDbProvider()
        {
            var dbProvider = new DbProvider(DbModeHint.Mem);
            var rocksDbFactory = new RocksDbFactory(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, "mem"));
            var initializer = new BaselineDbInitializer(dbProvider, new BaselineConfig(), rocksDbFactory, new MemDbFactory());
            await initializer.Init();
            Assert.NotNull(dbProvider.GetDb<IDb>(BaselineDbNames.BaselineTree));
            Assert.NotNull(dbProvider.GetDb<IDb>(BaselineDbNames.BaselineTreeMetadata));
            Assert.AreEqual(2, dbProvider.RegisteredDbs.Count());
            Assert.IsTrue(dbProvider.GetDb<IDb>(BaselineDbNames.BaselineTree) is MemDb);
            Assert.IsTrue(dbProvider.GetDb<IDb>(BaselineDbNames.BaselineTreeMetadata) is MemDb);
        }

        [Test]
        public async Task ProviderInitTests_RocksDbProvider()
        {
            var rocksDbFactory = new RocksDbFactory(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, "rocks"));
            var dbProvider = new DbProvider(DbModeHint.Persisted);
            var initializer = new BaselineDbInitializer(dbProvider, new BaselineConfig(), rocksDbFactory, new MemDbFactory());
            await initializer.Init();
            Assert.NotNull(dbProvider.GetDb<IDb>(BaselineDbNames.BaselineTree));
            Assert.NotNull(dbProvider.GetDb<IDb>(BaselineDbNames.BaselineTreeMetadata));
            Assert.AreEqual(2, dbProvider.RegisteredDbs.Count());
            Assert.IsTrue(dbProvider.GetDb<IDb>(BaselineDbNames.BaselineTree) is DbOnTheRocks);
            Assert.IsTrue(dbProvider.GetDb<IDb>(BaselineDbNames.BaselineTreeMetadata) is DbOnTheRocks);
        }

        [Test]
        public async Task ProviderInitTests_ReadonlyDbProvider()
        {
            var dbProvider = new DbProvider(DbModeHint.Persisted);
            var rocksDbFactory = new RocksDbFactory(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, "readonly"));
            var readonlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
            var initializer = new BaselineDbInitializer(readonlyDbProvider, new BaselineConfig(), rocksDbFactory, new MemDbFactory());
            await initializer.Init();
            Assert.NotNull(readonlyDbProvider.GetDb<IDb>(BaselineDbNames.BaselineTree));
            Assert.NotNull(readonlyDbProvider.GetDb<IDb>(BaselineDbNames.BaselineTreeMetadata));
            Assert.AreEqual(2, readonlyDbProvider.RegisteredDbs.Count());
            Assert.IsTrue(readonlyDbProvider.GetDb<IDb>(BaselineDbNames.BaselineTree) is ReadOnlyDb);
            Assert.IsTrue(readonlyDbProvider.GetDb<IDb>(BaselineDbNames.BaselineTreeMetadata) is ReadOnlyDb);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folderWithDbs))
                Directory.Delete(_folderWithDbs, true);
        }
    }
}
