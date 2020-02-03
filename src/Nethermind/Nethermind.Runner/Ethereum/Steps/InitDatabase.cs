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

using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Store;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency]
    public class InitDatabase : IStep
    {
        private readonly EthereumRunnerContext _context;

        public InitDatabase(EthereumRunnerContext context)
        {
            _context = context;
        }

        public async Task Execute()
        {
            /* sync */
            IDbConfig dbConfig = _context.Config<IDbConfig>();
            ISyncConfig syncConfig = _context.Config<ISyncConfig>();
            IInitConfig initConfig = _context.Config<IInitConfig>();

            foreach (PropertyInfo propertyInfo in typeof(IDbConfig).GetProperties())
            {
                if (_context.Logger.IsDebug) _context.Logger.Debug($"DB {propertyInfo.Name}: {propertyInfo.GetValue(dbConfig)}");
            }

            if (syncConfig.BeamSyncEnabled)
            {
                _context.DbProvider = new BeamSyncDbProvider(initConfig.BaseDbPath, dbConfig, _context.LogManager, initConfig.StoreReceipts || syncConfig.DownloadReceiptsInFastSync);
            }
            else
            {
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
            }

            _context.DisposeStack.Push(_context.DbProvider);

            // IDbProvider debugRecorder = new RocksDbProvider(Path.Combine(_context._initConfig.BaseDbPath, "debug"), dbConfig, _context._logManager, _context._initConfig.StoreTraces, _context._initConfig.StoreReceipts);
            // _context._dbProvider = new RpcDbProvider(_context._jsonSerializer, new BasicJsonRpcClient(KnownRpcUris.Localhost, _context._jsonSerializer, _context._logManager), _context._logManager, debugRecorder);

            // IDbProvider debugReader = new ReadOnlyDbProvider(new RocksDbProvider(Path.Combine(_context._initConfig.BaseDbPath, "debug"), dbConfig, _context._logManager, _context._initConfig.StoreTraces, _context._initConfig.StoreReceipts), false);
            // _context._dbProvider = debugReader;
        }
    }
}