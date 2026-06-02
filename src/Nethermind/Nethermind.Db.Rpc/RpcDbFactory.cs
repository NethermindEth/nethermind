// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Db.Rpc
{
    public class RpcDbFactory(
        IDbFactory wrappedRocksDbFactory,
        IJsonSerializer jsonSerializer,
        IJsonRpcClient jsonRpcClient,
        ILogManager logManager) : IDbFactory
    {
        private readonly IDbFactory _wrappedRocksDbFactory = wrappedRocksDbFactory;
        private readonly IJsonSerializer _jsonSerializer = jsonSerializer;
        private readonly IJsonRpcClient _jsonRpcClient = jsonRpcClient;
        private readonly ILogManager _logManager = logManager;

        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
        {
            IColumnsDb<T> rocksDb = _wrappedRocksDbFactory.CreateColumnsDb<T>(dbSettings);
            return new ReadOnlyColumnsDb<T>(
                new RpcColumnsDb<T>(dbSettings.DbName, _jsonSerializer, _jsonRpcClient, _logManager, rocksDb),
                true);
        }

        public IDb CreateDb(DbSettings dbSettings)
        {
            IDb rocksDb = _wrappedRocksDbFactory.CreateDb(dbSettings);
            return WrapWithRpc(rocksDb);
        }

        public string GetFullDbPath(DbSettings dbSettings) => _wrappedRocksDbFactory.GetFullDbPath(dbSettings);

        private IDb WrapWithRpc(IDb db) =>
            new ReadOnlyDb(new RpcDb(db.Name, _jsonSerializer, _jsonRpcClient, _logManager, db), true);
    }
}
