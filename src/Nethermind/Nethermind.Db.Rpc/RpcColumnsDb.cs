// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Db.Rpc
{
    public class RpcColumnsDb<T> : IColumnsDb<T> where T : struct, Enum
    {
        private readonly string _dbName;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IJsonRpcClient _rpcClient;
        private readonly ILogManager _logManager;
        private readonly IColumnsDb<T> _recordDb;

        public RpcColumnsDb(
            string dbName,
            IJsonSerializer jsonSerializer,
            IJsonRpcClient rpcClient,
            ILogManager logManager,
            IColumnsDb<T> recordDb
        )
        {
            _dbName = dbName;
            _jsonSerializer = jsonSerializer;
            _rpcClient = rpcClient;
            _logManager = logManager;
            _recordDb = recordDb;
        }

        public IDb GetColumnDb(T key)
        {
            string dbName = _dbName + key;
            IDb column = _recordDb.GetColumnDb(key);
            return new RpcDb(dbName, _jsonSerializer, _rpcClient, _logManager, column);
        }

        public IEnumerable<T> ColumnKeys => Enum.GetValues<T>();

        public IColumnsWriteBatch<T> StartWriteBatch()
        {
            return new InMemoryColumnWriteBatch<T>(this);
        }
    }
}
