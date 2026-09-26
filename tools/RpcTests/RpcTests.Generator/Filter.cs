// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Generator;

public class Filter(ExecutionArgs args)
{
    private static JsonNode? GetParams(JsonNode request) => request["params"] switch
    {
        JsonObject paramsObject => paramsObject,
        JsonArray { Count: 0 } => null,
        JsonArray { Count: 1 } paramsArray => paramsArray[0],
        _ => null
    };

    public bool IncludeRequest(JsonNode request)
    {
        if (request["method"] is not { } method)
            return false;
        if (args.Methods is { Count: > 0 } allowed && !allowed.Contains(method.ToString()))
            return false;

        if (GetParams(request) is not { } @params)
            return true;

        if ((args.MinBlocks is not null || args.MaxBlocks is not null) &&
            method.ToString() == "eth_getLogs")
        {
            long? fromBlock = @params["fromBlock"] is { } fromBlockJson ? Convert.ToInt64(fromBlockJson.ToString(), 16) : null;
            long? toBlock = @params["toBlock"] is { } toBlockJson ? Convert.ToInt64(toBlockJson.ToString(), 16) : null;
            long range = fromBlock is null || toBlock is null ? 0 : toBlock.Value - fromBlock.Value;

            if (args.MinBlocks is { } minBlocks && range < minBlocks) return false;
            if (args.MaxBlocks is { } maxBlocks && range > maxBlocks) return false;
        }

        return true;
    }

    public bool IncludeResponse(JsonNode response)
    {
        if (args.MinResultLen is { } minResultLen &&
            response["result"] is JsonArray resultArray)
        {
            if (resultArray.Count < minResultLen) return false;
        }

        return true;
    }
}
