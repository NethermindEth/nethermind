// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Client
{
    public class JsonRpcResponse<T>
    {
        [JsonProperty(PropertyName = "jsonrpc", Order = 1)]
        public string JsonRpc { get; set; }

        [JsonProperty(PropertyName = "result", NullValueHandling = NullValueHandling.Ignore, Order = 2)]
        public T Result { get; set; }

        [JsonProperty(PropertyName = "error", NullValueHandling = NullValueHandling.Ignore, Order = 3)]
        public Error Error { get; set; }

        [JsonConverter(typeof(IdConverter))]
        [JsonProperty(PropertyName = "id", Order = 0, NullValueHandling = NullValueHandling.Include)]
        public object Id { get; set; }
    }
}
