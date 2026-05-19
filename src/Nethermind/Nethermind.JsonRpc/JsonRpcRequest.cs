// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc
{
    public class JsonRpcRequest
    {
        public string JsonRpc { get; set; }
        public string Method { get; set; }

        public JsonElement Params { get; set; }

        [JsonConverter(typeof(JsonRpcIdConverter))]
        public JsonRpcId Id { get; set; }

        public override string ToString() => $"Id:{Id}, {Method}({Params})";
    }
}
