// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc
{
    public class JsonRpcRequest
    {
        public string JsonRpc { get; set; }
        public string Method { get; set; }

        public JsonElement Params { get; set; }

        [JsonConverter(typeof(IdConverter))]
        public object Id { get; set; }

        public override string ToString()
        {
            return $"Id:{Id}, {Method}({Params})";
        }
    }
}
