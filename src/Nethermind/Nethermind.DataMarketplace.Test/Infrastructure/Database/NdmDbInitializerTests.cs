// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Infrastructure.Database;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
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
