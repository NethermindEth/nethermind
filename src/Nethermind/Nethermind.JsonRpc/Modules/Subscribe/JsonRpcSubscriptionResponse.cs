// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class JsonRpcSubscriptionResponse : JsonRpcResponse
    {
        [JsonProperty(PropertyName = "params", Order = 2)]
        public JsonRpcSubscriptionResult Params { get; set; }

        [JsonProperty(PropertyName = "method", Order = 1)]
        public new string MethodName => "eth_subscription";

        [JsonConverter(typeof(IdConverter))]
        [JsonProperty(PropertyName = "id", Order = 3, NullValueHandling = NullValueHandling.Ignore)]
        public new object? Id { get { return base.Id; } set { base.Id = value; } }
    }
}
