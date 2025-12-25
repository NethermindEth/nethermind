// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Eth;

public class Filter : IJsonRpcParam
{
    public AddressAsKey[]? Address { get; set; }

    public BlockParameter FromBlock { get; set; }

    public BlockParameter ToBlock { get; set; }

    public IEnumerable<Hash256[]?>? Topics { get; set; }

    public bool UseIndex { get; set; } = true;

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

            if (hasBlockHash && blockHashElement.ValueKind != JsonValueKind.Null)
            {
                if (hasFromBlock || hasToBlock)
                {
                    throw new ArgumentException("cannot specify both BlockHash and FromBlock/ToBlock, choose one or the other");
                }

                FromBlock = new(new Hash256(blockHashElement.ToString()));
                ToBlock = FromBlock;
            }
            else
            {
                FromBlock = hasFromBlock && fromBlockElement.ValueKind != JsonValueKind.Null
                    ? BlockParameterConverter.GetBlockParameter(fromBlockElement.ToString())
                    : BlockParameter.Earliest;
                ToBlock = hasToBlock && toBlockElement.ValueKind != JsonValueKind.Null
                    ? BlockParameterConverter.GetBlockParameter(toBlockElement.ToString())
                    : BlockParameter.Latest;
            }

            if (filter.TryGetProperty("address"u8, out JsonElement addressElement))
            {
                Address = GetAddress(addressElement, options);
            }

            if (filter.TryGetProperty("topics"u8, out JsonElement topicsElement) && topicsElement.ValueKind == JsonValueKind.Array)
            {
                Topics = GetTopics(topicsElement, options);
            }

            if (filter.TryGetProperty("useIndex"u8, out JsonElement useIndex))
            {
                UseIndex = useIndex.ValueKind switch
                {
                    JsonValueKind.False => false,
                    JsonValueKind.True => true,
                    _ => UseIndex
                };
            }
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private static AddressAsKey[]? GetAddress(JsonElement token, JsonSerializerOptions options)
    {
        switch (token.ValueKind)
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                return [new AddressAsKey(new Address(token.ToString()))];
            case JsonValueKind.Array:
                var enumerator = token.EnumerateArray();
                List<AddressAsKey> result = new();
                while (enumerator.MoveNext())
                {
                    result.Add(new(new Address(enumerator.Current.ToString())));
                }

                return result.ToArray();
            default:
                throw new ArgumentException("invalid address field");
        }
    }

    private static IEnumerable<Hash256[]?>? GetTopics(JsonElement? array, JsonSerializerOptions options)
    {
        if (array is null)
        {
            yield break;
        }

        foreach (var token in array.GetValueOrDefault().EnumerateArray())
        {
            switch (token.ValueKind)
            {
                case JsonValueKind.Undefined or JsonValueKind.Null:
                    yield return null;
                    break;
                case JsonValueKind.String:
                    yield return [new Hash256(token.GetString()!)];
                    break;
                case JsonValueKind.Array:
                    JsonElement.ArrayEnumerator enumerator = token.EnumerateArray();
                    List<Hash256> result = new();
                    while (enumerator.MoveNext())
                    {
                        result.Add(new(enumerator.Current.ToString()));
                    }

                    yield return result.ToArray();
                    break;
                default:
                    throw new ArgumentException("invalid topics field");
            }
        }
    }
}
