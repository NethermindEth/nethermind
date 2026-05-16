// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace RpcTestsGen;

public class Filter(ExecutionArgs args)
{
    public bool IncludeRequest(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (args.Exclude is { } exclude && line.Contains(exclude, StringComparison.Ordinal)) return false;
        if (args.Include is { } include && !line.Contains(include, StringComparison.Ordinal)) return false;

        return true;
    }

    private static JsonNode? GetParams(JsonNode request) => request["params"] switch
    {
        JsonObject paramsObject => paramsObject,
        JsonArray {Count: 0} => null,
        JsonArray {Count: 1} paramsArray => paramsArray[0],
        _ => null
    };

    public bool IncludeRequest(JsonNode request)
    {
        if (GetParams(request) is not {} @params)
        {
            return true;
        }

        if ((args.MinBlocks is not null || args.MaxBlocks is not null) &&
            request["method"]?.ToString() == "eth_getLogs")
        {
            int? fromBlock = @params["fromBlock"] is {} fromBlockJson ? Convert.ToInt32(fromBlockJson.ToString(), 16) : null;
            int? toBlock = @params["toBlock"] is {} toBlockJson ? Convert.ToInt32(toBlockJson.ToString(), 16) : null;
            int range = fromBlock is null || toBlock is null ? 0 : toBlock.Value - fromBlock.Value;

            if (args.MinBlocks is {} minBlocks && range < minBlocks) return false;
            if (args.MaxBlocks is {} maxBlocks && range > maxBlocks) return false;
        }

        return true;
    }

    public bool IncludeResponse(JsonNode response)
    {
        if (args.MinResultLen is {} minResultLen &&
            response["result"] is JsonArray resultArray)
        {
            if (resultArray.Count < minResultLen) return false;
        }

        return true;
    }
}
