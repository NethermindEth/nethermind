//  Copyright (c) 2021 Demerzel Solutions Limited
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

using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Db.Rpc
{
    public class RpcDbFactory : IRocksDbFactory, IMemDbFactory
    {
        private readonly IMemDbFactory _wrappedMemDbFactory;
        private readonly IRocksDbFactory _wrappedRocksDbFactory;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IJsonRpcClient _jsonRpcClient;
        private readonly ILogManager _logManager;

        public RpcDbFactory(
            IMemDbFactory wrappedMemDbFactory,
            IRocksDbFactory wrappedRocksDbFactory,
            IJsonSerializer jsonSerializer,
            IJsonRpcClient jsonRpcClient,
            ILogManager logManager)
        {
            _wrappedMemDbFactory = wrappedMemDbFactory;
            _wrappedRocksDbFactory = wrappedRocksDbFactory;
            _jsonSerializer = jsonSerializer;
            _jsonRpcClient = jsonRpcClient;
            _logManager = logManager;
        }

        public IColumnsDb<T> CreateColumnsDb<T>(RocksDbSettings rocksDbSettings)
        {
            var rocksDb = _wrappedRocksDbFactory.CreateColumnsDb<T>(rocksDbSettings);
            return new ReadOnlyColumnsDb<T>(new RpcColumnsDb<T>(rocksDbSettings.DbName, _jsonSerializer, _jsonRpcClient, _logManager, rocksDb), true);
        }

        public IColumnsDb<T> CreateColumnsDb<T>(string dbName)
        {
            var memDb = _wrappedMemDbFactory.CreateColumnsDb<T>(dbName);
            return new ReadOnlyColumnsDb<T>(new RpcColumnsDb<T>(dbName, _jsonSerializer, _jsonRpcClient, _logManager, memDb), true);
        }

        public IDb CreateDb(RocksDbSettings rocksDbSettings)
        {
            var rocksDb = _wrappedRocksDbFactory.CreateDb(rocksDbSettings);
            return WrapWithRpc(rocksDb);
        }

        public IDb CreateDb(string dbName)
        {
            var memDb = _wrappedMemDbFactory.CreateDb(dbName);
            return WrapWithRpc(memDb);
        }
        
        private IDb WrapWithRpc(IDb db)
        {
            return new ReadOnlyDb(new RpcDb(db.Name, _jsonSerializer, _jsonRpcClient, _logManager, db), true);
        }
    }
}
