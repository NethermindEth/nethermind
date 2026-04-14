// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Db.Rpc
{
    public class RpcColumnsDb<T>(
        string dbName,
        IJsonSerializer jsonSerializer,
        IJsonRpcClient rpcClient,
        ILogManager logManager,
        IColumnsDb<T> recordDb
        ) : IColumnsDb<T> where T : struct, Enum
    {
        private readonly string _dbName = dbName;
        private readonly IJsonSerializer _jsonSerializer = jsonSerializer;
        private readonly IJsonRpcClient _rpcClient = rpcClient;
        private readonly ILogManager _logManager = logManager;
        private readonly IColumnsDb<T> _recordDb = recordDb;

        public IDb GetColumnDb(T key)
        {
            string dbName = _dbName + key;
            IDb column = _recordDb.GetColumnDb(key);
            return new RpcDb(dbName, _jsonSerializer, _rpcClient, _logManager, column);
        }

        public IEnumerable<T> ColumnKeys => Enum.GetValues<T>();

        public IColumnsWriteBatch<T> StartWriteBatch() => new InMemoryColumnWriteBatch<T>(this);

        public IColumnDbSnapshot<T> CreateSnapshot() => throw new NotSupportedException("Snapshot not implemented");

        public void Dispose() { }
        public void Flush(bool onlyWal = false) { }
    }
}
