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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Infrastructure.Database;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test
{
    [Parallelizable(ParallelScope.All)]
    public class NdmDbProviderTests
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
            var initializer = new NdmDbInitializer(new NdmConfig(), dbProvider, rocksDbFactory, new MemDbFactory());
            await initializer.Init();
            Assert.NotNull(dbProvider.GetDb<IDb>(NdmDbNames.Configs));
            Assert.NotNull(dbProvider.GetDb<IDb>(NdmDbNames.EthRequests));
            Assert.AreEqual(2, dbProvider.RegisteredDbs.Count());
            Assert.IsTrue(dbProvider.GetDb<IDb>(NdmDbNames.Configs) is MemDb);
            Assert.IsTrue(dbProvider.GetDb<IDb>(NdmDbNames.EthRequests) is MemDb);
        }

        [Test]
        public async Task ProviderInitTests_RocksDbProvider()
        {
            var dbProvider = new DbProvider(DbModeHint.Persisted);
            var rocksDbFactory = new RocksDbFactory(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, "rocks"));
            var initializer = new NdmDbInitializer(new NdmConfig(), dbProvider, rocksDbFactory, new MemDbFactory());
            await initializer.Init();
            Assert.NotNull(dbProvider.GetDb<IDb>(NdmDbNames.Configs));
            Assert.NotNull(dbProvider.GetDb<IDb>(NdmDbNames.EthRequests));
            Assert.AreEqual(2, dbProvider.RegisteredDbs.Count());
            Assert.IsTrue(dbProvider.GetDb<IDb>(NdmDbNames.Configs) is DbOnTheRocks);
            Assert.IsTrue(dbProvider.GetDb<IDb>(NdmDbNames.EthRequests) is DbOnTheRocks);
        }

        [Test]
        public async Task ProviderInitTests_ReadonlyDbProvider()
        {
            var dbProvider = new DbProvider(DbModeHint.Persisted);
            var rocksDbFactory = new RocksDbFactory(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, "readonly"));
            var readonlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
            var initializer = new NdmDbInitializer(new NdmConfig(), readonlyDbProvider, rocksDbFactory, new MemDbFactory());
            await initializer.Init();
            Assert.NotNull(readonlyDbProvider.GetDb<IDb>(NdmDbNames.Configs));
            Assert.NotNull(readonlyDbProvider.GetDb<IDb>(NdmDbNames.EthRequests));
            Assert.AreEqual(2, readonlyDbProvider.RegisteredDbs.Count());
            Assert.IsTrue(readonlyDbProvider.GetDb<IDb>(NdmDbNames.Configs) is ReadOnlyDb);
            Assert.IsTrue(readonlyDbProvider.GetDb<IDb>(NdmDbNames.EthRequests) is ReadOnlyDb);
        }

        [Test]
        public async Task ProviderInitTests_BeamSyncDbProvider()
        {
            var syncModeSelector = Substitute.For<ISyncModeSelector>();
            var dbProvider = TestMemDbProvider.Init();
            var rocksDbFactory = new RocksDbFactory(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, "beam"));
            IDbProvider beamSyncDbProvider = new BeamSyncDbProvider(syncModeSelector, dbProvider, new SyncConfig(), LimboLogs.Instance);
            var initializer = new NdmDbInitializer(new NdmConfig(), beamSyncDbProvider, rocksDbFactory, new MemDbFactory());
            await initializer.Init();
            Assert.NotNull(beamSyncDbProvider.GetDb<IDb>(NdmDbNames.Configs));
            Assert.NotNull(beamSyncDbProvider.GetDb<IDb>(NdmDbNames.EthRequests));
            Assert.IsTrue(beamSyncDbProvider.GetDb<IDb>(NdmDbNames.Configs) is MemDb);
            Assert.IsTrue(beamSyncDbProvider.GetDb<IDb>(NdmDbNames.EthRequests) is MemDb);
        }

        [Test]
        public async Task ProviderInitTests_RpcDbProvider()
        {
            var dbProvider = new DbProvider(DbModeHint.Persisted);
            var rocksDbFactory = new RocksDbFactory(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, "rpc"));
            var serializer = Substitute.For<IJsonSerializer>();
            var client = Substitute.For<IJsonRpcClient>();
            var rpcDbFactory = new RpcDbFactory(new MemDbFactory(), rocksDbFactory, serializer, client, LimboLogs.Instance);
            var initializer = new NdmDbInitializer(new NdmConfig(), dbProvider, rpcDbFactory, rpcDbFactory);
            await initializer.Init();
            dbProvider.RegisterDb("test", new MemDb());
            Assert.NotNull(dbProvider.GetDb<IDb>(NdmDbNames.Configs));
            Assert.NotNull(dbProvider.GetDb<IDb>(NdmDbNames.EthRequests));
            Assert.AreEqual(3, dbProvider.RegisteredDbs.Count());
            Assert.IsTrue(dbProvider.GetDb<IDb>(NdmDbNames.Configs) is ReadOnlyDb);
            Assert.IsTrue(dbProvider.GetDb<IDb>(NdmDbNames.EthRequests) is ReadOnlyDb);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folderWithDbs))
                Directory.Delete(_folderWithDbs, true);
        }
    }
}
