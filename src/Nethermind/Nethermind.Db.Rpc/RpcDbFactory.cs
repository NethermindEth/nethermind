// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        public IColumnsDb<T> CreateColumnsDb<T>(RocksDbSettings rocksDbSettings) where T : struct, Enum
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

        public string GetFullDbPath(RocksDbSettings rocksDbSettings) => _wrappedRocksDbFactory.GetFullDbPath(rocksDbSettings);

        private IDb WrapWithRpc(IDb db) =>
            new ReadOnlyDb(new RpcDb(db.Name, _jsonSerializer, _jsonRpcClient, _logManager, db), true);
    }
}
