// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc
{
    public class JsonRpcRequest
    {
        public string JsonRpc { get; set; }
        public string Method { get; set; }

        [JsonProperty(Required = Required.Default)]
        public string[]? Params { get; set; }

        [JsonConverter(typeof(IdConverter))]
        [System.Text.Json.Serialization.JsonConverter(typeof(IdJsonConverter))]
        public object Id { get; set; }

        public override string ToString()
        {
            string paramsString = Params is null ? string.Empty : $"{string.Join(",", Params)}";
            return $"ID {Id}, {Method}({paramsString})";
        }
    }
}
