// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.GethStyle
{
    public class GethTraceOptions
    {
        [JsonPropertyName("disableStorage")]
        public bool DisableStorage { get; set; }

        [JsonPropertyName("disableMemory")]
        public bool DisableMemory { get; set; }

        [JsonPropertyName("disableStack")]
        public bool DisableStack { get; set; }

        [JsonPropertyName("tracer")]
        public string Tracer { get; set; }

        [JsonPropertyName("timeout")]
        public string Timeout { get; set; }

        public static GethTraceOptions Default = new();
    }
}
