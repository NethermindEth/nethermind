// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc
{
    public class Error
    {
        [JsonPropertyName("code")]
        [JsonPropertyOrder(0)]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        [JsonPropertyOrder(1)]
        public string? Message { get; set; }

        [JsonPropertyName("data")]
        [JsonPropertyOrder(2)]
        public object? Data { get; set; }

        [JsonIgnore]
        public bool SuppressWarning { get; set; }
    }
}
