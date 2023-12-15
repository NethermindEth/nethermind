// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class JsonRpcSubscriptionResponse : JsonRpcResponse
    {
        [JsonPropertyName("method")]
        [JsonPropertyOrder(1)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public new static string MethodName => "eth_subscription";

        [JsonPropertyName("params")]
        [JsonPropertyOrder(2)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonRpcSubscriptionResult Params { get; set; }

        [JsonPropertyName("id")]
        [JsonPropertyOrder(3)]
        [JsonConverter(typeof(IdConverter))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public new object? Id { get { return base.Id; } set { base.Id = value; } }
    }
}
