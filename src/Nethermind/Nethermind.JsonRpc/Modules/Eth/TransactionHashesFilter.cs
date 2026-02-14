// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Eth;

public class TransactionHashesFilter : IJsonRpcParam
{
    public Hash256[]? TransactionHashes { get; set; }

    public void ReadJson(JsonElement jsonValue, JsonSerializerOptions options)
    {
        if (jsonValue.TryGetProperty("transactionHashes", out JsonElement hashesElement) &&
            hashesElement.ValueKind == JsonValueKind.Array)
        {
            var hashes = new Hash256[hashesElement.GetArrayLength()];
            int index = 0;
            
            foreach (JsonElement hashElement in hashesElement.EnumerateArray())
            {
                if (hashElement.ValueKind == JsonValueKind.String)
                {
                    string? hashString = hashElement.GetString();
                    if (hashString is not null)
                    {
                        hashes[index++] = new Hash256(hashString);
                    }
                }
            }
            
            TransactionHashes = hashes;
        }
    }
}
