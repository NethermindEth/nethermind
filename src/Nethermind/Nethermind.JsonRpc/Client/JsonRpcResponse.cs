// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Client
{
    public class JsonRpcResponse<T>
    {
        [JsonConverter(typeof(IdConverter))]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        [JsonPropertyName("id")]
        public object Id { get; set; }

        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("result")]
        public T Result { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("error")]
        public Error Error { get; set; }
    }
}
