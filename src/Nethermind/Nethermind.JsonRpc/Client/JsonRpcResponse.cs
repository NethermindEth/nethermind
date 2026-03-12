// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Client
{
    public class JsonRpcResponse<T>
    {
        [JsonPropertyOrder(1)]
        public string JsonRpc { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyOrder(2)]
        public T Result { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyOrder(3)]
        public Error Error { get; set; }

        [JsonConverter(typeof(IdConverter))]
        [JsonPropertyOrder(0)]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public object Id { get; set; }
    }
}
