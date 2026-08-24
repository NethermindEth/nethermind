// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Bootnode;

internal static class JsonRpcEndpoint
{
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

            List<JsonRpcResponse> responses = [];
            foreach (JsonElement request in payload.EnumerateArray())
            {
                responses.Add(HandleSingle(request, store, status));
            }

            return Results.Json(responses);
        }

        return Results.Json(HandleSingle(payload, store, status));
    }

    private static JsonRpcResponse HandleSingle(JsonElement payload, DiscoveredNodeStore store, BootnodeStatus status)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return JsonRpcResponse.Failure(null, -32600, "Invalid request");
        }

        object? id = TryReadId(payload);
        if (!payload.TryGetProperty("method", out JsonElement methodElement) ||
            methodElement.ValueKind != JsonValueKind.String)
        {
            return JsonRpcResponse.Failure(id, -32600, "Invalid request");
        }

        string? method = methodElement.GetString();
        return method switch
        {
            "bootnode_activeNodes" => HandleNodes(payload, id, store, activeOnly: true),
            "bootnode_allNodes" => HandleNodes(payload, id, store, activeOnly: false),
            "bootnode_status" => JsonRpcResponse.Success(id, status.CreateStatus(store.CreateSnapshot())),
            "bootnode_nodeInfo" => JsonRpcResponse.Success(id, status.Identity),
            _ => JsonRpcResponse.Failure(id, -32601, $"Method not found: {method}")
        };
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

    private static object? TryReadId(JsonElement payload)
    {
        if (!payload.TryGetProperty("id", out JsonElement idElement))
        {
            return null;
        }

        return idElement.ValueKind switch
        {
            JsonValueKind.Number when idElement.TryGetInt64(out long id) => id,
            JsonValueKind.String => idElement.GetString(),
            JsonValueKind.Null => null,
            _ => idElement.GetRawText()
        };
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
