// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class JsonRpcSubscriptionResponse : JsonRpcResponse
    {
        [JsonPropertyName("params")]
        [JsonPropertyOrder(2)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonRpcSubscriptionResult Params { get; set; }

        [JsonPropertyName("method")]
        [JsonPropertyOrder(1)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public new string MethodName => "eth_subscription";

        [JsonPropertyName("id")]
        [JsonConverter(typeof(IdConverter))]
        [JsonPropertyOrder(3)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public new object? Id { get { return base.Id; } set { base.Id = value; } }
    }
}
