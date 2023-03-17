// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Client
{
    public class JsonRpcResponse<T>
    {
        [JsonProperty(PropertyName = "jsonrpc", Order = 1)]
        [System.Text.Json.Serialization.JsonPropertyOrder(1)]
        [System.Text.Json.Serialization.JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; }

        [JsonProperty(PropertyName = "result", NullValueHandling = NullValueHandling.Ignore, Order = 2)]
        [System.Text.Json.Serialization.JsonPropertyOrder(2)]
        public T Result { get; set; }

        [JsonProperty(PropertyName = "error", NullValueHandling = NullValueHandling.Ignore, Order = 3)]
        [System.Text.Json.Serialization.JsonPropertyOrder(3)]
        public Error Error { get; set; }

        [JsonConverter(typeof(IdConverter))]
        [System.Text.Json.Serialization.JsonConverter(typeof(IdJsonConverter))]
        [JsonProperty(PropertyName = "id", Order = 0, NullValueHandling = NullValueHandling.Include)]
        [System.Text.Json.Serialization.JsonPropertyOrder(0)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
        public object Id { get; set; }
    }
}
