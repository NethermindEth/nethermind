// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.Evm.Tracing.GethStyle
{
    public class GethTraceOptions
    {
        [JsonProperty("disableStorage")]
        [System.Text.Json.Serialization.JsonPropertyName("disableStorage")]
        public bool DisableStorage { get; set; }

        [JsonProperty("disableMemory")]
        [System.Text.Json.Serialization.JsonPropertyName("disableMemory")]
        public bool DisableMemory { get; set; }

        [JsonProperty("disableStack")]
        [System.Text.Json.Serialization.JsonPropertyName("disableStack")]
        public bool DisableStack { get; set; }

        [JsonProperty("tracer")]
        [System.Text.Json.Serialization.JsonPropertyName("tracer")]
        public string Tracer { get; set; }

        [JsonProperty("timeout")]
        [System.Text.Json.Serialization.JsonPropertyName("timeout")]
        public string Timeout { get; set; }

        public static GethTraceOptions Default = new();
    }
}
