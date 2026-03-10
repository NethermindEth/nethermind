// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Eth;

public class TransactionHashesFilter : IJsonRpcParam
{
    public HashSet<ValueHash256>? TransactionHashes { get; set; }

    public void ReadJson(JsonElement jsonValue, JsonSerializerOptions options)
    {
        if (jsonValue.TryGetProperty("transactionHashes", out JsonElement hashesElement) &&
            hashesElement.ValueKind == JsonValueKind.Array)
        {
            // Validate max 200 hashes BEFORE allocating anything
            if (hashesElement.GetArrayLength() > 200)
            {
                throw new ArgumentException("Cannot subscribe to more than 200 transaction hashes at once.");
            }

            var hashes = new HashSet<ValueHash256>();

            foreach (JsonElement hashElement in hashesElement.EnumerateArray())
            {
                if (hashElement.ValueKind == JsonValueKind.String)
                {
                    string? hashString = hashElement.GetString();
                    if (hashString is not null)
                    {
                        hashes.Add(new ValueHash256(hashString));
                    }
                }
            }

            TransactionHashes = hashes;
        }
    }
}
