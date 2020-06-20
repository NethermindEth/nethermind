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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class InitDatabase : IStep
    {
        private readonly EthereumRunnerContext _context;

        public InitDatabase(EthereumRunnerContext context)
        {
            _context = context;
        }

        public async Task Execute(CancellationToken _)
        {
            ILogger logger = _context.LogManager.GetClassLogger();
            
            /* sync */
            IDbConfig dbConfig = _context.Config<IDbConfig>();
            ISyncConfig syncConfig = _context.Config<ISyncConfig>();
            IInitConfig initConfig = _context.Config<IInitConfig>();

            foreach (PropertyInfo propertyInfo in typeof(IDbConfig).GetProperties())
            {
                if (logger.IsDebug) logger.Debug($"DB {propertyInfo.Name}: {propertyInfo.GetValue(dbConfig)}");
            }

            _context.DbProvider = await GetDbProvider(initConfig, dbConfig, initConfig.StoreReceipts || syncConfig.DownloadReceiptsInFastSync);
            if (syncConfig.BeamSync)
            {
                _context.SyncModeSelector = new PendingSyncModeSelector();
                BeamSyncDbProvider beamSyncProvider = new BeamSyncDbProvider(_context.SyncModeSelector, _context.DbProvider, _context.Config<ISyncConfig>(), _context.LogManager);
                _context.DbProvider = beamSyncProvider;
            }
        }

        private async Task<IDbProvider> GetDbProvider(IInitConfig initConfig, IDbConfig dbConfig, bool storeReceipts)
        {
            RocksDbProvider rocksDb;
            switch (initConfig.DiagnosticMode)
            {
                case DiagnosticMode.RpcDb:
                    rocksDb = await GetRocksDbProvider(dbConfig, Path.Combine(initConfig.BaseDbPath, "debug"), storeReceipts);
                    return new RpcDbProvider(_context.EthereumJsonSerializer, new BasicJsonRpcClient(new Uri(initConfig.RpcDbUrl), _context.EthereumJsonSerializer, _context.LogManager), _context.LogManager, rocksDb);
                case DiagnosticMode.ReadOnlyDb:
                    rocksDb = await GetRocksDbProvider(dbConfig, Path.Combine(initConfig.BaseDbPath, "debug"), storeReceipts);
                    return new ReadOnlyDbProvider(rocksDb, storeReceipts);
                case DiagnosticMode.MemDb:
                    return new MemDbProvider();
                default:
                    return await GetRocksDbProvider(dbConfig, initConfig.BaseDbPath, storeReceipts);
            }
        }

        private async Task<RocksDbProvider> GetRocksDbProvider(IDbConfig dbConfig, string basePath, bool useReceiptsDb)
        {
            IInitConfig initConfig = _context.Config<IInitConfig>();
            RocksDbProvider debugRecorder = new RocksDbProvider(_context.LogManager, _context.Config<INdmConfig>().Enabled);
            ThisNodeInfo.AddInfo("DB location  :", $"{basePath}");
            await debugRecorder.Init(basePath, dbConfig, useReceiptsDb);
            return debugRecorder;
        }
    }
}