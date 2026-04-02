// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.EngineApiProxy.Models;

public class JsonRpcRequest
{
    [JsonProperty("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonProperty("method")]
    public string Method { get; set; } = string.Empty;

    [JsonProperty("params")]
    public JArray? Params { get; set; }

    [JsonProperty("id")]
    public object? Id { get; set; }

    [JsonIgnore]
    public Dictionary<string, string>? OriginalHeaders { get; set; }

    public JsonRpcRequest()
    {
    }

    public JsonRpcRequest(string method, JArray? parameters = null, object? id = null)
    {
        Method = method;
        Params = parameters;
        Id = id;
    }

    public override string ToString()
    {
        return $"{{method: {Method}, id: {Id}, params: {Params?.ToString() ?? "null"}}}";
    }
}
