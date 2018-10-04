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
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.Client;

namespace Nethermind.Store.Rpc
{
    public class RpcDb : IDb
    {
        private readonly string _dbName;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;
        private readonly IJsonRpcClient _rpcClient;
        private IDb _memDb = new MemDb();
        private HashSet<byte[]> _removedValues = new HashSet<byte[]>(Bytes.EqualityComparer);

        public RpcDb(string dbName, IJsonSerializer jsonSerializer, IJsonRpcClient rpcClient, ILogManager logManager)
        {
            _dbName = dbName;
            _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void Dispose()
        {
        }

        public byte[] this[byte[] key]
        {
            get
            {
                if (_removedValues.Contains(key)) return null;

                return _memDb[key] ?? GetThroughRpc(key);
            }
            set
            {
                _removedValues.Remove(key);
                _memDb[key] = value;
            }
        }

        public void Remove(byte[] key)
        {
            _removedValues.Add(key);
        }

        public void StartBatch()
        {
        }

        public void CommitBatch()
        {
        }

        private byte[] GetThroughRpc(byte[] key)
        {
            string response = _rpcClient.Post("debug_getFromDb", _dbName, key.ToHexString()).Result;
            return _jsonSerializer.Deserialize<byte[]>(response);
        }
    }
}