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
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class InitDatabase : IStep
    {
        private readonly IBasicApi _api;

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

            foreach (PropertyInfo propertyInfo in typeof(IDbConfig).GetProperties())
            {
                if (logger.IsDebug) logger.Debug($"DB {propertyInfo.Name}: {propertyInfo.GetValue(dbConfig)}");
            }

            try
            {
                var useReceiptsDb = initConfig.StoreReceipts || syncConfig.DownloadReceiptsInFastSync;
                _api.DbProvider = GetDbProvider(initConfig, dbConfig, useReceiptsDb);
                InitDbApi(initConfig, dbConfig, initConfig.StoreReceipts || syncConfig.DownloadReceiptsInFastSync);
                var dbInitalizer = new StandardDbInitializer(_api.DbProvider, _api.RocksDbFactory, _api.MemDbFactory);
                await dbInitalizer.InitStandardDbs(useReceiptsDb);
                if (syncConfig.BeamSync)
                {
                    _api.SyncModeSelector = new PendingSyncModeSelector();
                    BeamSyncDbProvider beamSyncProvider = new BeamSyncDbProvider(_api.SyncModeSelector, _api.DbProvider, _api.Config<ISyncConfig>(), _api.LogManager);
                    _api.DbProvider = beamSyncProvider;
                }
            }
            catch(TypeInitializationException)
            {
                if(logger.IsError)
                    logger.Error("RocksDb was not found, please make sure it is installed on your machine. \n On macOs : 'brew install rocksdb'");
            }
        }

        // ToDo: receipts columns db, add logging readonly factory?, dbMode and RocksDbFactory? default implementation of interface, rocksDbSettings with builder

        private void InitDbApi(IInitConfig initConfig, IDbConfig dbConfig, bool storeReceipts)
        {
            switch (initConfig.DiagnosticMode)
            {
                case DiagnosticMode.RpcDb:
                    _api.DbProvider = GetRocksDbProvider(DbModeHint.Persisted);
                    var rocksDbFactory = new RocksDbFactory(dbConfig, _api.LogManager, Path.Combine(initConfig.BaseDbPath, "debug"));
                    var rpcDbFactory = new RpcDbFactory(new MemDbFactory(), rocksDbFactory, _api.EthereumJsonSerializer, new BasicJsonRpcClient(new Uri(initConfig.RpcDbUrl), _api.EthereumJsonSerializer, _api.LogManager), _api.LogManager);
                    _api.RocksDbFactory = rpcDbFactory;
                    _api.MemDbFactory = rpcDbFactory;
                    break;
                case DiagnosticMode.ReadOnlyDb:
                    var rocksDbProvider = GetRocksDbProvider(DbModeHint.Persisted);
                    _api.DbProvider = new ReadOnlyDbProvider(rocksDbProvider, storeReceipts); // ToDo storeReceipts as createInMemoryWriteStore - bug?
                    _api.RocksDbFactory = new RocksDbFactory(dbConfig, _api.LogManager, Path.Combine(initConfig.BaseDbPath, "debug"));
                    _api.MemDbFactory = new MemDbFactory();
                    break;
                case DiagnosticMode.MemDb:
                    _api.DbProvider = new MemDbProvider();
                    _api.RocksDbFactory = new RocksDbFactory(dbConfig, _api.LogManager, Path.Combine(initConfig.BaseDbPath, "debug"));
                    _api.MemDbFactory = new MemDbFactory();
                    break;
                default:
                    _api.DbProvider = GetRocksDbProvider(DbModeHint.Persisted);
                    _api.RocksDbFactory = new RocksDbFactory(dbConfig, _api.LogManager, initConfig.BaseDbPath);
                    _api.MemDbFactory = new MemDbFactory();
                    break;
            }
        }

        private IDbProvider GetDbProvider(IInitConfig initConfig, IDbConfig dbConfig, bool storeReceipts)
        {
            RocksDbProvider rocksDb;
            switch (initConfig.DiagnosticMode)
            {
                case DiagnosticMode.RpcDb:
                    rocksDb = GetRocksDbProvider(DbModeHint.Persisted);
                    return new RpcDbProvider(_api.EthereumJsonSerializer, new BasicJsonRpcClient(new Uri(initConfig.RpcDbUrl), _api.EthereumJsonSerializer, _api.LogManager), _api.LogManager, rocksDb);
                case DiagnosticMode.ReadOnlyDb:
                    rocksDb = GetRocksDbProvider(DbModeHint.Persisted);
                    return new ReadOnlyDbProvider(rocksDb, storeReceipts);
                case DiagnosticMode.MemDb:
                    return new MemDbProvider();
                default:
                    return GetRocksDbProvider(DbModeHint.Mem);
            }
        }

        private RocksDbProvider GetRocksDbProvider(DbModeHint dbModeHint)
        {
            return new RocksDbProvider(dbModeHint);
        }
    }
}
