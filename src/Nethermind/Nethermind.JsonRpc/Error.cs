// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.JsonRpc
{
    public class Error
    {
        [JsonProperty(PropertyName = "code", Order = 0)]
        public int Code { get; set; }

        [JsonProperty(PropertyName = "message", Order = 1)]
        public string? Message { get; set; }

        [JsonProperty(PropertyName = "data", Order = 2)]
        public object? Data { get; set; }

        [JsonIgnore]
        public bool SuppressWarning { get; set; }
    }
}
