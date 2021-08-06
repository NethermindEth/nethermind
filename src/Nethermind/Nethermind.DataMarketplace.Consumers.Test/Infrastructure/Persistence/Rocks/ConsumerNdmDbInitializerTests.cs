//  Copyright (c) 2018 Demerzel Solutions Limited	
//  This file is part of the Nethermind library.	
// 	
//  The Nethermind library is free software: you can redistribute it and/or modify	
//  it under the terms of the GNU Lesser General Public License as published by	
//  the Free Software Foundation, either version 3 of the License, or	
//  (at your option) any later version.	
// 	
//  The Nethermind library is distributed in the hope that it will be useful,	
//  but WITHOUT ANY WARRANTY; without even the implied warranty of	
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the	
//  GNU Lesser General Public License for more details.	
// 	
//  You should have received a copy of the GNU Lesser General Public License	
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.	

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
using Nethermind.Synchronization.BeamSync;
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

        [Test]
        public async Task ProviderInitTests_BeamSyncDbProvider()
        {
            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            using IDbProvider dbProvider = await TestMemDbProvider.InitAsync();
            RocksDbFactory rocksDbFactory = new RocksDbFactory(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, "beam"));
            IDbProvider beamSyncDbProvider = new BeamSyncDbProvider(syncModeSelector, dbProvider, new SyncConfig(), LimboLogs.Instance);
            ConsumerNdmDbInitializer initializer = new ConsumerNdmDbInitializer(beamSyncDbProvider, new NdmConfig(), rocksDbFactory, new MemDbFactory());
            initializer.Reset();
            await initializer.InitAsync();
            Assert.IsTrue(beamSyncDbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerDepositApprovals) is MemDb);
            Assert.IsTrue(beamSyncDbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerReceipts) is MemDb);
            Assert.IsTrue(beamSyncDbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerSessions) is MemDb);
            Assert.IsTrue(beamSyncDbProvider.GetDb<IDb>(ConsumerNdmDbNames.Deposits) is MemDb);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folderWithDbs))
                Directory.Delete(_folderWithDbs, true);
        }
    }
}
