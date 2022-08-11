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

        Topics = topics == null ? null : GetTopics(filter["topics"] as JArray);
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
