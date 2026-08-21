// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Bootnode;

internal static class JsonRpcEndpoint
{
    public static IResult Handle(JsonElement payload, DiscoveredNodeStore store, BootnodeStatus status)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
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
            "bootnode_activeNodes" => JsonRpcResponse.Success(id, store.GetActiveNodes()),
            "bootnode_allNodes" => JsonRpcResponse.Success(id, store.GetAllNodes()),
            "bootnode_status" => JsonRpcResponse.Success(id, status.CreateStatus(store.CreateSnapshot())),
            "bootnode_nodeInfo" => JsonRpcResponse.Success(id, status.Identity),
            _ => JsonRpcResponse.Failure(id, -32601, $"Method not found: {method}")
        };
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
