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
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Runner.Ethereum.Steps.Db
{
    public class DbFactory : IDbFactory
    {
        private readonly IJsonSerializer _serializer;
        private readonly IJsonRpcClient _client;
        private readonly ILogManager _logManager;
        private readonly IInitConfig _initConfig;
        private readonly IDbConfig _dbConfig;
        
        public DbFactory(
            IJsonSerializer serializer, 
            IJsonRpcClient client, 
            ILogManager logManager,
            IInitConfig initConfig,
            IDbConfig dbConfig)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _initConfig = initConfig ?? throw new ArgumentNullException(nameof(initConfig));
            _dbConfig = dbConfig ?? throw new ArgumentNullException(nameof(dbConfig));
        }

        public IDb Create(Func<string, IConfig, IDb> newRocksDb, bool createInMemoryWriteStore = true)
        {
            IDb rocksDb;
            switch (_initConfig.DiagnosticMode)
            {
                case DiagnosticMode.RpcDb:
                    rocksDb = newRocksDb(Path.Combine(_initConfig.BaseDbPath, "debug"), _dbConfig);
                    return new ReadOnlyDb(new RpcDb(rocksDb.Name, _serializer, _client, _logManager, rocksDb), createInMemoryWriteStore);
                case DiagnosticMode.ReadOnlyDb:
                    rocksDb = newRocksDb(Path.Combine(_initConfig.BaseDbPath, "debug"), _dbConfig);
                    return new ReadOnlyDb(rocksDb, createInMemoryWriteStore); ;
                case DiagnosticMode.MemDb:
                    return new MemDb();
                default:
                    return newRocksDb(_initConfig.BaseDbPath, _dbConfig);
            }
        }

        public IDb Create(string dbName, bool createInMemoryWriteStore = true)
        {
            return Create((string basePath, IConfig config) => new SimpleRocksDb(basePath, dbName, _dbConfig, _logManager), createInMemoryWriteStore);
        }
    }
}
