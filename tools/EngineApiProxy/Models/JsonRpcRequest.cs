// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nethermind.EngineApiProxy.Models;

public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonArray? Params { get; set; }

    [JsonPropertyName("id")]
    public JsonNode? Id { get; set; }

    [JsonIgnore]
    public Dictionary<string, string>? OriginalHeaders { get; set; }

    public JsonRpcRequest()
    {
    }

    public JsonRpcRequest(string method, JsonArray? parameters = null, JsonNode? id = null)
    {
        Method = method;
        Params = parameters;
        Id = id;
    }

    public override string ToString() => $"{{method: {Method}, id: {Id}, params: {Params?.ToJsonString() ?? "null"}}}";
}
