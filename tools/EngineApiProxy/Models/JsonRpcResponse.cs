// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nethermind.EngineApiProxy.Models;

public class JsonRpcResponse
{
    /// <summary>JSON-RPC 2.0 internal error code (-32603), used for proxy-side failures.</summary>
    public const int InternalErrorCode = -32603;

    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonNode? Id { get; set; }

    [JsonPropertyName("result")]
    public JsonNode? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }

    public JsonRpcResponse()
    {
    }

    public JsonRpcResponse(JsonNode? id, JsonNode? result = null, JsonRpcError? error = null)
    {
        Id = id;
        Result = result;
        Error = error;
    }

    public static JsonRpcResponse CreateErrorResponse(JsonNode? id, int code, string message) => new(id, null, new JsonRpcError { Code = code, Message = message });
}

public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Data { get; set; }
}
