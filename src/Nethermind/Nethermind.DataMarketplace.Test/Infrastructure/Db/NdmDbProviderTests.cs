////  Copyright (c) 2018 Demerzel Solutions Limited
////  This file is part of the Nethermind library.
//// 
////  The Nethermind library is free software: you can redistribute it and/or modify
////  it under the terms of the GNU Lesser General Public License as published by
////  the Free Software Foundation, either version 3 of the License, or
////  (at your option) any later version.
//// 
////  The Nethermind library is distributed in the hope that it will be useful,
////  but WITHOUT ANY WARRANTY; without even the implied warranty of
////  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
////  GNU Lesser General Public License for more details.
//// 
////  You should have received a copy of the GNU Lesser General Public License
////  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System.Threading.Tasks;
//using Nethermind.Blockchain.Synchronization;
//using Nethermind.DataMarketplace.Infrastructure.Db;
//using Nethermind.Db;
//using Nethermind.Db.Rocks;
//using Nethermind.Db.Rocks.Config;
//using Nethermind.Db.Rpc;
//using Nethermind.JsonRpc.Client;
//using Nethermind.Logging;
//using Nethermind.Serialization.Json;
//using Nethermind.Synchronization.BeamSync;
//using Nethermind.Synchronization.ParallelSync;
//using NSubstitute;
//using NUnit.Framework;

//namespace Nethermind.Baseline.Test
//{
//    [Parallelizable(ParallelScope.All)]
//    public class NdmDbProviderTests
//    {
//        private List<IDb> _dbsToCleanup = new List<IDb>();
//        private string _folderWithDbs;

//        [OneTimeSetUp]
//        public void Initialize()
//        {
//            _folderWithDbs = Guid.NewGuid().ToString();
//        }

//        [Test]
//        public async Task ProviderInitTests_MemDbProvider()
//        {
//            var dbProvider = new MemDbProvider();
//            var provider = new NdmDbProvider(dbProvider, LimboLogs.Instance);
//            await provider.Init();
//            Assert.NotNull(provider.ConfigsDb);
//            Assert.NotNull(provider.EthRequestsDb);
//            Assert.AreEqual(2, dbProvider.OtherDbs.Count());
//            Assert.IsTrue(provider.ConfigsDb is MemDb);
//            Assert.IsTrue(provider.EthRequestsDb is MemDb);
//        }

//        [Test]
//        public async Task ProviderInitTests_RocksDbProvider()
//        {
//            var dbProvider = new RocksDbProvider(null, new DbConfig(), Path.Combine(_folderWithDbs, "rocks"));
//            var provider = new NdmDbProvider(dbProvider, LimboLogs.Instance);
//            await provider.Init();
//            Assert.NotNull(provider.ConfigsDb);
//            Assert.NotNull(provider.EthRequestsDb);
//            Assert.AreEqual(2, dbProvider.OtherDbs.Count());
//            Assert.IsTrue(provider.ConfigsDb is DbOnTheRocks);
//            Assert.IsTrue(provider.EthRequestsDb is DbOnTheRocks);
//        }

//        [Test]
//        public async Task ProviderInitTests_ReadonlyDbProvider()
//        {
//            var dbProvider = new RocksDbProvider(null, new DbConfig(), Path.Combine(_folderWithDbs, "readonly"));
//            var readonlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
//            var provider = new NdmDbProvider(readonlyDbProvider, LimboLogs.Instance);
//            await provider.Init();
//            Assert.NotNull(provider.ConfigsDb);
//            Assert.NotNull(provider.EthRequestsDb);
//            Assert.AreEqual(2, dbProvider.OtherDbs.Count());
//            Assert.IsTrue(provider.ConfigsDb is ReadOnlyDb);
//            Assert.IsTrue(provider.EthRequestsDb is ReadOnlyDb);
//        }

//        [Test]
//        public async Task ProviderInitTests_BeamSyncDbProvider()
//        {
//            var syncModeSelector = Substitute.For<ISyncModeSelector>();
//            var dbProvider = new MemDbProvider();
//            var beamSyncDbProvider = new BeamSyncDbProvider(syncModeSelector, dbProvider, new SyncConfig(), LimboLogs.Instance);
//            var provider = new NdmDbProvider(beamSyncDbProvider, LimboLogs.Instance);
//            await provider.Init();
//            Assert.NotNull(provider.ConfigsDb);
//            Assert.NotNull(provider.EthRequestsDb);
//            Assert.AreEqual(2, dbProvider.OtherDbs.Count());
//            Assert.IsTrue(provider.ConfigsDb is MemDb);
//            Assert.IsTrue(provider.EthRequestsDb is MemDb);
//        }

//        [Test]
//        public async Task ProviderInitTests_RpcDbProvider()
//        {
//            var serializer = Substitute.For<IJsonSerializer>();
//            var client = Substitute.For<IJsonRpcClient>();
//            var dbProvider = new MemDbProvider();
//            var rpcDbProvider = new RpcDbProvider(serializer, client, LimboLogs.Instance, dbProvider);
//            var provider = new NdmDbProvider(rpcDbProvider, LimboLogs.Instance);
//            rpcDbProvider.RegisterDb("test", "Test", new DbConfig());
//            await provider.Init();
//            Assert.NotNull(provider.ConfigsDb);
//            Assert.NotNull(provider.EthRequestsDb);
//            Assert.AreEqual(3, dbProvider.OtherDbs.Count());
//            Assert.IsTrue(provider.ConfigsDb is ReadOnlyDb);
//            Assert.IsTrue(provider.EthRequestsDb is ReadOnlyDb);
//        }

//        [OneTimeTearDown]
//        public void TearDown()
//        {
//            foreach (var dbToCleanup in _dbsToCleanup)
//            {
//                dbToCleanup?.Dispose();
//            }

//            if (Directory.Exists(_folderWithDbs))
//                Directory.Delete(_folderWithDbs);
//        }
//    }
//}
