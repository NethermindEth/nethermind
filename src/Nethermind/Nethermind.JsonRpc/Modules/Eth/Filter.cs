// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Eth;

public class Filter : IJsonRpcParam
{
    public AddressAsKey[]? Address { get; set; }

    public Hash256? BlockHash { get; set; }

    public BlockParameter? FromBlock { get; set; }

    public BlockParameter? ToBlock { get; set; }

    public IEnumerable<Hash256[]?>? Topics { get; set; }

    public void ReadJson(JsonElement filter, JsonSerializerOptions options)
    {
        JsonDocument doc = null;
        try
        {
            if (filter.ValueKind == JsonValueKind.String)
            {
                doc = JsonDocument.Parse(filter.GetString());
                filter = doc.RootElement;
            }

            bool hasBlockHash = filter.TryGetProperty("blockHash"u8, out JsonElement blockHashElement);
            bool hasFromBlock = filter.TryGetProperty("fromBlock"u8, out JsonElement fromBlockElement);
            bool hasToBlock = filter.TryGetProperty("toBlock"u8, out JsonElement toBlockElement);

            if (hasBlockHash)
            {
                if (hasFromBlock || hasToBlock)
                {
                    throw new ArgumentException("either (fromBlock and toBlock) or blockHash have to be specified");
                }

                BlockHash = new Hash256(blockHashElement.ToString());
            }

            FromBlock = hasFromBlock ? new BlockParameter(LongConverter.FromString(fromBlockElement.ToString())) : BlockParameter.Earliest;
            ToBlock = hasToBlock ? new BlockParameter(LongConverter.FromString(toBlockElement.ToString())) : BlockParameter.Latest;


            if (filter.TryGetProperty("address"u8, out JsonElement addressElement))
            {
                Address = GetAddress(addressElement, options);
            }

            if (filter.TryGetProperty("topics"u8, out JsonElement topicsElement) && topicsElement.ValueKind == JsonValueKind.Array)
            {
                Topics = GetTopics(topicsElement, options);
            }
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private static AddressAsKey[]? GetAddress(JsonElement token, JsonSerializerOptions options) => token switch
    {
        { ValueKind: JsonValueKind.Undefined } _ => null,
        { ValueKind: JsonValueKind.Null } _ => null,
        { ValueKind: JsonValueKind.Array } _ => token.Deserialize<AddressAsKey[]>(options),
        { ValueKind: JsonValueKind.String } _ => [token.Deserialize<AddressAsKey>()],
        _ => throw new ArgumentException("invalid address field")
    };

    private static IEnumerable<Hash256[]?>? GetTopics(JsonElement? array, JsonSerializerOptions options)
    {
        if (array is null)
        {
            yield break;
        }

        foreach (var token in array.GetValueOrDefault().EnumerateArray())
        {
            yield return token switch
            {
                { ValueKind: JsonValueKind.Undefined } _ => null,
                { ValueKind: JsonValueKind.Null } _ => null,
                { ValueKind: JsonValueKind.Array } _ => token.Deserialize<Hash256[]>(options),
                _ => [new Hash256(token.GetString()!)],
            };
        }
    }
}
