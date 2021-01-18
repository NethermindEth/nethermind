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

        public Span<byte> GetSpan(byte[] key) => this[key].AsSpan();

        public void DangerousReleaseMemory(in Span<byte> span)
        {
            
        }
    }
}
