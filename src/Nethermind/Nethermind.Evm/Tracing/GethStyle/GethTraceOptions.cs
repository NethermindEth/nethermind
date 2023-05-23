// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.Evm.Tracing.GethStyle
{
    public class GethTraceOptions
    {
        [JsonProperty("disableStorage")]
        public bool DisableStorage { get; set; }

        [JsonProperty("disableMemory")]
        public bool DisableMemory { get; set; }

        [JsonProperty("disableStack")]
        public bool DisableStack { get; set; }

        [JsonProperty("tracer")]
        public string Tracer { get; set; }

        [JsonProperty("timeout")]
        public string Timeout { get; set; }

        public static GethTraceOptions Default = new();
    }
}
