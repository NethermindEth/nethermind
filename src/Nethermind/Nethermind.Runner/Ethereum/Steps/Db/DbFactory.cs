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
using Nethermind.Api;
using Nethermind.Db;
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
        
        public DbFactory(
            IJsonSerializer serializer, 
            IJsonRpcClient client, 
            ILogManager logManager,
            IInitConfig initConfig)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _initConfig = initConfig ?? throw new ArgumentNullException(nameof(initConfig));
        }

        public IDb Create(Func<IDb> newRocksDb, bool createInMemoryWriteStore = true)
        {
            IDb rocksDb;
            switch (_initConfig.DiagnosticMode)
            {
                case DiagnosticMode.RpcDb:
                    rocksDb = newRocksDb();
                    return new ReadOnlyDb(new RpcDb(rocksDb.Name, _serializer, _client, _logManager, rocksDb), createInMemoryWriteStore);
                case DiagnosticMode.ReadOnlyDb:
                    rocksDb = newRocksDb();
                    return new ReadOnlyDb(rocksDb, createInMemoryWriteStore); ;
                case DiagnosticMode.MemDb:
                    return new MemDb();
                default:
                    return newRocksDb();
            }
        }
    }
}
