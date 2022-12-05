// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Test.IO;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence.Rocks
{
    [TestFixture]
    [Parallelizable(ParallelScope.Default)]
    public class ConsumerNdmDbInitializerTests
    {
        private string _folderWithDbs;

        [OneTimeSetUp]
        public void Initialize()
        {
            _folderWithDbs = TempPath.GetTempDirectory().Path;
            Directory.CreateDirectory(_folderWithDbs);
        }

        [Test]
        public async Task ProviderInitTests_MemDbProvider()
        {
            using DbProvider dbProvider = new DbProvider(DbModeHint.Mem);
            RocksDbFactory rocksDbFactory = new RocksDbFactory(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, "mem"));
            ConsumerNdmDbInitializer initializer = new ConsumerNdmDbInitializer(dbProvider, new NdmConfig(), rocksDbFactory, new MemDbFactory());
            initializer.Reset();
            await initializer.InitAsync();
            Assert.AreEqual(4, dbProvider.RegisteredDbs.Count());
            Assert.IsTrue(dbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerDepositApprovals) is MemDb);
            Assert.IsTrue(dbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerReceipts) is MemDb);
            Assert.IsTrue(dbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerSessions) is MemDb);
            Assert.IsTrue(dbProvider.GetDb<IDb>(ConsumerNdmDbNames.Deposits) is MemDb);
        }

        [Test]
        public async Task ProviderInitTests_RocksDbProvider()
        {
            RocksDbFactory rocksDbFactory = new RocksDbFactory(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, "rocks"));
            using var dbProvider = new DbProvider(DbModeHint.Persisted);
            var initializer = new ConsumerNdmDbInitializer(dbProvider, new NdmConfig(), rocksDbFactory, new MemDbFactory());
            initializer.Reset();
            await initializer.InitAsync();
            Assert.AreEqual(4, dbProvider.RegisteredDbs.Count());
            Assert.IsTrue(dbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerDepositApprovals) is DbOnTheRocks);
            Assert.IsTrue(dbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerReceipts) is DbOnTheRocks);
            Assert.IsTrue(dbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerSessions) is DbOnTheRocks);
            Assert.IsTrue(dbProvider.GetDb<IDb>(ConsumerNdmDbNames.Deposits) is DbOnTheRocks);
        }

        [Test]
        public async Task ProviderInitTests_ReadonlyDbProvider()
        {
            using DbProvider dbProvider = new DbProvider(DbModeHint.Persisted);
            RocksDbFactory rocksDbFactory = new RocksDbFactory(new DbConfig(), LimboLogs.Instance,
                Path.Combine(_folderWithDbs, "readonly"));
            using ReadOnlyDbProvider readonlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
            ConsumerNdmDbInitializer initializer = new ConsumerNdmDbInitializer(readonlyDbProvider, new NdmConfig(), rocksDbFactory,
                new MemDbFactory());
            initializer.Reset();
            await initializer.InitAsync();
            Assert.AreEqual(4, readonlyDbProvider.RegisteredDbs.Count());
            Assert.IsTrue(readonlyDbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerDepositApprovals) is ReadOnlyDb);
            Assert.IsTrue(readonlyDbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerReceipts) is ReadOnlyDb);
            Assert.IsTrue(readonlyDbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerSessions) is ReadOnlyDb);
            Assert.IsTrue(readonlyDbProvider.GetDb<IDb>(ConsumerNdmDbNames.Deposits) is ReadOnlyDb);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folderWithDbs))
                Directory.Delete(_folderWithDbs, true);
        }
    }
}
