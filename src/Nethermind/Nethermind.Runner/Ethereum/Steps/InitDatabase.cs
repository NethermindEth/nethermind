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
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Synchronization.BeamSync;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class InitDatabase : IStep
    {
        private readonly EthereumRunnerContext _context;

        public InitDatabase(EthereumRunnerContext context)
        {
            _context = context;
        }

        public async Task Execute()
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

            if (initConfig.UseMemDb)
            {
                _context.DbProvider = new MemDbProvider();
            }
            else
            {
                RocksDbProvider rocksDbProvider = new RocksDbProvider(_context.LogManager);
                await rocksDbProvider.Init(initConfig.BaseDbPath, dbConfig, initConfig.StoreReceipts || syncConfig.DownloadReceiptsInFastSync);
                _context.DbProvider = rocksDbProvider;
            }

            if (syncConfig.BeamSync)
            {
                BeamSyncDbProvider beamSyncProvider = new BeamSyncDbProvider(_context.DbProvider, "processor DB", _context.LogManager);
                _context.DbProvider = beamSyncProvider;
                _context.NodeDataConsumer = beamSyncProvider.NodeDataConsumer;
            }

            // RocksDbProvider debugRecorder = new RocksDbProvider(_context.LogManager);
            // await debugRecorder.Init(Path.Combine(initConfig.BaseDbPath, "debug"), dbConfig, initConfig.StoreReceipts);
            // var host = KnownRpcUris.Localhost; // new Uri("http://host:Port");
            // _context.DbProvider = new RpcDbProvider(_context.EthereumJsonSerializer, new BasicJsonRpcClient(host, _context.EthereumJsonSerializer, _context.LogManager), _context.LogManager, debugRecorder);
            // _context.DbProvider = new ReadOnlyDbProvider(debugRecorder, false);;
        }
    }
}