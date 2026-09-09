// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Bootnode;

internal static class JsonRpcEndpoint
{
    private const string JsonRpcVersion = "2.0";
    internal const int MaxBatchSize = 16;

    public static IResult Handle(JsonElement payload, DiscoveredNodeStore store, BootnodeStatus status)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            int requestCount = payload.GetArrayLength();
            if (requestCount is 0 or > MaxBatchSize)
            {
                return Results.Json(JsonRpcResponse.Failure(null, -32600, $"Batch must contain between 1 and {MaxBatchSize} requests."));
            }

            List<JsonRpcResponse> responses = new(requestCount);
            foreach (JsonElement request in payload.EnumerateArray())
            {
                JsonRpcResponse? response = HandleSingle(request, store, status);
                if (response is not null)
                {
                    responses.Add(response);
                }
            }

            return responses.Count == 0 ? Results.NoContent() : Results.Json(responses);
        }

        JsonRpcResponse? singleResponse = HandleSingle(payload, store, status);
        return singleResponse is null ? Results.NoContent() : Results.Json(singleResponse);
    }

    private static JsonRpcResponse? HandleSingle(JsonElement payload, DiscoveredNodeStore store, BootnodeStatus status)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return JsonRpcResponse.Failure(null, -32600, "Invalid request");
        }

        if (!TryReadId(payload, out bool hasId, out object? id))
        {
            return JsonRpcResponse.Failure(null, -32600, "Invalid request");
        }

        if (!payload.TryGetProperty("jsonrpc", out JsonElement versionElement) ||
            versionElement.ValueKind != JsonValueKind.String ||
            versionElement.GetString() != JsonRpcVersion)
        {
            return JsonRpcResponse.Failure(hasId ? id : null, -32600, "Invalid request");
        }

        if (!payload.TryGetProperty("method", out JsonElement methodElement) ||
            methodElement.ValueKind != JsonValueKind.String)
        {
            return JsonRpcResponse.Failure(hasId ? id : null, -32600, "Invalid request");
        }

        string? method = methodElement.GetString();
        JsonRpcResponse response = method switch
        {
            "bootnode_activeNodes" => HandleNodes(payload, id, store, activeOnly: true),
            "bootnode_allNodes" => HandleNodes(payload, id, store, activeOnly: false),
            "bootnode_status" => JsonRpcResponse.Success(id, status.CreateStatus(store.CreateSnapshot())),
            "bootnode_nodeInfo" => JsonRpcResponse.Success(id, status.Identity),
            _ => JsonRpcResponse.Failure(id, -32601, $"Method not found: {method}")
        };

        return hasId ? response : null;
    }

    private static JsonRpcResponse HandleNodes(JsonElement payload, object? id, DiscoveredNodeStore store, bool activeOnly)
    {
        if (!TryReadPagination(payload, out int offset, out int limit, out string error))
        {
            return JsonRpcResponse.Failure(id, -32602, error);
        }

        NodeDto[] nodes = activeOnly
            ? store.GetActiveNodes(offset, limit)
            : store.GetAllNodes(offset, limit);
        return JsonRpcResponse.Success(id, nodes);
    }

    private static bool TryReadPagination(JsonElement payload, out int offset, out int limit, out string error)
    {
        offset = 0;
        limit = DiscoveredNodeStore.DefaultNodePageSize;

        if (!payload.TryGetProperty("params", out JsonElement parameters))
        {
            return DiscoveredNodeStore.TryValidatePagination(offset, limit, out error);
        }

        if (parameters.ValueKind == JsonValueKind.Object)
        {
            bool hasOffset = false;
            bool hasLimit = false;
            foreach (JsonProperty parameter in parameters.EnumerateObject())
            {
                switch (parameter.Name)
                {
                    case "offset" when !hasOffset:
                        if (!parameter.Value.TryGetInt32(out offset))
                        {
                            error = "offset must be an integer.";
                            return false;
                        }

                        hasOffset = true;
                        break;
                    case "limit" when !hasLimit:
                        if (!parameter.Value.TryGetInt32(out limit))
                        {
                            error = "limit must be an integer.";
                            return false;
                        }

                        hasLimit = true;
                        break;
                    case "offset":
                    case "limit":
                        error = $"Duplicate pagination parameter '{parameter.Name}'.";
                        return false;
                    default:
                        error = $"Unknown pagination parameter '{parameter.Name}'.";
                        return false;
                }
            }

            return DiscoveredNodeStore.TryValidatePagination(offset, limit, out error);
        }

        if (parameters.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement parameter in parameters.EnumerateArray())
            {
                if (index > 1)
                {
                    error = "params accepts at most offset and limit.";
                    return false;
                }

                if (!parameter.TryGetInt32(out int value))
                {
                    error = index == 0 ? "offset must be an integer." : "limit must be an integer.";
                    return false;
                }

                if (index == 0)
                {
                    offset = value;
                }
                else
                {
                    limit = value;
                }

                index++;
            }

            return DiscoveredNodeStore.TryValidatePagination(offset, limit, out error);
        }

        error = "params must be an object or array.";
        return false;
    }

    private static bool TryReadId(JsonElement payload, out bool hasId, out object? id)
    {
        hasId = payload.TryGetProperty("id", out JsonElement idElement);
        if (!hasId)
        {
            id = null;
            return true;
        }

        switch (idElement.ValueKind)
        {
            case JsonValueKind.Number:
                id = idElement.Clone();
                return true;
            case JsonValueKind.String:
                id = idElement.GetString();
                return true;
            case JsonValueKind.Null:
                id = null;
                return true;
            default:
                id = null;
                return false;
        }
    }
}

internal sealed record JsonRpcResponse(
    string Jsonrpc,
    object? Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonRpcError? Error)
{
    public static JsonRpcResponse Success(object? id, object result) => new("2.0", id, result, null);

    public static JsonRpcResponse Failure(object? id, int code, string message) => new("2.0", id, null, new JsonRpcError(code, message));
}

internal sealed record JsonRpcError(int Code, string Message);
