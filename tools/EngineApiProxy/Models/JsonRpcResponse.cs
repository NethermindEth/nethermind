// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.EngineApiProxy.Models;

public class JsonRpcResponse
{
    [JsonProperty("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonProperty("id")]
    public object? Id { get; set; }

    [JsonProperty("result", NullValueHandling = NullValueHandling.Include)]
    public object? Result { get; set; }

    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public JsonRpcError? Error { get; set; }

    public JsonRpcResponse()
    {
    }

    public JsonRpcResponse(object? id, object? result = null, JsonRpcError? error = null)
    {
        Id = id;
        Result = result;
        Error = error;
    }

    public static JsonRpcResponse CreateErrorResponse(object? id, int code, string message)
    {
        return new JsonRpcResponse(id, null, new JsonRpcError { Code = code, Message = message });
    }
}

public class JsonRpcError
{
    [JsonProperty("code")]
    public int Code { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
    public object? Data { get; set; }
}
