// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(ApplyMemoryHint))]
    public class InitDatabase : IStep
    {
        private readonly INethermindApi _api;

        public InitDatabase(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken _)
        {
            ILogger logger = _api.LogManager.GetClassLogger();

            /* sync */
            IDbConfig dbConfig = _api.Config<IDbConfig>();
            ISyncConfig syncConfig = _api.Config<ISyncConfig>();
            IInitConfig initConfig = _api.Config<IInitConfig>();
            IPruningConfig pruningConfig = _api.Config<IPruningConfig>();

            foreach (PropertyInfo propertyInfo in typeof(IDbConfig).GetProperties())
            {
                if (logger.IsDebug) logger.Debug($"DB {propertyInfo.Name}: {propertyInfo.GetValue(dbConfig)}");
            }

            try
            {
                bool useReceiptsDb = initConfig.StoreReceipts || syncConfig.DownloadReceiptsInFastSync;
                InitDbApi(initConfig, dbConfig, initConfig.StoreReceipts || syncConfig.DownloadReceiptsInFastSync);
                StandardDbInitializer dbInitializer = new(_api.DbProvider, _api.RocksDbFactory, _api.MemDbFactory, _api.FileSystem, pruningConfig.Mode.IsFull());
                await dbInitializer.InitStandardDbsAsync(useReceiptsDb);
            }
            catch (TypeInitializationException ex)
            {
                if (logger.IsError)
                    logger.Error("Failed loading RocksDB", ex);
            }
        }

        private void InitDbApi(IInitConfig initConfig, IDbConfig dbConfig, bool storeReceipts)
        {
            switch (initConfig.DiagnosticMode)
            {
                case DiagnosticMode.RpcDb:
                    _api.DbProvider = new DbProvider(DbModeHint.Persisted);
                    RocksDbFactory rocksDbFactory = new(dbConfig, _api.LogManager, Path.Combine(initConfig.BaseDbPath, "debug"));
                    RpcDbFactory rpcDbFactory = new(new MemDbFactory(), rocksDbFactory, _api.EthereumJsonSerializer, new BasicJsonRpcClient(new Uri(initConfig.RpcDbUrl), _api.EthereumJsonSerializer, _api.LogManager), _api.LogManager);
                    _api.RocksDbFactory = rpcDbFactory;
                    _api.MemDbFactory = rpcDbFactory;
                    break;
                case DiagnosticMode.ReadOnlyDb:
                    DbProvider rocksDbProvider = new(DbModeHint.Persisted);
                    _api.DbProvider = new ReadOnlyDbProvider(rocksDbProvider, storeReceipts); // ToDo storeReceipts as createInMemoryWriteStore - bug?
                    _api.DisposeStack.Push(rocksDbProvider);
                    _api.RocksDbFactory = new RocksDbFactory(dbConfig, _api.LogManager, Path.Combine(initConfig.BaseDbPath, "debug"));
                    _api.MemDbFactory = new MemDbFactory();
                    break;
                case DiagnosticMode.MemDb:
                    _api.DbProvider = new DbProvider(DbModeHint.Mem);
                    _api.RocksDbFactory = new RocksDbFactory(dbConfig, _api.LogManager, Path.Combine(initConfig.BaseDbPath, "debug"));
                    _api.MemDbFactory = new MemDbFactory();
                    break;
                default:
                    _api.DbProvider = new DbProvider(DbModeHint.Persisted);
                    _api.RocksDbFactory = new RocksDbFactory(dbConfig, _api.LogManager, initConfig.BaseDbPath);
                    _api.MemDbFactory = new MemDbFactory();
                    break;
            }
        }
    }
}
