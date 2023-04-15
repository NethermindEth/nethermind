// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;

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

                void UpdateMetrics()
                {
                    Db.Metrics.StateDbSize = _api.DbProvider!.StateDb.GetSize();
                    Db.Metrics.ReceiptsDbSize = _api.DbProvider!.ReceiptsDb.GetSize();
                    Db.Metrics.HeadersDbSize = _api.DbProvider!.HeadersDb.GetSize();
                    Db.Metrics.BlocksDbSize = _api.DbProvider!.BlocksDb.GetSize();

                    Db.Metrics.DbSize = _api.DbProvider!.RegisteredDbs.Values.Aggregate(0L, (sum, db) => sum + db.GetSize());
                }

                _api.MonitoringService.AddMetricsUpdateAction(UpdateMetrics);
            }
            catch (TypeInitializationException e)
            {
                if (logger.IsError)
                    logger.Error("RocksDb was not found, please make sure it is installed on your machine. \n On macOs : 'brew install rocksdb'", e);
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
