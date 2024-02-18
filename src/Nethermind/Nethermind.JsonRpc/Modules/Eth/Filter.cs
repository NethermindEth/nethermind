// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;

using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Eth;

public class Filter : IJsonRpcParam
{
    public object? Address { get; set; }

    public BlockParameter? FromBlock { get; set; }

    public BlockParameter? ToBlock { get; set; }

    public IEnumerable<object?>? Topics { get; set; }

    public void ReadJson(JsonElement filter, JsonSerializerOptions options)
    {
        JsonDocument doc = null;
        string blockHash = null;
        try
        {
            if (filter.ValueKind == JsonValueKind.String)
            {
                doc = JsonDocument.Parse(filter.GetString());
                filter = doc.RootElement;
            }

            if (filter.TryGetProperty("blockHash"u8, out JsonElement blockHashElement))
            {
                blockHash = blockHashElement.GetString();
            }

            if (blockHash is null)
            {
                filter.TryGetProperty("fromBlock"u8, out JsonElement fromBlockElement);
                FromBlock = BlockParameterConverter.GetBlockParameter(fromBlockElement.ToString());
                filter.TryGetProperty("toBlock"u8, out JsonElement toBlockElement);
                ToBlock = BlockParameterConverter.GetBlockParameter(toBlockElement.ToString());
            }
            else
            {
                FromBlock = ToBlock = BlockParameterConverter.GetBlockParameter(blockHash);
            }

            filter.TryGetProperty("address"u8, out JsonElement addressElement);
            Address = GetAddress(addressElement, options);

            if (filter.TryGetProperty("topics"u8, out JsonElement topicsElement) && topicsElement.ValueKind == JsonValueKind.Array)
            {
                Topics = GetTopics(topicsElement, options);
            }
            else
            {
                Topics = null;
            }
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private static object? GetAddress(JsonElement? token, JsonSerializerOptions options) => GetSingleOrMany(token, options);

    private static IEnumerable<object?> GetTopics(JsonElement? array, JsonSerializerOptions options)
    {
        if (array is null)
        {
            yield break;
        }

        foreach (var token in array.GetValueOrDefault().EnumerateArray())
        {
            yield return GetSingleOrMany(token, options);
        }
    }

    private static object? GetSingleOrMany(JsonElement? token, JsonSerializerOptions options) => token switch
    {
        null => null,
        { ValueKind: JsonValueKind.Undefined } _ => null,
        { ValueKind: JsonValueKind.Null } _ => null,
        { ValueKind: JsonValueKind.Array } _ => token.GetValueOrDefault().Deserialize<string[]>(options),
        _ => token.GetValueOrDefault().GetString(),
    };
}
