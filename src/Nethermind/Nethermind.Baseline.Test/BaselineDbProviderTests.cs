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
//using Nethermind.Baseline.Config;
//using Nethermind.Baseline.Db;
//using Nethermind.Blockchain.Synchronization;
//using Nethermind.Db;
//using Nethermind.Db.Rocks;
//using Nethermind.Db.Rocks.Config;
//using Nethermind.Logging;
//using Nethermind.Synchronization.BeamSync;
//using Nethermind.Synchronization.ParallelSync;
//using NSubstitute;
//using NUnit.Framework;

//namespace Nethermind.Baseline.Test
//{
//    [Parallelizable(ParallelScope.All)]
//    public class BaselineDbProviderTests
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
//            var provider = new BaselineDbProvider(dbProvider, new BaselineConfig(), new DbConfig());
//            await provider.Init();
//            Assert.NotNull(provider.BaselineTreeDb);
//            Assert.NotNull(provider.BaselineTreeMetadataDb);
//            Assert.AreEqual(2, dbProvider.OtherDbs.Count());
//            Assert.IsTrue(provider.BaselineTreeDb is MemDb);
//            Assert.IsTrue(provider.BaselineTreeMetadataDb is MemDb);
//        }

//        [Test]
//        public async Task ProviderInitTests_RocksDbProvider()
//        {
//            var dbProvider = new RocksDbProvider(null, new DbConfig(), Path.Combine(_folderWithDbs, "rocks"));
//            var provider = new BaselineDbProvider(dbProvider, new BaselineConfig(), new DbConfig());
//            await provider.Init();
//            Assert.NotNull(provider.BaselineTreeDb);
//            Assert.NotNull(provider.BaselineTreeMetadataDb);
//            Assert.AreEqual(2, dbProvider.OtherDbs.Count());
//            Assert.IsTrue(provider.BaselineTreeDb is DbOnTheRocks);
//            Assert.IsTrue(provider.BaselineTreeMetadataDb is DbOnTheRocks);
//        }

//        [Test]
//        public async Task ProviderInitTests_ReadonlyDbProvider()
//        {
//            var dbProvider = new RocksDbProvider(null, new DbConfig(), Path.Combine(_folderWithDbs, "readonly"));
//            var readonlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
//            var provider = new BaselineDbProvider(readonlyDbProvider, new BaselineConfig(), new DbConfig());
//            await provider.Init();
//            Assert.NotNull(provider.BaselineTreeDb);
//            Assert.NotNull(provider.BaselineTreeMetadataDb);
//            Assert.AreEqual(2, dbProvider.OtherDbs.Count());
//            Assert.IsTrue(provider.BaselineTreeDb is ReadOnlyDb);
//            Assert.IsTrue(provider.BaselineTreeMetadataDb is ReadOnlyDb);
//        }

//        [Test]
//        public async Task ProviderInitTests_BeamSyncDbProvider()
//        {
//            var syncModeSelector = Substitute.For<ISyncModeSelector>();
//            var dbProvider = new MemDbProvider();
//            var beamSyncDbProvider = new BeamSyncDbProvider(syncModeSelector, dbProvider, new SyncConfig(), LimboLogs.Instance);
//            var provider = new BaselineDbProvider(beamSyncDbProvider, new BaselineConfig(), new DbConfig());
//            await provider.Init();
//            Assert.NotNull(provider.BaselineTreeDb);
//            Assert.NotNull(provider.BaselineTreeMetadataDb);
//            Assert.AreEqual(2, dbProvider.OtherDbs.Count());
//            Assert.IsTrue(provider.BaselineTreeDb is MemDb);
//            Assert.IsTrue(provider.BaselineTreeMetadataDb is MemDb);
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
