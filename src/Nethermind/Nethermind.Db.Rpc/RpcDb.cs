// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Db.Rpc
{
    public class RpcDb(
        string dbName,
        IJsonSerializer jsonSerializer,
        IJsonRpcClient rpcClient,
        ILogManager logManager,
        IDb recordDb)
        : IDb
    {
        private readonly IJsonSerializer _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        private readonly ILogger _logger = logManager?.GetClassLogger<RpcDb>() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IJsonRpcClient _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));

        public void Dispose()
        {
            _logger.Info($"Disposing RPC DB {Name}");
            recordDb.Dispose();
        }

        public string Name => "RpcDb";

        public byte[]? this[ReadOnlySpan<byte> key]
        {
            get => Get(key);
            set => Set(key, value);
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => ThrowWritesNotSupported();
        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => GetThroughRpc(key);
        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
        {
            get
            {
                KeyValuePair<byte[], byte[]?>[] pairs = new KeyValuePair<byte[], byte[]?>[keys.Length];
                for (int i = 0; i < keys.Length; i++)
                {
                    byte[] key = keys[i];
                    pairs[i] = new KeyValuePair<byte[], byte[]?>(key, GetThroughRpc(key));
                }

                return pairs;
            }
        }

        public void Remove(ReadOnlySpan<byte> key) => ThrowWritesNotSupported();
        public bool KeyExists(ReadOnlySpan<byte> key) => GetThroughRpc(key) is not null;
        public void Flush(bool onlyWal = false) { }
        public void Clear() { }
        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => recordDb.GetAll();
        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => recordDb.GetAllKeys();
        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => recordDb.GetAllValues();
        public IWriteBatch StartWriteBatch() => throw new InvalidOperationException("RPC DB does not support writes");

        private byte[]? GetThroughRpc(ReadOnlySpan<byte> key)
        {
            string? responseJson = _rpcClient.Post("debug_getFromDb", dbName, key.ToHexString()).Result;
            if (responseJson is null)
            {
                return null;
            }

            JsonRpcSuccessResponse response = _jsonSerializer.Deserialize<JsonRpcSuccessResponse>(responseJson)
                ?? throw new JsonException("RPC DB response decoding returned null.");

            byte[]? value = null;
            if (response.Result is string result)
            {
                value = Bytes.FromHexString(result);
                recordDb[key] = value;
            }
            else if (response.Result is JsonElement { ValueKind: JsonValueKind.String } resultElement)
            {
                string? resultText = resultElement.GetString();
                if (resultText is not null)
                {
                    value = Bytes.FromHexString(resultText);
                    recordDb[key] = value;
                }
            }

            return value;
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags writeFlags) => ThrowWritesNotSupported();
        private static void ThrowWritesNotSupported() => throw new InvalidOperationException("RPC DB does not support writes");
        public void DangerousReleaseMemory(in ReadOnlySpan<byte> span) { }
    }
}
