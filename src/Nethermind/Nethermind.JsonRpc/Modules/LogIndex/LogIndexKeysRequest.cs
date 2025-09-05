// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.LogIndex;

public class LogIndexKeysRequest : IJsonRpcParam
{
    public Address? Address { get; set; }
    public Hash256? Topic { get; set; }
    public int? TopicIndex { get; set; }

    public BlockParameter FromBlock { get; set; } = BlockParameter.Earliest;

    public BlockParameter ToBlock { get; set; } = BlockParameter.Latest;

    public bool IncludeValues { get; set; } = false;

    public void ReadJson(JsonElement json, JsonSerializerOptions options)
    {
        if (json.TryGetProperty("fromBlock"u8, out JsonElement fromBlockElement))
            FromBlock = BlockParameterConverter.GetBlockParameter(fromBlockElement.ToString());

        if (json.TryGetProperty("toBlock"u8, out JsonElement toBlockElement))
            ToBlock = BlockParameterConverter.GetBlockParameter(toBlockElement.ToString());

        if (json.TryGetProperty("address"u8, out JsonElement addressElement) && addressElement.ValueKind == JsonValueKind.String)
            Address = new(Bytes.FromHexString(addressElement.ToString()));

        if (json.TryGetProperty("topic"u8, out JsonElement topicElement) && topicElement.ValueKind == JsonValueKind.String)
            Topic = new(Bytes.FromHexString(topicElement.ToString()));

        if (json.TryGetProperty("topicIndex"u8, out JsonElement topicIndexElement) && topicIndexElement.ValueKind == JsonValueKind.Number)
            TopicIndex = topicIndexElement.GetInt32();

        if (json.TryGetProperty("includeValues"u8, out JsonElement valuesElement))
            IncludeValues = valuesElement.GetBoolean();
    }
}
