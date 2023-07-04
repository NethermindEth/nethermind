// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Attributes;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Db.Rpc
{
    public class RpcColumnsDb<T> : RpcDb, IColumnsDb<T>
    {
        public RpcColumnsDb(string dbName, IJsonSerializer jsonSerializer, IJsonRpcClient rpcClient, ILogManager logManager, IDb recordDb) : base(dbName, jsonSerializer, rpcClient, logManager, recordDb)
        {
        }

        [Todo(Improve.MissingFunctionality, "Need to implement RPC method for column DB's")]
        public IDbWithSpan GetColumnDb(T key) => this;

        [Todo(Improve.MissingFunctionality, "Need to implement RPC method for column DB's")]
        public IEnumerable<T> ColumnKeys { get; } = Array.Empty<T>();

        public Span<byte> GetSpan(ReadOnlySpan<byte> key) => this[key].AsSpan();
        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            this[key] = value.ToArray();
        }

        public void DangerousReleaseMemory(in Span<byte> span)
        {

        }
    }
}
