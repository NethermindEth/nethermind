/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;

namespace Nethermind.Store.Rpc
{
    public class RpcDb : IDb
    {
        private readonly string _dbName;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;
        private readonly IJsonRpcClient _rpcClient;
        private readonly IDb _recordDb;

        public RpcDb(string dbName, IJsonSerializer jsonSerializer, IJsonRpcClient rpcClient, ILogManager logManager, IDb recordDb)
        {
            _dbName = dbName;
            _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _recordDb = recordDb;
        }

        public void Dispose()
        {
            if (_recordDb is StateDb stateDb)
            {
                stateDb.Commit();
            }
        }

        public string Name { get; } = "RpcDb";

        public byte[] this[byte[] key]
        {
            get => GetThroughRpc(key);
            set => throw new InvalidOperationException("RPC DB does not support writes");
        }

        public void Remove(byte[] key)
        {
            throw new InvalidOperationException("RPC DB does not support writes");
        }

        public bool KeyExists(byte[] key)
        {
            return GetThroughRpc(key) != null;
        }

        public byte[][] GetAll() => _recordDb.GetAll();

        public void StartBatch()
        {
            throw new InvalidOperationException("RPC DB does not support writes");
        }

        public void CommitBatch()
        {
            throw new InvalidOperationException("RPC DB does not support writes");
        }

        private byte[] GetThroughRpc(byte[] key)
        {
            string responseJson = _rpcClient.Post("debug_getFromDb", _dbName, key.ToHexString()).Result;
            JsonRpcResponse response = _jsonSerializer.Deserialize<JsonRpcResponse>(responseJson);

            byte[] value = null;
            if (response.Result != null)
            {
                value = Bytes.FromHexString((string) response.Result);
                if (_recordDb != null)
                {
                    _recordDb[key] = value;
                }
            }

            return value;
        }
    }
}