// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.LogIndex;

public class LogIndexKeysRequest : IJsonRpcParam
{
    public byte[] Key { get; set; } = [];

    public BlockParameter FromBlock { get; set; } = BlockParameter.Earliest;

    public BlockParameter ToBlock { get; set; } = BlockParameter.Latest;

    public bool IncludeValues { get; set; } = false;

    public void ReadJson(JsonElement json, JsonSerializerOptions options)
    {
        if (json.TryGetProperty("fromBlock"u8, out JsonElement fromBlockElement))
            FromBlock = BlockParameterConverter.GetBlockParameter(fromBlockElement.ToString());

        if (json.TryGetProperty("toBlock"u8, out JsonElement toBlockElement))
            ToBlock = BlockParameterConverter.GetBlockParameter(toBlockElement.ToString());

        if (json.TryGetProperty("key"u8, out JsonElement keyElement) && keyElement.ValueKind == JsonValueKind.String)
            Key = Bytes.FromHexString(keyElement.ToString());

        if (json.TryGetProperty("includeValues"u8, out JsonElement valuesElement))
            IncludeValues = valuesElement.GetBoolean();
    }
}

// TODO: add forward/backward sync status?
public record LogIndexStatus(int? FromBlock, int? ToBlock);
