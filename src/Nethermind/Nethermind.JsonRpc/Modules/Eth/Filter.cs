// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Modules.Eth;

public class Filter : IJsonRpcParam
{
    public object? Address { get; set; }

    public BlockParameter? FromBlock { get; set; }

    public BlockParameter? ToBlock { get; set; }

    public IEnumerable<object?>? Topics { get; set; }

    public void ReadJson(JsonSerializer serializer, string json)
    {
        var filter = serializer.Deserialize<JObject>(json.ToJsonTextReader());
        var blockHash = filter["blockHash"]?.Value<string>();

        if (blockHash is null)
        {
            FromBlock = BlockParameterConverter.GetBlockParameter(filter["fromBlock"]?.Value<string>());
            ToBlock = BlockParameterConverter.GetBlockParameter(filter["toBlock"]?.Value<string>());
        }
        else
            FromBlock =
                ToBlock = BlockParameterConverter.GetBlockParameter(blockHash);

        Address = GetAddress(filter["address"]);

        var topics = filter["topics"] as JArray;

        Topics = topics is null ? null : GetTopics(filter["topics"] as JArray);
    }

    private static object? GetAddress(JToken? token) => GetSingleOrMany(token);

    private static IEnumerable<object?> GetTopics(JArray? array)
    {
        if (array is null)
        {
            yield break;
        }

        foreach (JToken token in array)
        {
            yield return GetSingleOrMany(token);
        }
    }

    private static object? GetSingleOrMany(JToken? token) => token switch
    {
        null => null,
        JArray _ => token.ToObject<IEnumerable<string>>(),
        _ => token.Value<string>(),
    };
}
