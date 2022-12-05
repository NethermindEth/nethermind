// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc
{
    public class JsonRpcRequest
    {
        public string JsonRpc { get; set; }
        public string Method { get; set; }

        [JsonProperty(Required = Required.Default)]
        public JToken[]? Params { get; set; }

        [JsonConverter(typeof(IdConverter))]
        public object Id { get; set; }

        public override string ToString()
        {
            string paramsString = Params is null ? string.Empty : $"{string.Join(",", Params.Select(p => p.ToString()))}";
            return $"ID {Id}, {Method}({paramsString})";
        }
    }
}
